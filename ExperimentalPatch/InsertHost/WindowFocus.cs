using System;
using System.Runtime.InteropServices;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 把某个窗口叫到前台（编辑器壳与桥里的 VST3 编辑器共用）。
//
// 【为什么单独一个类】它原来挂在 Vst3Insert 上（进程内实现）。VST3 的进程内实现已经搬到
// 桥工程，而宿主仍然要"让插件窗口浮上来"，所以这段只碰 Win32 的代码留在宿主侧。
//
// 【为什么是这几步】只调用 SetForegroundWindow 常常不生效（系统判定调用方不在前台时
// 会把调用变成任务栏闪烁）。先 ShowWindow(SW_RESTORE) 恢复最小化，再 SetWindowPos 到最前，
// 最后 SetForegroundWindow —— 用户"打开界面"时窗口应该在眼前。
public static class WindowFocus
{
    const int SwRestore = 9;
    static readonly IntPtr HwndTopmost = new(-1);
    const uint SwpNoSize = 0x0001;
    const uint SwpNoMove = 0x0002;
    const uint SwpShowWindow = 0x0040;

    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        ShowWindow(hwnd, SwRestore);
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);
}
