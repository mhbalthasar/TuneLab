using System;
using System.IO;
using System.Threading;
using TuneLab.ExperimentalPatch.VstBridge;

namespace TuneLab.ExperimentalPatch.InsertHost;

// [P5.2] 强杀留下的残骸清理（Windows 没有这一类：命名段/命名事件由内核随进程回收）。
//
// Unix 上桥与宿主之间有两样东西落在文件系统里，而且都**以宿主 PID 命名**：
//   · 段文件：<temp>/tunelab-vstbridge/TuneLab.VstBridge-<bitness>-<hostPid>.in / .out
//   · 管道 socket：.NET 的 NamedPipeServerStream 在 Unix 上用 UDS，落在 <temp>/CoreFxPipe_<管道名>，
//     管道名同样是 TuneLab.VstBridge-<bitness>-<hostPid>
//
// 谁创建、谁清理：
//   · 段文件：宿主是创建者，KillBridge / Dispose 时删（正常路径）；进程被强杀时没机会删 → 扫。
//   · socket：桥是创建者，桥 Dispose 时会 unlink；桥被 SIGKILL 时不会 → 扫。
//
// 规矩很简单：**名字里的宿主 PID 已经死了 = 上一次没清干净，删掉**。活着的进程自己会清。
// PIN 复用只可能把"该删"误判成"还活"（保守、不漏删活人的东西），不会误删别人的文件。
public static class BridgeResidue
{
    static int sSwept;

    /// <summary>每进程只扫一次。桥第一次要用之前调（见 VstBridgeClient.EnsureStartedAsync）。</summary>
    public static void SweepOnce()
    {
        if (OperatingSystem.IsWindows())
            return;

        if (Interlocked.Exchange(ref sSwept, 1) != 0)
            return;

        Sweep();
    }

    /// <summary>扫一遍并删掉属于已死宿主的段文件与管道 socket。测试直接调它。</summary>
    public static void Sweep()
    {
        if (OperatingSystem.IsWindows())
            return;

        string prefix = BridgeProtocol.PipeName + "-";

        // 1) 段文件。就在我们自己的目录里，随便扫。
        try
        {
            string dir = SharedAudioRing.UnixSectionDirectory;

            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    // 去掉 .in / .out 后的名字形如 <prefix><bitness>-<hostPid>。
                    string bare = Path.GetFileNameWithoutExtension(file);

                    if (!bare.StartsWith(prefix, StringComparison.Ordinal))
                        continue;

                    if (IsDeadOwner(bare))
                        TryDelete(file);
                }
            }
        }
        catch (Exception)
        {
        }

        // 2) 管道 socket。落在 temp 根下，按 CoreFxPipe_ 前缀认领。
        try
        {
            string temp = Path.GetTempPath();

            foreach (string file in Directory.EnumerateFiles(temp, "CoreFxPipe_" + BridgeProtocol.PipeName + "*"))
            {
                string bare = Path.GetFileName(file)["CoreFxPipe_".Length..];

                if (!bare.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                if (IsDeadOwner(bare))
                    TryDelete(file);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>宿主的管道 socket 路径（.NET 在 Unix 上就是这么算的）。</summary>
    public static string PipeSocketPath(string pipeName) =>
        Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);

    /// <summary>
    /// 删掉某个管道名对应的 socket 文件。两处都调：
    ///   · 启动桥之前 —— 上一轮桥被强杀留下的同名 socket 会让新桥 bind 失败（EADDRINUSE）；
    ///   · KillBridge 之后 —— 桥被 SIGKILL，没机会 unlink。
    /// </summary>
    public static void DeletePipeSocket(string? pipeName)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(pipeName))
            return;

        TryDelete(PipeSocketPath(pipeName));
    }

    // 名字末尾那段就是宿主 PID（<prefix><bitness>-<hostPid>）。
    static bool IsDeadOwner(string bareName)
    {
        int dash = bareName.LastIndexOf('-');

        if (dash < 0)
            return false;

        if (!int.TryParse(bareName[(dash + 1)..], out int hostPid))
            return false;

        return hostPid > 0 && !UnixProcess.IsAlive(hostPid);
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
