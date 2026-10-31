using System;
using System.Runtime.InteropServices;
using System.Threading;
using TuneLab.ExperimentalPatch.InsertHost;

namespace TuneLab.ExperimentalPatch.VstBridge;

// VST3 插件编辑器的桥侧宿主。**跑在桥进程里**，与 Vst2EditorWindow 同构、同一条纪律。
//
// 【为什么窗口不能开在宿主进程】vendored 的 ownvst3 只支持"插件自己开一扇顶层窗"这一种
// 模式（把宿主 HWND 交给 CreateEditor 时 GUI 从不挂进去，见 Vst3Insert.OpenEditor 的实测
// 记录）。那扇窗属于**创建它的线程**，消息必须由那条线程泵。桥的主线程正阻塞读管道，
// 所以这里单起一条 STA 线程：OpenEditor → 找窗 → 泵消息；关窗时在**同一条线程**上 CloseEditor。
//
// 【为什么要把 HWND 回传宿主】窗口在桥进程里，宿主不能像进程内那样用 SnapshotTopLevelWindows
// 找它。把句柄经协议带回宿主，宿主的 Vst3EditorShell 才能置前 / 判"用户是否已经关掉"。
//
// 【与 VST2 的区别】VST2 的窗口是桥**自己建的**、插件挂进去；VST3 的是插件自己建的，
// 我们只是找到它的句柄。所以这里没有 effEditOpen/effEditIdle，只有 SafeDispatchMessage
// 驱动插件自己的队列（VSTGUI/JUCE 那类不泵不画第一帧）。
internal sealed class Vst3EditorWindow : IDisposable
{
    readonly Vst3Insert mInsert;

    Thread? mThread;
    IntPtr mHwnd;
    volatile bool mClosed;
    volatile bool mExited;
    readonly ManualResetEventSlim mReady = new(false);
    bool mOpened;

    // 插件那扇窗的 HWND（桥进程内）。宿主用它做置前 / 可见性查询。
    public IntPtr Handle => mHwnd;

    // 编辑器线程还活着吗？用户点 ✕ 之后窗口没了、泵也退了，宿主那边还拿着这个对象 ——
    // 不判这一下，下次点「界面」会往一个已销毁的 HWND 上置前（"关掉后再点没反应"）。
    public bool IsAlive => !mExited;

    // 与 VST2 的回执形状保持一致。VST3 的 GUI 不在"我们的窗口"里，恒为 0。
    public int ChildWindowCount => 0;

    Vst3EditorWindow(Vst3Insert insert)
    {
        mInsert = insert;
    }

    // 从桥的主线程调：起编辑器线程并等它把窗口开出来。
    // 返回 false = 插件没有界面 / CreateEditor 失败 / 超时（线程已收拾干净）。
    public static bool TryOpen(Vst3Insert insert, out Vst3EditorWindow? window)
    {
        var candidate = new Vst3EditorWindow(insert);

        if (!candidate.Start())
        {
            candidate.Dispose();
            window = null;
            return false;
        }

        window = candidate;
        return true;
    }

    bool Start()
    {
        mThread = new Thread(Run)
        {
            IsBackground = true,
            Name = "vst3-editor",
        };

        try
        {
            // VSTGUI / JUCE 那类编辑器常假设 UI 线程是 STA。
            mThread.SetApartmentState(ApartmentState.STA);
        }
        catch (Exception)
        {
            // 平台不支持 apartment：照跑，多数插件不依赖。
        }

        mThread.Start();

        // 宿主侧 OpenEditorBlocking 有超时；桥自己也不能无限等：等满就放弃，
        // 后台线程留给它自己收拾（插件卡在里面也不挡住桥的卸载）。
        return mReady.Wait(StartTimeout) && mOpened;
    }

    // 建 VST3 view 可能要几百毫秒到数秒（重型插件的 GUI 初始化）。
    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

    // 从桥的主线程调：已经开着就带到前台。
    public void BringToFront()
    {
        IntPtr hwnd = mHwnd;
        if (hwnd != IntPtr.Zero)
            Vst3Insert.BringToFront(hwnd);
    }

    public void Dispose()
    {
        if (mClosed)
            return;

        mClosed = true;

        // 【必须等它退完】编辑器线程还要在**同一条线程**上 CloseEditor（native 的约定）。
        // 不等就返回，桥会立刻 Unload → insert.Dispose()，而插件代码还在那条线程上跑 ——
        // 与 VST2 那条路一样，结果是卸载时 AccessViolation。
        try
        {
            mThread?.Join(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // 线程已经没了 / 不能 Join：继续，后面只是不再用它。
        }
    }

    // ── 编辑器线程 ──

    void Run()
    {
        try
        {
            // 打开前的顶层窗集合：打开后取差集就是插件那扇窗。
            var before = mInsert.SnapshotTopLevelWindows();

            bool opened;
            try
            {
                // 传 0：这个 native 库只支持"插件自己开顶层窗"（见文件头）。
                opened = mInsert.OpenEditor(IntPtr.Zero);
            }
            catch (Exception)
            {
                opened = false;
            }

            if (opened && mInsert.IsEditorOpen)
            {
                // 插件窗口可能比 CreateEditor 晚几十毫秒才出现：轮询几次再放弃。
                for (int attempt = 0; attempt < 40 && mHwnd == IntPtr.Zero && !mClosed; attempt++)
                {
                    mHwnd = mInsert.FindEditorWindowSince(before);

                    if (mHwnd == IntPtr.Zero)
                        Thread.Sleep(25);
                }

                mOpened = mInsert.IsEditorOpen;
            }
        }
        catch (Exception)
        {
            mOpened = false;
        }

        mReady.Set();

        if (!mOpened)
        {
            mExited = true;
            return;
        }

        // 置顶 + 前台。宿主那边已经 AllowSetForegroundWindow 授权过，这里的
        // SetForegroundWindow 才会生效（否则 Windows 静默忽略，窗口藏在宿主后面）。
        if (mHwnd != IntPtr.Zero)
            Vst3Insert.BringToFront(mHwnd);

        // 消息泵：插件窗口属于这条线程，消息得由它派发。SafeDispatchMessage 负责插件
        // 自己的队列，PeekMessage 循环补上窗口消息 / 键盘（TranslateMessage）。
        while (!mClosed)
        {
            bool alive;
            try
            {
                alive = mInsert.IsEditorOpen;
            }
            catch (Exception)
            {
                alive = false;
            }

            if (!alive)
                break;   // 用户把插件窗口关掉了

            try
            {
                mInsert.PumpEditorMessages();   // → native SafeDispatchMessage
                DrainMessages();
            }
            catch (Exception)
            {
                break;
            }

            Thread.Sleep(15);
        }

        // 摘掉插件的 view —— 必须在创建它的这条线程上（native 的约定）。
        try
        {
            mInsert.CloseEditor();
        }
        catch (Exception)
        {
            // 插件已经不在了：没有可做的。
        }

        mExited = true;
    }

    void DrainMessages()
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    // ── Win32 ──

    const uint PmRemove = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("user32.dll")]
    static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DispatchMessage(ref MSG lpMsg);
}
