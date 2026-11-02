using System;
using System.Collections.Generic;
using System.Linq;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.ExperimentalPatch.VstBridge;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 一个跑在**桥进程**里的 VST3 插件实例：宿主侧的 IParameterInsert 实现。
//
// 【它在整条链里的位置】与 Vst2BridgeInsert 完全同构：宿主音频线程把交织块翻成平面、
// 用共享环发一条 process 指令；桥那边真正的插件崩 / 挂只死桥，宿主继续跑。
// 区别只在插件格式：VST2 走 AEffect，VST3 走桥侧的 Vst3PluginHost（它复用 Vst3Insert）。
//
// 【为什么 VST3 也要隔离】实测（NOTES-vst3-vendor.md）：chowdsp 的 CHOWTapeModel /
// ChowMatrix 在 Create 里直接卡死，BYOD / ChowCentaur 建第二个实例时卡死；还有 Blue Cat
// 历史性的 native abort。进程内遇到这些 = 整个 TuneLab 没了，连工程都没得救。
// 过桥之后，超时（P4）一杀桥，宿主只是"这个插入旁通了"。
//
// 【实时纪律】与 Vst2BridgeInsert 相同：Process 里不分配、不加锁、不抛异常。
public sealed class Vst3BridgeInsert : IMixInsert, IParameterInsert, IRuntimeInsertFailure, IDisposable
{
    readonly IBridgeChannel mChannel;
    readonly int mSlotId;
    readonly int mInputChannels;
    readonly int mSidechainChannels;
    readonly int mBlockSize;
    readonly float[][] mInPlanes;
    readonly float[][] mOutPlanes;
    readonly bool mNeedsOpenGl;

    volatile bool mBypassed;
    bool mDisposed;

    Vst3BridgeInsert(InsertPluginInfo plugin, IBridgeChannel channel, int slotId, int blockSize,
        int latencySamples, bool hasEditor, bool needsOpenGl, bool bypassed, int inputChannels)
    {
        Plugin = plugin;
        Name = plugin.Name;

        mChannel = channel;
        mSlotId = slotId;
        mBlockSize = Math.Max(blockSize, 32);
        HasEditor = hasEditor;
        mNeedsOpenGl = needsOpenGl;

        mInputChannels = Math.Max(0, inputChannels);
        mSidechainChannels = Math.Clamp(mInputChannels - 2, 0, 2);

        // 平面缓冲按共享环的最大声道数（4）分配，与 Vst2BridgeInsert 同口径。
        const int maxChannels = SharedAudioLayout.MaxChannels;

        mInPlanes = new float[maxChannels][];
        mOutPlanes = new float[maxChannels][];
        for (int ch = 0; ch < maxChannels; ch++)
        {
            mInPlanes[ch] = new float[mBlockSize];
            mOutPlanes[ch] = new float[mBlockSize];
        }

        LatencySamples = Math.Max(0, latencySamples);
        mBypassed = bypassed;
    }

    public string Name { get; }

    public InsertPluginInfo Plugin { get; }

    public bool IsBypassed => mBypassed;

    public int LatencySamples { get; }

    public int InputChannels => mInputChannels;

    public int SidechainChannels => mSidechainChannels;

    // P2：桥侧开插件 GUI（见文件头）。HasEditor 来自插件自报；开不出时宿主会退参数面板。
    public bool HasEditor { get; }

    public bool NeedsOpenGl => mNeedsOpenGl;

    public (int Width, int Height)? EditorSize => null;

    public void SetBypassed(bool bypassed) => mBypassed = bypassed;

    // [P4] 运行期音频挂起：插件在桥的 process 里卡死 → SendProcess 连续失败到阈值，
    // 桥的音频路停用（见 VstBridgeClient.AudioStalled）。实例本身还活着（还能改旁通 /
    // 移除），但插件已经不工作了 —— 界面据此把这一行标红。
    public string? RuntimeFailureReason
        => mChannel.AudioStalled ? IRuntimeInsertFailure.AudioStallReason : null;

    public void Process(Span<float> buffer, int offset, int count)
        => Process(buffer, offset, count, default);

    public void Process(Span<float> buffer, int offset, int count, ReadOnlySpan<float> sidechain)
    {
        if (mDisposed || mBypassed || count <= 0)
            return;

        int channels = sidechain.IsEmpty || mSidechainChannels == 0 ? 2 : 2 + mSidechainChannels;

        for (int done = 0; done < count; done += mBlockSize)
        {
            int n = Math.Min(mBlockSize, count - done);

            if (!ProcessChunk(buffer, offset + done * 2, n, sidechain, done, channels))
                return;   // 桥超时 / 死了：剩下的部分保持直通
        }
    }

    bool ProcessChunk(Span<float> buffer, int offset, int n,
        ReadOnlySpan<float> sidechain, int sidechainOffset, int channels)
    {
        // 交织 → 平面（与 Vst2BridgeInsert 逐位相同的填充公式）。
        for (int ch = 0; ch < channels; ch++)
        {
            var plane = mInPlanes[ch];

            if (ch < 2)
            {
                for (int i = 0; i < n; i++)
                    plane[i] = buffer[offset + i * 2 + ch];

                continue;
            }

            if (mSidechainChannels == 1)
            {
                for (int i = 0; i < n; i++)
                {
                    int frame = sidechainOffset + i;
                    plane[i] = frame < sidechain.Length / 2
                        ? (sidechain[frame * 2] + sidechain[frame * 2 + 1]) * 0.5f
                        : 0f;
                }

                continue;
            }

            int source = ch - 2;
            for (int i = 0; i < n; i++)
            {
                int frame = sidechainOffset + i;
                plane[i] = frame < sidechain.Length / 2 ? sidechain[frame * 2 + source] : 0f;
            }
        }

        for (int ch = 0; ch < channels; ch++)
            Array.Clear(mOutPlanes[ch], 0, n);

        bool ok;
        try
        {
            ok = mChannel.SendProcess(mSlotId, mInPlanes, mOutPlanes, channels, n, bypass: false);
        }
        catch (Exception)
        {
            ok = false;
        }

        if (!ok)
            return false;

        for (int i = 0; i < n; i++)
        {
            float left = mOutPlanes[0][i];
            float right = channels > 1 ? mOutPlanes[1][i] : mOutPlanes[0][i];

            buffer[offset + i * 2] = float.IsFinite(left) ? left : 0f;
            buffer[offset + i * 2 + 1] = float.IsFinite(right) ? right : 0f;
        }

        return true;
    }

    // ── 状态与参数（只在 UI 线程用）──

    public byte[]? SaveState() => mChannel.GetStateBlocking(mSlotId);

    public bool LoadState(byte[] data) => data.Length > 0 && mChannel.SetStateBlocking(mSlotId, data);

    public IReadOnlyList<InsertParameter> Parameters
    {
        get
        {
            var raw = mChannel.GetParametersBlocking(mSlotId);
            if (raw == null)
                return [];

            return raw.Select(p => new InsertParameter
            {
                Id = (int)p.Id,
                Name = p.Name,
                MinValue = p.Min,
                MaxValue = p.Max,
                DefaultValue = p.DefaultValue,
                CurrentValue = p.Value,
            }).ToList();
        }
    }

    public double GetParameter(int id) => mChannel.GetParametersBlocking(mSlotId)?
        .FirstOrDefault(p => p.Id == id)?.Value ?? 0;

    public void SetParameter(int id, double value) => mChannel.SetParameterBlocking(mSlotId, id, value);

    // 停止播放时把参数改动冲进处理器：发一小块静音过插件，桥那边会跑一次 process()。
    // 只在 UI 线程、且不在播放时调（父类契约如此），所以直接复用一个栈上的静音块即可。
    public void FlushParameterChanges()
    {
        if (mDisposed || mBypassed)
            return;

        const int frames = 512;
        var silence = new float[frames * 2];
        Process(silence, 0, frames);
    }

    // ── 编辑器（P2：桥侧开窗 + 回传 HWND）──
    //
    // 插件窗口在**桥进程**里，本类只负责把"开 / 关 / 查"翻成桥消息。桥那边带一条 STA 线程
    // 开窗 + 泵消息（Vst3EditorWindow）；宿主拿回传的 HWND 做置前，并用跨进程的 IsWindow
    // 判断"用户是不是已经把它关掉了"。

    IntPtr mEditorWindow;
    volatile bool mEditorOpen;

    public bool OpenEditor(IntPtr parent)
    {
        if (mDisposed)
            return false;

        // 已经开着：桥侧新的 OpenEditor 会走"带到前台"分支（不重建，保留窗口里的临时状态）。
        if (mEditorOpen && (!OperatingSystem.IsWindows() || mEditorWindow == IntPtr.Zero || IsWindow(mEditorWindow)))
            return true;

        var (ok, _, _, window) = mChannel.OpenEditorBlocking(mSlotId, Name);
        if (!ok)
        {
            mEditorOpen = false;
            mEditorWindow = IntPtr.Zero;
            return false;
        }

        mEditorWindow = window == 0 ? IntPtr.Zero : new IntPtr(window);
        mEditorOpen = true;
        return true;
    }

    public bool IsEditorOpen
    {
        get
        {
            if (!mEditorOpen)
                return false;

            // 窗口在桥进程里：跨进程的 IsWindow 能查到它是否还存在。用户点 ✕ 之后窗口销毁
            // → false，宿主的编辑器壳据此注销自己（"关掉后再点没反应"的根治）。
            if (OperatingSystem.IsWindows() && mEditorWindow != IntPtr.Zero && !IsWindow(mEditorWindow))
            {
                mEditorOpen = false;
                return false;
            }

            return true;
        }
    }

    // 桥有自己的 STA 线程在泵插件消息（Vst3EditorWindow），宿主这边不需要泵。
    public void PumpEditorMessages()
    {
    }

    public void CloseEditor()
    {
        if (!mEditorOpen && mEditorWindow == IntPtr.Zero)
            return;

        mChannel.CloseEditorBlocking(mSlotId);
        mEditorOpen = false;
        mEditorWindow = IntPtr.Zero;
    }

    // VST3 的窗口由插件自己管尺寸；宿主不 resize（与进程内那条路一致）。
    public void ResizeEditor(int width, int height)
    {
    }

    // 窗口不在本进程：没有本地顶层窗可枚举。
    public HashSet<IntPtr> SnapshotTopLevelWindows() => [];

    // 桥模式下窗口句柄由桥回传，直接交给编辑器壳做置前（壳拿不到本地句柄）。
    public IntPtr FindEditorWindowSince(HashSet<IntPtr> before) => mEditorWindow;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hWnd);

    // 桥进程的 stderr（最近几十行）。加载 / 编辑器失败时带上它。
    public string? BridgeError => mChannel.LastBridgeError;

    public void Dispose()
    {
        if (mDisposed)
            return;

        mDisposed = true;

        try
        {
            mChannel.UnloadBlocking(mSlotId);
        }
        catch (Exception)
        {
            // 卸载阶段桥已经死了：没有可做的补救，别往 UI 线程抛。
        }
    }

    // ── 建实例 ──

    // 隔离在当前平台是否**原则上**被支持：64 位 + Windows / Linux / macOS。
    // 具体能不能真用，还要看桥 exe 与 native 是否就位 —— EnsureStartedBlocking / BridgeFormats
    // 会如实回报（缺了就报 bridgeUnavailable，调用方原样报错，不再有进程内可选）。
    public static bool PlatformSupported =>
        Environment.Is64BitProcess &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

    // 本机**静态**上能不能跑 VST3：平台 + 64 位 + 64 位桥的可执行文件就位。
    //
    // 【为什么只查文件】设置页渲染时会调它，不能为了画一行状态去起一个进程。
    //
    // 【它现在就是"VST3 能不能用"】进程内那条路已经删掉了（VST3 一律隔离），
    // 所以这个判断直接决定 FX 菜单里 VST3 那一栏是不是灰的。
    public static bool IsolationAvailable =>
        PlatformSupported && VstBridgeClient.BridgeExecutableExists;

    /// <summary>IsolationAvailable 为 false 时的短原因（进设置页状态行与菜单）。</summary>
    public static string? IsolationUnavailableReason => PlatformSupported
        ? "the 64-bit bridge executable is not installed beside the application"
        : "the isolated VST3 host requires a 64-bit Windows, Linux or macOS host";

    /// <summary>
    /// 把 VST3 装进 64 位桥。失败返回 null 并写 reason。
    /// bridgeUnavailable = 失败在"桥这一侧"（没起来 / 不带 vst3）；false = 桥是好的，
    /// 是插件自己拒绝 / 加载时把桥弄没了。两种都只是"这个插入装不进来"，不再有回退。
    /// 只在 UI 线程调（会同步等桥的回复）。
    /// </summary>
    public static Vst3BridgeInsert? Create(InsertPluginInfo plugin, InsertSettings settings,
        out string? reason, out bool bridgeUnavailable)
    {
        reason = null;
        bridgeUnavailable = false;

        // 32 位宿主 / 未知平台没有可用的桥（VST3 锁 64 位，见 VST3-BRIDGE.md §9.1）。
        if (!PlatformSupported)
        {
            reason = "the isolated VST3 host requires a 64-bit Windows, Linux or macOS host";
            bridgeUnavailable = true;
            return null;
        }

        int blockSize = Math.Clamp(settings.MaxBlockSize, 32, SharedAudioLayout.MaxBlockFrames);

        var client = VstBridgeClient.ForBitness(64);
        client.ClearBridgeError();

        if (!client.EnsureStartedBlocking())
        {
            reason = "the 64-bit bridge could not be started";
            if (client.LastBridgeError is { Length: > 0 } detail)
                reason += " — " + detail;

            bridgeUnavailable = true;
            return null;
        }

        // 桥自报支持 vst3 才继续：旧桥 / 缺 native 的桥只声明 vst2。
        if (client.BridgeFormats is { } formats
            && !formats.Contains(PluginFormats.Vst3, StringComparer.OrdinalIgnoreCase))
        {
            reason = "the 64-bit bridge does not carry VST3";
            bridgeUnavailable = true;
            return null;
        }

        // ── 从这条线以下：桥是好的，失败都算插件的（不回退进程内）──

        int slotId = client.AllocateSlot();
        var loaded = client.LoadBlocking(slotId, plugin.Locator, settings.SampleRate, blockSize,
            settings.Bypass, format: PluginFormats.Vst3);

        if (loaded == null)
        {
            // 加载超时（插件卡在 Create）会走到这里：LastBridgeError 里带着"没在 Ns 内回、桥已杀"。
            reason = "the 64-bit bridge did not answer the load request";
            if (client.LastBridgeError is { Length: > 0 } detail)
                reason += " — " + detail;

            return null;
        }

        if (!loaded.Ok)
        {
            reason = loaded.Reason ?? "the plugin refused to load";
            return null;
        }

        if (loaded.IsSynth)
        {
            client.UnloadBlocking(slotId);
            reason = "this plugin is an instrument; inserts need an effect";
            return null;
        }

        if (loaded.InputChannels <= 0 || loaded.OutputChannels <= 0)
        {
            client.UnloadBlocking(slotId);
            reason = "the plugin has no audio channels (it is a MIDI utility, not an effect)";
            return null;
        }

        return new Vst3BridgeInsert(plugin, client, slotId, blockSize,
            loaded.LatencySamples, loaded.HasEditor, loaded.NeedsOpenGl, settings.Bypass, loaded.InputChannels);
    }

    // 测试用的入口：直接注入一个假的桥通道，不碰 VstBridgeClient。
    internal static Vst3BridgeInsert CreateForTest(InsertPluginInfo plugin, IBridgeChannel channel, int slotId,
        int blockSize, int latencySamples, bool hasEditor, bool needsOpenGl, bool bypassed, int inputChannels)
        => new(plugin, channel, slotId, blockSize, latencySamples, hasEditor, needsOpenGl, bypassed, inputChannels);
}
