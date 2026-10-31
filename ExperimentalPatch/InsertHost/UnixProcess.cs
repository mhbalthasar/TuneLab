using System;
using System.Runtime.InteropServices;

namespace TuneLab.ExperimentalPatch.InsertHost;

// Unix 侧"进程生命周期"用到的几个 libc 调用（Windows 用 JobObject，不走这里）。
//
// 【为什么单独一处】宿主与桥都要用：桥启动时 setpgid 自成进程组、装存活看门狗；宿主超时后
// 按进程组一锅端。两边对"进程组"的理解必须一致，所以口径只写一遍（VST3-BRIDGE.md §9.4/§9.5）。
//
// 【只在 Unix 调】Windows 上这些入口都不会被走到（宿主 / 桥都按 OperatingSystem.IsWindows 分叉）。
// DllImport("libc") 在 Linux 映射到 libc.so、macOS 映射到 libSystem —— 两端都有这几个符号。
public static class UnixProcess
{
    const int SIGKILL = 9;

    [DllImport("libc", SetLastError = true)]
    static extern int setpgid(int pid, int pgid);

    [DllImport("libc", SetLastError = true)]
    static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    static extern int getpid();

    [DllImport("libc", SetLastError = true)]
    static extern int getppid();

    [DllImport("libc", SetLastError = true)]
    static extern int getpgrp();

    /// <summary>
    /// 桥启动时调：自成进程组（组长 = 自己）。于是宿主超时后能一次干掉桥**和它拉起的孙进程**，
    /// 而不受"孙进程恰好被 reparent"影响（§9.5）。
    /// </summary>
    public static bool PutInOwnProcessGroup() => setpgid(0, 0) == 0;

    /// <summary>宿主调：SIGKILL 整个进程组（组长 pid = 桥的 pid，因为桥启动时 setpgid(0,0)）。</summary>
    public static void KillProcessGroup(int leaderPid)
    {
        if (leaderPid > 0)
            kill(-leaderPid, SIGKILL);
    }

    /// <summary>
    /// 存活看门狗在"宿主没了"（存活管道 EOF）时调：优先带走整个进程组；
    /// 万一 setpgid 没成功（我们不是组长），退化成只杀自己 —— 绝不能按错的 pgid 误伤别人。
    /// </summary>
    public static void KillOwnProcessGroupOrSelf()
    {
        int pid = getpid();

        if (getpgrp() == pid)
            kill(-pid, SIGKILL);
        else
            kill(pid, SIGKILL);
    }

    public static int GetPid() => getpid();

    public static int GetParentPid() => getppid();

    const int EPERM = 1;

    /// <summary>
    /// 宿主调：这个 PID 还活着吗。残留清扫用它判断"名字里那个宿主是不是已经没了"。
    /// kill(pid, 0) 不发信号，只做存在性/权限检查：0 = 存在；ESRCH = 没了；
    /// EPERM = 存在但不属于我们（也算活着）。
    /// </summary>
    public static bool IsAlive(int pid)
    {
        if (pid <= 0)
            return false;

        if (kill(pid, 0) == 0)
            return true;

        return Marshal.GetLastPInvokeError() == EPERM;
    }
}
