using System;
using System.Runtime.InteropServices;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 探测本进程能不能拿到可用的 OpenGL —— 用来区分"插件渲染坏了"和"这台机器没有 GL"。
//
// 【为什么需要它】DPF / NanoVG 那类插件（ZamDynamicEQ、Dragonfly 系列）的界面必须走
// OpenGL 1.3+。Windows 自带的软件渲染器只到 1.1，于是在虚拟机 / 远程桌面上它们的编辑器
// 会画成白色块，并在 stderr 里报 `glActiveTexture != nullptr` 之类的断言。
// 那个 stderr 属于插件自己（VST3 在本进程里，我们读不到），所以宿主只能**间接**判断：
// 开一次 GL 上下文，读 GL_VERSION。低于 1.3 就把原因写进日志 —— 下次再看到白窗，
// 日志里直接有答案，不用再从头查一遍。
//
// 【只做一次】结果缓存；创建/销毁上下文放在同一个调用里，不留给进程。
public static class OpenGlProbe
{
    static bool sProbed;
    static string? sVersion;

    /// <summary>GL_VERSION 字符串（如 "1.1.0 Microsoft Corporation GDI Generic"）。拿不到返回 null。</summary>
    public static string? Version
    {
        get
        {
            if (!sProbed)
            {
                sProbed = true;
                sVersion = Probe();
            }

            return sVersion;
        }
    }

    /// <summary>是否至少 OpenGL 1.3（DPF/NanoVG 的最低要求）。</summary>
    public static bool SupportsAtLeast13
    {
        get
        {
            string? version = Version;
            if (version == null)
                return false;

            int space = version.IndexOf(' ');
            string numeric = space > 0 ? version[..space] : version;

            string[] parts = numeric.Split('.');
            if (parts.Length < 2
                || !int.TryParse(parts[0], out int major)
                || !int.TryParse(parts[1], out int minor))
            {
                return false;
            }

            return major > 1 || (major == 1 && minor >= 3);
        }
    }

    // 建一个隐藏窗口 → 选像素格式 → 建 GL 上下文 → 读 GL_VERSION → 全部销毁。
    // 任何一步失败都返回 null（说明这台机器连上下文都建不出来）。
    static string? Probe()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        IntPtr hwnd = IntPtr.Zero;
        IntPtr dc = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;

        try
        {
            hwnd = CreateWindowEx(0, "STATIC", "tunelab-gl-probe", 0, 0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
                return null;

            dc = GetDC(hwnd);
            if (dc == IntPtr.Zero)
                return null;

            var format = new PIXELFORMATDESCRIPTOR
            {
                nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
                nVersion = 1,
                dwFlags = PfdDrawToWindow | PfdSupportOpengl,
                iPixelType = PfdTypeRgba,
                cColorBits = 32,
                cAlphaBits = 8,
                cDepthBits = 24,
            };

            int pixelFormat = ChoosePixelFormat(dc, ref format);
            if (pixelFormat == 0)
                return null;

            if (!SetPixelFormat(dc, pixelFormat, ref format))
                return null;

            context = WglCreateContext(dc);
            if (context == IntPtr.Zero)
                return null;

            if (!WglMakeCurrent(dc, context))
                return null;

            IntPtr version = GlGetString(GlVersion);
            return version == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(version);
        }
        catch (Exception)
        {
            // 没装 opengl32 / 驱动异常：当作"拿不到"。
            return null;
        }
        finally
        {
            try
            {
                WglMakeCurrent(IntPtr.Zero, IntPtr.Zero);

                if (context != IntPtr.Zero)
                    WglDeleteContext(context);

                if (dc != IntPtr.Zero)
                    ReleaseDC(hwnd, dc);

                if (hwnd != IntPtr.Zero)
                    DestroyWindow(hwnd);
            }
            catch (Exception)
            {
            }
        }
    }

    const int GlVersion = 0x1F02;

    const uint PfdDrawToWindow = 0x00000004;
    const uint PfdSupportOpengl = 0x00000020;
    const byte PfdTypeRgba = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize;
        public ushort nVersion;
        public uint dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits;
        public byte cRedShift;
        public byte cGreenBits;
        public byte cGreenShift;
        public byte cBlueBits;
        public byte cBlueShift;
        public byte cAlphaBits;
        public byte cAlphaShift;
        public byte cAccumBits;
        public byte cAccumRedBits;
        public byte cAccumGreenBits;
        public byte cAccumBlueBits;
        public byte cAccumAlphaBits;
        public byte cDepthBits;
        public byte cStencilBits;
        public byte cAuxBuffers;
        public byte iLayerType;
        public byte bReserved;
        public uint dwLayerMask;
        public uint dwVisibleMask;
        public uint dwDamageMask;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern int ChoosePixelFormat(IntPtr dc, ref PIXELFORMATDESCRIPTOR format);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern bool SetPixelFormat(IntPtr dc, int format, ref PIXELFORMATDESCRIPTOR descriptor);

    [DllImport("opengl32.dll", EntryPoint = "wglCreateContext", SetLastError = true)]
    static extern IntPtr WglCreateContext(IntPtr dc);

    [DllImport("opengl32.dll", EntryPoint = "wglMakeCurrent", SetLastError = true)]
    static extern bool WglMakeCurrent(IntPtr dc, IntPtr context);

    [DllImport("opengl32.dll", EntryPoint = "wglDeleteContext", SetLastError = true)]
    static extern bool WglDeleteContext(IntPtr context);

    [DllImport("opengl32.dll", EntryPoint = "glGetString")]
    static extern IntPtr GlGetString(uint name);
}

