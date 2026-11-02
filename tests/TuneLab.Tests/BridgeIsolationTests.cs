using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.VstBridge;
using Xunit;

namespace TuneLab.Tests;

// [P5.1] 隔离机制的端到端验证（VST3-BRIDGE.md §9.10 清单 1–3，Linux x64）。
//
// 【为什么单独一组】AudioStallWatchTests 钉的是"判定"，这组钉的是"进程本身"：
// 桥起来后是不是自成进程组、宿主没了（存活管道 EOF）桥会不会跟着死、KillBridge 是否真清干净。
// 这些只有起真进程才测得到。
//
// 【为什么默认跳过】要有 linux-x64 的桥 exe（自包含发布）落在测试输出目录的
// bridge/linux-x64/ 下。没有就跳过（CI / 别人的机器上不一定有），与 Vst3IntegrationTests 同口径。
// 本组读 /proc，故仅 Linux。
[Collection("IsolatedBridge")]
public class BridgeIsolationTests
{
    static bool PlatformOk => OperatingSystem.IsLinux() && Environment.Is64BitProcess;

    static string BridgeRid =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";

    static string BridgePath =>
        Path.Combine(AppContext.BaseDirectory, "bridge", BridgeRid, "TuneLab.ExperimentalPatch.VstBridge.x64");

    // 【为什么用"提前 return"而不是 Skip.Unless】Skip.Unless 靠抛异常表达跳过，在
    // dotnet test 里算一次**失败**（消息带 SKIP: 前缀，CI 另行识别）。这组在 Windows 上
    // 永远不适用，若那样做会让 Windows 的全绿套件每次都挂三条。所以这里安静地返回。
    // 覆盖只在 Linux + 桥 exe 就位时发生（本机与 CI 的 linux 目标上都满足）。
    static bool RequireBridge()
    {
        return PlatformOk && File.Exists(BridgePath);
    }

    static void StartOrFail(VstBridgeClient client)
    {
        bool started = client.EnsureStartedBlocking();
        Assert.True(started,
            $"the bridge did not start (path={BridgePath}, baseDir={AppContext.BaseDirectory}): {client.LastBridgeError}");
    }

    // 1) 桥起来后自成进程组：pgid == pid。宿主超时后才能一锅端（含孙进程）。
    [Fact]
    public void BridgeRunsInItsOwnProcessGroup()
    {
        if (!RequireBridge())
            return;

        var client = VstBridgeClient.ForBitness(64);
        try
        {
            StartOrFail(client);

            int pid = client.BridgeProcessIdForTest!.Value;
            Assert.Equal(pid, ProcessGroupOf(pid));
        }
        finally
        {
            client.KillBridge();
        }
    }

    // 2) 宿主消失（存活管道写端被回收）→ 桥的看门狗读到 EOF → 桥消失。
    [Fact]
    public void BridgeDiesWhenTheLivenessPipeCloses()
    {
        if (!RequireBridge())
            return;

        var client = VstBridgeClient.ForBitness(64);
        try
        {
            StartOrFail(client);

            int pid = client.BridgeProcessIdForTest!.Value;
            Assert.False(IsGone(pid), "the bridge should be alive right after starting");

            // 等价于"宿主进程被 SIGKILL、写端被内核回收"。
            client.CloseLivenessForTest();

            Assert.True(WaitGone(pid, TimeSpan.FromSeconds(5)),
                "the bridge survived the liveness-pipe EOF (orphan)");
        }
        finally
        {
            client.KillBridge();
        }
    }

    // 3) KillBridge 之后进程真的没了（不是留个僵尸）。
    [Fact]
    public void KillBridgeRemovesTheProcess()
    {
        if (!RequireBridge())
            return;

        var client = VstBridgeClient.ForBitness(64);
        StartOrFail(client);

        int pid = client.BridgeProcessIdForTest!.Value;
        client.KillBridge();

        Assert.True(WaitGone(pid, TimeSpan.FromSeconds(5)),
            "the bridge survived KillBridge");
    }

    // 4) [P5.2] 桥被杀掉后能在**同一进程、同一管道名**下重启。
    //    上一轮的 UDS socket 文件若没删掉，新桥 bind 会 EADDRINUSE —— 这条就钉住那件事。
    [Fact]
    public void BridgeCanRestartAfterBeingKilled()
    {
        if (!RequireBridge())
            return;

        var client = VstBridgeClient.ForBitness(64);

        try
        {
            StartOrFail(client);
            int first = client.BridgeProcessIdForTest!.Value;

            client.KillBridge();
            Assert.True(WaitGone(first, TimeSpan.FromSeconds(5)), "the first bridge did not go away");

            StartOrFail(client);
            int second = client.BridgeProcessIdForTest!.Value;
            Assert.NotEqual(first, second);
        }
        finally
        {
            client.KillBridge();
        }
    }

    // 5) [P5.2] 启动清扫：属于**已死宿主**的段文件与管道 socket 被删，属于活进程的保留。
    [Fact]
    public void ResidueSweepRemovesFilesFromDeadHosts()
    {
        if (!PlatformOk)
            return;

        string dir = SharedAudioRing.UnixSectionDirectory;
        Directory.CreateDirectory(dir);

        int deadPid = 99999999;          // 远大于典型 pid_max，kill(pid,0) 必 ESRCH
        int livePid = Environment.ProcessId;

        string deadIn = Path.Combine(dir, BridgeProtocol.PipeName + "-64-" + deadPid + ".in");
        string deadOut = Path.Combine(dir, BridgeProtocol.PipeName + "-64-" + deadPid + ".out");
        string liveIn = Path.Combine(dir, BridgeProtocol.PipeName + "-64-" + livePid + ".in");

        string deadSocket = BridgeResidue.PipeSocketPath(BridgeProtocol.PipeName + "-64-" + deadPid);
        string liveSocket = BridgeResidue.PipeSocketPath(BridgeProtocol.PipeName + "-64-" + livePid);

        File.WriteAllText(deadIn, "");
        File.WriteAllText(deadOut, "");
        File.WriteAllText(liveIn, "");
        File.WriteAllText(deadSocket, "");
        File.WriteAllText(liveSocket, "");

        try
        {
            BridgeResidue.Sweep();

            Assert.False(File.Exists(deadIn), "a dead host's section file should be swept");
            Assert.False(File.Exists(deadOut), "a dead host's section file should be swept");
            Assert.False(File.Exists(deadSocket), "a dead host's pipe socket should be swept");

            Assert.True(File.Exists(liveIn), "a live host's section file must be kept");
            Assert.True(File.Exists(liveSocket), "a live host's pipe socket must be kept");
        }
        finally
        {
            foreach (string file in new[] { deadIn, deadOut, liveIn, deadSocket, liveSocket })
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    // ── /proc 小工具 ──

    // /proc/<pid>/stat：comm 里可能有空格/括号，所以从最后一个 ')' 之后切；
    // 依次是 state(0) ppid(1) pgrp(2)。
    static int ProcessGroupOf(int pid)
    {
        string stat = File.ReadAllText($"/proc/{pid}/stat");
        int close = stat.LastIndexOf(')');
        string[] fields = stat[(close + 2)..].Split(' ');
        return int.Parse(fields[2]);
    }

    static bool IsGone(int pid) => !Directory.Exists($"/proc/{pid}") || IsZombie(pid);

    static bool IsZombie(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int close = stat.LastIndexOf(')');
            return stat.Length > close + 2 && stat[close + 2] == 'Z';
        }
        catch (Exception)
        {
            return true;
        }
    }

    static bool WaitGone(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (IsGone(pid))
                return true;

            Thread.Sleep(50);
        }

        return false;
    }
}
