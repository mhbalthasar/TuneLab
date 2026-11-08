using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TuneLab.ExperimentalPatch.VstBridge;

// ysfx 的 C ABI 绑定。
//
// 【为什么不是 DllImport("ysfx") 直接了事】库的文件名是 `ysfx.tlhost.dll` /
// `libysfx.tlhost.so` / `libysfx.tlhost.dylib`（与 ownvst3 同一套 .tlhost 约定），
// 不是运行时默认会找的 `ysfx.dll`。桥的 Vst3InProcessFactory 已为本程序集装了
// DllImportResolver；它把逻辑名 `ysfx` 转给下面的 Resolve，指到桥 exe 旁边的那一份。
// 测试时可以用 TUNELAB_YSFX_LIBRARY 指到任意路径。
//
// 【加载失败不抛】JsfxPluginHost.IsSupported 只回答"这个桥能不能跑 JSFX"。
// 缺库时桥照常启动，只是 HelloAck 不声明 jsfx —— 与 VST3 缺 ownvst3 时同一条路。
internal static unsafe class JsfxNative
{
    const string LibraryName = "ysfx";

    // 只在桥进程里用；x86 桥不声明 JSFX，也不应该尝试加载 64 位库。
    public static bool IsSupported { get; } = Probe();

    // 注意：这里**不**装 DllImportResolver。桥里的 Vst3InProcessFactory 已经为本程序集
    // 装了一个（ownvst3 需要它），.NET 不允许同一个程序集装第二个。那个 resolver 会把
    // `ysfx` 转给下面的 Resolve —— 见 Vst3InProcessFactory.ResolveNative。
    static bool Probe()
    {
        if (!Environment.Is64BitProcess)
            return false;

        try
        {
            string? path = FindLibraryPath();
            return path != null && NativeLibrary.Load(path) != IntPtr.Zero;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
            return IntPtr.Zero;

        try
        {
            string? path = FindLibraryPath();
            return path == null ? IntPtr.Zero : NativeLibrary.Load(path);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    static string? FindLibraryPath()
    {
        string? configured = Environment.GetEnvironmentVariable("TUNELAB_YSFX_LIBRARY");

        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Directory.Exists(configured))
            {
                string? inDirectory = FindIn(configured);
                if (inDirectory != null)
                    return inDirectory;
            }
            else if (File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }
        }

        return FindIn(AppContext.BaseDirectory);
    }

    static string? FindIn(string directory)
    {
        string[] names = OperatingSystem.IsWindows()
            ? ["ysfx.tlhost.dll", "ysfx.dll"]
            : OperatingSystem.IsMacOS()
                ? ["libysfx.tlhost.dylib", "libysfx.dylib"]
                : ["libysfx.tlhost.so", "libysfx.so"];

        foreach (string name in names)
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    // ── 配置 ──

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr ysfx_config_new();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_config_free(IntPtr config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_set_import_root(IntPtr config, [MarshalAs(UnmanagedType.LPUTF8Str)] string root);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_set_data_root(IntPtr config, [MarshalAs(UnmanagedType.LPUTF8Str)] string root);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_guess_file_roots(IntPtr config,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourcePath);

    // ── 实例 ──

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr ysfx_new(IntPtr config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_free(IntPtr fx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ysfx_load_file(IntPtr fx, [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath,
        uint loadOptions);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ysfx_compile(IntPtr fx, uint compileOptions);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_set_sample_rate(IntPtr fx, double sampleRate);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_set_block_size(IntPtr fx, uint blockSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_init(IntPtr fx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern uint ysfx_get_num_inputs(IntPtr fx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern uint ysfx_get_num_outputs(IntPtr fx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr ysfx_get_name(IntPtr fx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern double ysfx_get_pdc_delay(IntPtr fx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_process_float(IntPtr fx, float** ins, float** outs,
        uint numIns, uint numOuts, uint numFrames);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ysfx_has_section(IntPtr fx, uint type);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ysfx_get_gfx_dim(IntPtr fx, [Out] uint[] dim);

    // ── @gfx 窗口 ──

    public const uint SectionGfx = 5;

    public const uint GfxWindowFlagResizable = 1 << 0;
    public const uint GfxWindowFlagAlwaysOnTop = 1 << 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct GfxWindowDesc
    {
        public IntPtr Title;
        public uint Width;
        public uint Height;
        public double ScaleFactor;
        public uint Flags;
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr ysfx_gfx_window_create(IntPtr fx, ref GfxWindowDesc desc);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_gfx_window_destroy(IntPtr window);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ysfx_gfx_window_is_open(IntPtr window);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ysfx_gfx_window_pump(IntPtr window, int timeoutMs);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int ysfx_gfx_window_run(IntPtr window);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_gfx_window_request_close(IntPtr window);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_gfx_window_set_title(IntPtr window,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_gfx_window_resize(IntPtr window, uint width, uint height);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_gfx_window_show(IntPtr window, [MarshalAs(UnmanagedType.I1)] bool show);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr ysfx_gfx_window_get_native_handle(IntPtr window);

    // ── 参数 ──

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ysfx_slider_exists(IntPtr fx, uint index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr ysfx_slider_get_name(IntPtr fx, uint index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ysfx_slider_get_curve(IntPtr fx, uint index, out SliderCurve curve);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern double ysfx_slider_get_value(IntPtr fx, uint index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_slider_set_value(IntPtr fx, uint index, double value,
        [MarshalAs(UnmanagedType.I1)] bool notify);

    // ── 状态 ──

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr ysfx_save_state(IntPtr fx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void ysfx_state_free(IntPtr state);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ysfx_load_state(IntPtr fx, ref State state);

    [StructLayout(LayoutKind.Sequential)]
    public struct SliderCurve
    {
        public double Default;
        public double Min;
        public double Max;
        public double Inc;
        public byte Shape;
        public double Modifier;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StateSlider
    {
        public uint Index;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public IntPtr Sliders;
        public uint SliderCount;
        public IntPtr Data;
        public nuint DataSize;
    }
}
