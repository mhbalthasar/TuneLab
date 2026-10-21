using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TuneLab.ExperimentalPatch.VstBridge;

// OpenGL 兜底：让插件用 Mesa3D 的软件 opengl32。
//
// 【为什么需要】DPF / NanoVG 那类插件（ZamDynamicEQ、Dragonfly 系列）的界面必须走
// OpenGL 1.3+。Windows 自带的软件渲染器只到 1.1，于是在虚拟机 / 远程桌面上它们会画成
// 白色块（日志里是 `glActiveTexture != nullptr` / `Failed to create NanoVG context`）。
//
// 【怎么兜】在 `runtimes/patcher/<arch>/opengl32.dll` 放一份 Mesa3D（llvmpipe）构建，
// 启动时**先把它 LoadLibrary 进来**：Windows 的模块按**文件名**唯一，谁先加载谁生效，
// 之后插件 import `opengl32.dll` 拿到的就是我们这份。再用 SetDllDirectory 把它加进
// 搜索路径，照顾"插件自己用 LoadLibrary 去找"的情形。
//
// 【什么时候生效】必须早于任何 GL 调用。所以宿主在创建第一个插入实例之前调
// （InsertInstanceFactory 的静态构造），桥在自己的 Main 里调。
//
// 【开关就是那个文件】目录里有 opengl32.dll 才启用 —— 不想用软件渲染就别放。
// 这样不会在一台有正常 GPU 的机器上悄悄把渲染降级成 CPU。
public static class OpenGlPatcher
{
    /// <summary>已经生效的目录；null = 没启用（不是 Windows / 没放 dll / 加载失败）。</summary>
    public static string? AppliedDirectory { get; private set; }

    /// <summary>Mesa 用的渲染后端："d3d12"（有硬件 D3D12）或 "llvmpipe"（纯 CPU）。</summary>
    public static string? AppliedDriver { get; private set; }

    static bool sAttempted;

    public static void EnsureApplied()
    {
        if (sAttempted)
            return;

        sAttempted = true;

        if (!OperatingSystem.IsWindows())
            return;

        string? directory = FindPatcherDirectory();
        if (directory == null)
            return;

        // **硬件优先**：只有判定"这台机器没有可用的硬件 OpenGL"时才装软件实现。
        // 判定必须在加载任何 GL 模块之前做（模块按文件名唯一，先加载谁就全进程用谁），
        // 所以这里只看显示适配器，不创建 GL 上下文。
        if (HasHardwareGraphicsAdapter())
            return;

        try
        {
            // Mesa 的渲染后端：能走 DX 就走 DX（GPU 加速），否则纯 CPU（llvmpipe）。
            // 必须在加载 opengl32 之前设，Mesa 在初始化时读这个环境变量。
            string driver = ChooseGalliumDriver();
            Environment.SetEnvironmentVariable("GALLIUM_DRIVER", driver);
            AppliedDriver = driver;

            // 1) 加进 DLL 搜索路径（插件自己 LoadLibrary("opengl32") 时也能命中）。
            SetDllDirectory(directory);

            // 2) 预加载：模块按文件名唯一，先装进来的那份就是全进程用的那份。
            IntPtr module = LoadLibraryW(Path.Combine(directory, "opengl32.dll"));
            if (module == IntPtr.Zero)
                return;

            AppliedDirectory = directory;
        }
        catch (Exception)
        {
            // 加载失败就当没放：最坏是维持原来的"白窗"，不影响其它功能。
        }
    }

    // 有没有"像样"的显示适配器 —— 有就用硬件 OpenGL，没有才落到软件渲染。
    //
    // 【为什么不直接建个 GL 上下文问版本】那会把系统的 opengl32 加载进进程，
    // 之后无论再 LoadLibrary 哪一份 Mesa 都没用了（模块按文件名唯一）。所以只能
    // 看**适配器名字**：虚拟机 / 远程桌面 / 基础显示驱动这几类的 GL 只有 1.1，
    // 真显卡（NVIDIA/AMD/Intel）都不是这些名字。
    static bool HasHardwareGraphicsAdapter()
    {
        for (uint i = 0; ; i++)
        {
            var device = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };

            if (!EnumDisplayDevicesW(null, i, ref device, 0))
                break;

            // 只认"真的接到桌面上"的适配器（DISPLAY_DEVICE_ATTACHED_TO_DESKTOP）。
            if ((device.StateFlags & 0x1) == 0)
                continue;

            if (!LooksSoftwareOnly(device.DeviceString))
                return true;
        }

        return false;
    }

    // 选 Mesa 的渲染后端。
    //
    //   d3d12    → OpenGL 转成 D3D12 跑在 GPU 上（Mesa 的 gallium d3d12 驱动）。有硬件
    //              D3D12 就用它，比纯 CPU 快得多。
    //   llvmpipe → 纯 CPU 光栅化。没有硬件 D3D12 时的兜底。
    //
    // 判据是"能不能创建一个默认适配器的 D3D12 设备"—— 这是最直接的硬件 D3D12 可用性检查，
    // 而且不加载 opengl32（那是这次要避免的，见上面的说明）。
    static string ChooseGalliumDriver()
    {
        try
        {
            IntPtr library = LoadLibraryW("d3d12.dll");
            if (library == IntPtr.Zero)
                return "llvmpipe";

            IntPtr entry = GetProcAddress(library, "D3D12CreateDevice");
            if (entry == IntPtr.Zero)
                return "llvmpipe";

            var create = (D3D12CreateDeviceFn)Marshal.GetDelegateForFunctionPointer(entry, typeof(D3D12CreateDeviceFn));

            var iid = new Guid("189819f1-1db6-4b57-be54-1821339b85f7");   // IID_ID3D12Device
            const int featureLevel11_0 = 0xB000;

            // 默认适配器 = 硬件；失败（没有 D3D12 驱动 / 老系统）就退回 CPU。
            int hr = create(IntPtr.Zero, featureLevel11_0, ref iid, out IntPtr device);
            if (hr < 0)
                return "llvmpipe";

            if (device != IntPtr.Zero)
                Marshal.Release(device);   // 只问"有没有"，问完就放

            return "d3d12";
        }
        catch (Exception)
        {
            return "llvmpipe";
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int D3D12CreateDeviceFn(IntPtr adapter, int minimumFeatureLevel, ref Guid riid, out IntPtr device);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);

    static bool LooksSoftwareOnly(string? adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
            return true;

        // 覆盖：没有显卡驱动的虚拟机、远程桌面、"基础显示适配器"。
        string[] markers =
        [
            "Microsoft Basic Render Driver",
            "Microsoft Basic Display Adapter",
            "Hyper-V",
            "Remote Display",
            "Remote Desktop",
            "Virtual",
            "GameViewer",
            "Parsec",
            "Sunshine",
        ];

        foreach (string marker in markers)
        {
            if (adapterName.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevicesW(string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

    // 按**当前进程位数**选目录：32 位宿主/桥用 x86 那份，ARM64 用 arm64。
    static string? FindPatcherDirectory()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };

        string directory = Path.Combine(AppContext.BaseDirectory, "runtimes", "patcher", arch);

        return File.Exists(Path.Combine(directory, "opengl32.dll")) ? directory : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectory(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryW(string path);
}







