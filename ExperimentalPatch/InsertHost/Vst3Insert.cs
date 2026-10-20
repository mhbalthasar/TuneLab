using System;
using System.Runtime.InteropServices;
using OwnVST3Host;
using TuneLab.ExperimentalPatch.MixerEngine;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 一个已装好的 VST3 插件实例：**音频线程**上跑的那一半。
//
// 【实时纪律】（DESIGN.md §5.7）
//   · Process 里不分配：输入/输出平面数组与缓冲在构造时一次备齐，块长固定。
//   · 不加锁：旁通、参数都只在 UI 线程改；进音频线程的都是构造时定死的东西。
//   · 不抛异常：插件自己失效时返回失败让宿主决定，不往音频回调里抛。
//   · 过程是**平面**的（VST3 要分离声道），而引擎给的是**交织**的；这里做转换。
//     转换按块做、用预分配的数组 —— 这是每块一次的小常数开销，没有隐藏分配。
//
// 【位数】插件是 32 位而我们（现在）是 64 位进程时，LoadPlugin 会失败，
// 后端已经拦下来了。这里假定 wrapper 已经 Initialize 成功。
public sealed unsafe class Vst3Insert : IMixInsert, IDisposable
{
    readonly OwnVst3Wrapper mWrapper;
    readonly float[][] mInPlanes;   // 平面，每块一份
    readonly float[][] mOutPlanes;
    readonly int mChannels;
    readonly int mBlockSize;
    volatile bool mBypassed;
    bool mDisposed;
    bool mEditorOpen;
    bool mNeedsPump;

    public Vst3Insert(InsertPluginInfo plugin, OwnVst3Wrapper wrapper, double sampleRate, int blockSize, bool bypassed)
    {
        Plugin = plugin;
        Name = plugin.Name;
        mWrapper = wrapper;
        mBlockSize = blockSize;
        mBypassed = bypassed;

        // 声道数：以较宽的那个总线为准（与 wrapper 内部 ProcessAudio 的选择同口径）。
        // 我们这里固定成立体声进/出 —— 单声道插件也会被 wrapper 喂两份。
        mChannels = Math.Max(2, Math.Max(wrapper.ActualInputChannels, wrapper.ActualOutputChannels));

        mInPlanes = new float[mChannels][];
        mOutPlanes = new float[mChannels][];
        for (int ch = 0; ch < mChannels; ch++)
        {
            mInPlanes[ch] = new float[blockSize];
            mOutPlanes[ch] = new float[blockSize];
        }

        LatencySamples = Math.Max(0, wrapper.LatencySamples);
    }

    public string Name { get; }

    public InsertPluginInfo Plugin { get; }

    public bool IsBypassed => mBypassed;

    // VST3 的延迟是常量（构造后不变），所以直接读 wrapper 当时报的值。
    public int LatencySamples { get; }

    // 只在 UI 线程调。
    public void SetBypassed(bool bypassed) => mBypassed = bypassed;

    public void Process(Span<float> buffer, int offset, int count)
    {
        if (mDisposed || mBypassed || count <= 0)
            return;

        // 整块都要过插件：逐段（每段 ≤ mBlockSize，也就是插件被 Initialize 时声明的块长）处理。
        // 早先是 `n = Math.Min(count, mBlockSize)` 就返回 —— 块比声明值大时**后半块原样直通**，
        // 每 10ms 一次硬接缝，听感就是爆音。声明块长现在与引擎一致，这个循环是第二道保险。
        for (int done = 0; done < count; done += mBlockSize)
        {
            int n = Math.Min(mBlockSize, count - done);

            if (!ProcessChunk(buffer, offset + done * 2, n))
                return;   // 插件失效：剩下的部分保持直通
        }
    }

    bool ProcessChunk(Span<float> buffer, int offset, int n)
    {
        // 交织 → 平面。
        for (int ch = 0; ch < mChannels; ch++)
        {
            var plane = mInPlanes[ch];
            for (int i = 0; i < n; i++)
                plane[i] = buffer[offset + i * 2 + (ch % 2)];
        }

        // 清空输出平面：插件未必每个声道都写（M→S 的插件可能只写一边）。
        for (int ch = 0; ch < mChannels; ch++)
            Array.Clear(mOutPlanes[ch], 0, n);

        bool ok;
        try
        {
            ok = mWrapper.ProcessAudio(mInPlanes, mOutPlanes, mChannels, n);
        }
        catch (Exception)
        {
            // 插件炸了：这一块直通（不动 buffer），并记住它坏了。
            ok = false;
        }

        if (!ok)
        {
            // 失败 = 保持原样（直通）。宿主可以在上层看到持续失败后决定摘掉这个插件。
            return false;
        }

        // 平面 → 交织（取前两个声道，多余的声道不往立体声总线里塞）。
        for (int i = 0; i < n; i++)
        {
            float left = mOutPlanes[0][i];
            float right = mChannels > 1 ? mOutPlanes[1][i] : mOutPlanes[0][i];

            // NaN / ±Inf 就地清零：这类值一旦进了总线，整条链（含限幅器 —— NaN 的比较恒为 false）
            // 都拦不住，最终送到声卡就是一串爆音或整段静音。代价是一帧两次比较。
            buffer[offset + i * 2] = float.IsFinite(left) ? left : 0f;
            buffer[offset + i * 2 + 1] = float.IsFinite(right) ? right : 0f;
        }

        return true;
    }

    // ── 插件自身的状态与参数（全部只在 UI 线程用）──

    public string? Vendor => mWrapper.Vendor;
    public string? Version => mWrapper.Version;
    public bool HasEditor => mWrapper.HasEditor;

    // 插件自己报的首选尺寸（像素）。拿不到就返回 null，让宿主用默认大小。
    public (int Width, int Height)? EditorSize
    {
        get
        {
            try
            {
                var size = mWrapper.GetEditorSize();
                return size == null ? null : (size.Value.Width, size.Value.Height);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    // 打开插件编辑器：**窗口由插件/native 库自己建**，我们不提供宿主窗口。
    //
    // 【为什么改成这样】实测（探针 5/6/7）：把 Avalonia 窗口的平台句柄交给
    // CreateEditor，插件会返回成功、IsEditorOpen 也是 true、用户能瞥见 GUI 一闪 ——
    // 但 GUI 从来不挂进我们给的 HWND（窗口树下 0 个子窗口，STA 同线程也一样）。
    // 也就是说这个 vendored 的 ownvst3 只支持"自己开一扇顶层窗"这一种模式。
    // 硬要塞宿主窗口，结果是我们的窗口永远空白、还要额外做消息泵。
    //
    // 代价：编辑器窗口是独立的一扇，不带在混音台/宿主窗口里。这是这个 native 库
    // 的能力边界，不是我们的选择。换来的是 GUI 真的能显示、能交互。
    //
    // parent 参数保留（签名不动，调用方不必改），但一律按 0 传：
    // 不支持的路径留着只会让人以为它work。
    public bool OpenEditor(IntPtr parent)
    {
        if (mDisposed)
            return false;

        if (mEditorOpen)
            return true;

        try
        {
            mEditorOpen = mWrapper.CreateEditor(IntPtr.Zero);

            // 插件把 GUI 挂在自己的消息队列上：不泵就不画。定时器由宿主编辑器壳驱动。
            mNeedsPump = mEditorOpen;
            return mEditorOpen;
        }
        catch (Exception)
        {
            mEditorOpen = false;
            mNeedsPump = false;
            return false;
        }
    }

    // 泵插件自己的消息队列。对不需要它的插件是空操作（native 侧判断）。
    public void PumpEditorMessages()
    {
        if (!mEditorOpen)
            return;

        try
        {
            mWrapper.SafeDispatchMessage();
        }
        catch (Exception)
        {
            // 插件已经不在了：窗口跟着关，这里不该再把异常抛到定时器上。
            mNeedsPump = false;
        }
    }

    // 插件自己那扇窗是否还开着。**native 库自己的判断**，比数我们的窗口可靠 ——
    // 实测数窗口那条恒为 0 且会误导（GUI 一直是有的，只是不挂在我们给的 HWND 上）。
    public bool IsEditorOpen
    {
        get
        {
            if (!mEditorOpen)
                return false;

            try
            {
                return mWrapper.IsEditorOpen;
            }
            catch (Exception)
            {
                // 插件已经没了：按"没开着"处理，让上层的定时器停。
                return false;
            }
        }
    }

    /// <summary>
    /// 我们给的宿主窗口里挂了多少个子窗口。**已废弃的检测**：插件自己开顶层窗时
    /// 这里永远是 0，那并不代表"没有界面"（实测误导过一整轮）。留着只为兼容旧调用方。
    /// </summary>
    public int ChildWindowCount(IntPtr parent)
    {
        if (parent == IntPtr.Zero)
            return 0;

        // 走链表而不是 EnumChildWindows 的回调：托管进程里那个回调实测一个都不触发
        // （连自己那个活窗口都数出 0，害我把"探测方法坏了"误判成"所有插件都没界面"）。
        // FindWindowEx 遍历兄弟链，不依赖回调。
        int count = 0;

        IntPtr child = FindWindowEx(parent, IntPtr.Zero, null, null);
        while (child != IntPtr.Zero)
        {
            count++;
            child = FindWindowEx(parent, child, null, null);
        }

        return count;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    public void CloseEditor()
    {
        if (!mEditorOpen)
            return;

        mEditorOpen = false;
        mNeedsPump = false;

        try
        {
            mWrapper.CloseEditor();
        }
        catch (Exception)
        {
            // 插件在关窗时抛错：句柄马上要跟着 wrapper 一起没，吞掉。
        }
    }

    // 窗口尺寸变了：把客户区告诉插件（IPlugView::onSize）。
    public void ResizeEditor(int width, int height)
    {
        if (!mEditorOpen || width <= 0 || height <= 0)
            return;

        try
        {
            mWrapper.ResizeEditor(width, height);
        }
        catch (Exception)
        {
        }
    }

    public IReadOnlyList<VST3Parameter> Parameters => mWrapper.GetAllParameters();

    public double GetParameter(int id) => mWrapper.GetParameter(id);

    public void SetParameter(int id, double value)
    {
        try
        {
            mWrapper.SetParameter(id, value);
        }
        catch (Exception)
        {
            // 插件已经不在了：参数写了也没用，别让 UI 崩。
        }
    }

    // 插件的完整状态块（DESIGN.md §5.6：VST3 = IComponent::getState / setState）。
    public byte[]? SaveState()
    {
        try
        {
            return mWrapper.GetState();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool LoadState(byte[] data)
    {
        if (data == null || data.Length == 0)
            return false;

        try
        {
            return mWrapper.SetState(data);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (mDisposed)
            return;

        mDisposed = true;

        // 先摘编辑器再销毁插件：反过来的话，窗口里挂着的 view 会指向已经释放的组件。
        CloseEditor();

        try
        {
            mWrapper.Dispose();
        }
        catch (Exception)
        {
            // 卸载阶段插件抛错：记不了日志（这里没有宿主日志），只能吞掉。
        }
    }
}




