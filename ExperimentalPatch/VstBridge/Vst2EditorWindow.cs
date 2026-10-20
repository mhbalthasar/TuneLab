using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TuneLab.ExperimentalPatch.VstBridge;

// VST2 的 dispatcher 函数指针类型（cdecl）。PluginHost 与编辑器窗口共用同一个声明：
// 两边必须传同一个原生指针，各写一份迟早漂移。PluginHost 里有完整的 ABI 注记。
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr Vst2DispatcherProc(IntPtr effect, int opcode, int index,
    IntPtr value, IntPtr ptr, float opt);

// VST2 插件编辑器的宿主窗口。**跑在桥进程里**。
//
// 【为什么窗口不能开在宿主进程】VST2 的编辑器是插件在自己的 dll 里创建的原生子窗口，
// 它属于**加载插件的那条进程**：跨进程 SetParent 在 Windows 上不可靠（菜单、焦点、
// 拖放、DPI 全都会错），所以谁加载插件谁开窗。桥加载插件，于是桥开窗 ——
// 这也是 DESIGN.md §5.5 里"桥自己开窗 + 主进程激活"那条。
//
// 【线程】VST2 的编辑器必须在**同一条线程**上 open / idle / close（插件常在那里建消息循环
// 与定时器）。所以这里单起一条 STA 线程，创建窗口 → effEditOpen → 消息循环；
// 音频的 process 仍走桥的主线程，两边互不阻塞（这正是 VST2 允许的用法）。
//
// 【窗口关闭】插件可能给窗口装子类（子类会转发未处理消息，所以 WM_CLOSE 能到我们手里）。
// 关窗路径统一走 PostMessage(WM_CLOSE)：即使消息被插件的子类吞了，宿主这边也只是"窗口
// 没关掉"，不会把桥弄死；下次打开会复用同一个窗口。
internal sealed class Vst2EditorWindow : IDisposable
{
    // ── VST2 opcode（与 PluginHost 里同一套编号）──
    const int EffEditGetRect = 13;
    const int EffEditOpen = 14;
    const int EffEditClose = 15;
    const int EffEditIdle = 19;

    const int WmDestroy = 0x0002;
    const int WmClose = 0x0010;
    const int WmTimer = 0x0113;
    const int WmApp = 0x8000;
    const int WmBringToFront = WmApp + 1;

    const int SwShow = 5;
    const int SwRestore = 9;

    const uint SwpNoMove = 0x0002;
    const uint SwpNoZOrder = 0x0004;
    const uint SwpNoActivate = 0x0010;

    readonly IntPtr mEffect;
    readonly Vst2DispatcherProc mDispatcher;
    readonly string mTitle;

    Thread? mThread;
    IntPtr mHwnd;
    volatile bool mClosed;
    readonly ManualResetEventSlim mReady = new(false);
    bool mOpened;

    // 必须在 UI 线程上调用（和 open / idle / close 同一条线程）——所以它只由 Run 用。
    Vst2EditorWindow(IntPtr effect, Vst2DispatcherProc dispatcher, string title)
    {
        mEffect = effect;
        mDispatcher = dispatcher;
        mTitle = title;
    }

    // 从桥的主线程调：起编辑器线程并等它把窗口开出来。
    // 返回 false = 插件没有界面 / effEditOpen 失败 / 超时（窗口已经收拾干净）。
    public static bool TryOpen(IntPtr effect, Vst2DispatcherProc dispatcher, string title, out Vst2EditorWindow? window)
    {
        var candidate = new Vst2EditorWindow(effect, dispatcher, title);

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
            Name = "vst2-editor",
        };

        try
        {
            // VST2 插件（尤其用 VSTGUI / MFC 的老插件）常假设 UI 线程是 STA。
            mThread.SetApartmentState(ApartmentState.STA);
        }
        catch (Exception)
        {
            // 非 Windows / 平台不支持：照跑，多数插件不依赖 apartment。
        }

        mThread.Start();

        // 等 effEditOpen 返回。插件初始化重时可能几百毫秒，15 秒还没好就当它坏了。
        return mReady.Wait(TimeSpan.FromSeconds(15)) && mOpened;
    }

    // 从桥的主线程调：把窗口带到前面（已经打开时）或请求关闭。
    public void BringToFront()
    {
        IntPtr hwnd = mHwnd;
        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
        }
    }

    public void Dispose()
    {
        if (mClosed)
            return;

        mClosed = true;

        IntPtr hwnd = mHwnd;
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);

        // 编辑器线程是后台线程：插件卡在消息循环里也不会挡住桥退出。
    }

    // ── 编辑器线程 ──

    void Run()
    {
        string className = "TuneLabVst2Editor_" + Guid.NewGuid().ToString("N");

        WndProcDelegate wndProc = WindowProc;

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512),   // IDC_ARROW
            lpszClassName = className,
        };

        if (RegisterClassEx(ref wndClass) == 0)
        {
            mReady.Set();
            return;
        }

        // 窗口大小：先问插件（effEditGetRect 允许在 effEditOpen 之前调），拿不到用 640×480。
        if (!TryGetEditorSize(out int width, out int height))
        {
            width = 640;
            height = 480;
        }

        IntPtr hwnd = CreateWindowEx(0, className, mTitle,
            WsOverlappedWindow, 80, 80, width, height,
            IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            mReady.Set();
            return;
        }

        mHwnd = hwnd;

        ShowWindow(hwnd, SwShow);
        UpdateWindow(hwnd);
        SetForegroundWindow(hwnd);

        // effEditOpen：把宿主窗口句柄交给插件，它会在里面创建自己的子窗口。
        Call(EffEditOpen, 0, IntPtr.Zero, hwnd, 0);

        // 打开之后再问一次尺寸：不少插件只有在这时才报得准，然后按它调整窗口。
        bool hasRealSize = TryGetEditorSize(out int openedWidth, out int openedHeight);
        if (hasRealSize && (openedWidth != width || openedHeight != height))
        {
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, openedWidth, openedHeight,
                SwpNoMove | SwpNoZOrder | SwpNoActivate);
        }

        // 有些插件要 idle 才画第一帧。
        Call(EffEditIdle, 0, IntPtr.Zero, IntPtr.Zero, 0);

        SetTimer(hwnd, 1, 50, IntPtr.Zero);

        mOpened = true;
        mReady.Set();

        // GetMessage 返回 0 = WM_QUIT，-1 = 出错；两者都退出循环（写成 while(GetMessage(...))
        // 会把 -1 当成"继续"）。
        while (GetMessage(out MSG message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        mReady.Set();
    }

    IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmTimer:
                // effEditIdle 必须在 UI 线程上、按固定节奏调（VST2 用它驱动动画与计时器）。
                Call(EffEditIdle, 0, IntPtr.Zero, IntPtr.Zero, 0);
                return IntPtr.Zero;

            case WmBringToFront:
                SetForegroundWindow(hwnd);
                return IntPtr.Zero;

            case WmClose:
                Call(EffEditClose, 0, IntPtr.Zero, IntPtr.Zero, 0);
                KillTimer(hwnd, 1);
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    // 从 ERect* 读出插件首选的客户区尺寸。返回 false = 插件没报 / 报了离谱的值。
    bool TryGetEditorSize(out int width, out int height)
    {
        width = 640;
        height = 480;

        IntPtr rect = Call(EffEditGetRect, 0, IntPtr.Zero, IntPtr.Zero, 0);
        if (rect == IntPtr.Zero)
            return false;

        short top = Marshal.ReadInt16(rect, 0);
        short left = Marshal.ReadInt16(rect, 2);
        short bottom = Marshal.ReadInt16(rect, 4);
        short right = Marshal.ReadInt16(rect, 6);

        int w = right - left;
        int h = bottom - top;

        if (w < 120 || w > 4096 || h < 80 || h > 4096)
            return false;

        width = w;
        height = h;
        return true;
    }

    IntPtr Call(int opcode, int index, IntPtr value, IntPtr ptr, float opt)
    {
        try
        {
            return mDispatcher(mEffect, opcode, index, value, ptr, opt);
        }
        catch (Exception)
        {
            // 插件在编辑器回调里抛错：桥自己不能倒。窗口照常关。
            return IntPtr.Zero;
        }
    }

    // ── Win32 ──

    const uint WsOverlappedWindow = 0x00CF0000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

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

    delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);
}
