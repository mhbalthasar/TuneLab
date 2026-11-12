using System;
using System.Runtime.InteropServices;

namespace TuneLab.ExperimentalPatch.VstBridge;

// 这台机器有没有**硬件** OpenGL —— 只用来决定"能不能打开需要 GL 1.3+ 的插件界面"。
//
// 【为什么不建个 GL 上下文问版本】那会把系统的 opengl32 加载进进程（模块按文件名唯一），
// 而且插件进程里多碰一次 GL 只会增加不稳定因素。这里只看显示适配器名字：
// 虚拟机 / 远程桌面 / 基础显示驱动的 GL 只有 1.1，真显卡都不是这些名字。
//
// 【软件 OpenGL 不再由宿主代装】用户需要时自己把 Mesa 的 opengl32.dll 放到程序目录
// （与 exe 同级）—— 那是**系统/用户层面**的决定，宿主不替他做，也不带任何 Mesa 文件。
public static class OpenGlAvailability
{
    public static bool HasUsableOpenGl => HasHardwareGraphicsAdapter();

    public static bool HasHardwareGraphicsAdapter()
    {
        if (!OperatingSystem.IsWindows())
            return true;   // 非 Windows 不在这里判断（VST3 宿主本身也只有 Windows 版）

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

    static bool LooksSoftwareOnly(string? adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
            return true;

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
}
