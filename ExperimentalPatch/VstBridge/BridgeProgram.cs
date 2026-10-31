using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.VstBridge;

// 桥进程的入口（DESIGN.md §5.3）。
//
// 【它要守住的全部东西】
//   1. 宿主说连才连；连上以后**只**听宿主的。
//   2. 宿主消失（无论是正常退出、崩溃还是被任务管理器杀掉）→ 本进程必须立刻消失。
//      Windows：把自己塞进宿主的 JobObject，句柄由宿主持有。宿主一死，作业句柄被系统关闭，
//      JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE 生效，内核杀我们，轮不到我们反应。
//      Unix（P5.1，VST3-BRIDGE.md §9.4）：宿主另开一根**存活管道**，写端只在宿主持有；
//      这里起一支独立看门狗线程阻塞读它。宿主一死，写端被内核回收 → read 返回 0 → 我们
//      带走自己的整个进程组（setpgid 自成一组的那个）。看门狗独立于主循环，插件卡死也照样醒。
//   3. 不写标准输出以外的鬼东西：宿主可能把我们的 stdouts 接走，任何日志都要能关掉。
internal static class BridgeProgram
{
    static int Main(string[] args)
    {
        // 参数契约：宿主传 --pipe <name>。缺了就说明是"被人手点开了"，立刻退出
        // ——一个单独运行的桥没有任何意义（它的共享段与生命周期都归宿主）。
        //
        // 【为什么没有 --job】作业句柄是进程本地的，把它的数值传给子进程只会得到
        // 一个不存在的句柄（ERROR_INVALID_HANDLE）。所以"加入作业"这一步由宿主
        // 在 Process.Start 之后自己调 AssignProcessToJobObject 完成（见 VstBridgeClient）。
        string? pipeName = null;

        // [P5.1] Unix 专用：宿主存活管道的读端 fd（字符串形式）。Windows 上不传。
        string? livenessHandle = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--pipe")
                pipeName = args[i + 1];
            else if (args[i] == "--liveness")
                livenessHandle = args[i + 1];
        }

        if (pipeName == null)
        {
            Console.Error.WriteLine("vst-bridge: missing --pipe; this process only runs under TuneLab.");
            return 2;
        }

        // Unix：自成进程组 + 起看门狗。要在开始等宿主连接**之前**装好 ——
        // 否则宿主在这段窗口里崩掉，我们就成了孤儿。
        if (!OperatingSystem.IsWindows() && livenessHandle is { Length: > 0 })
        {
            UnixProcess.PutInOwnProcessGroup();
            StartLivenessWatchdog(livenessHandle);
        }

        Console.CancelKeyPress += (_, e) =>
        {
            // Ctrl+C 放行默认行为（进程退出）；拦截只是为了不让控制台把栈打出来吓到人。
            e.Cancel = false;
        };

        try
        {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            // 等宿主来连。给一个上限：宿主启动我们之后应该在几百毫秒内连上；
            // 连不上说明启动路径出了问题，别在这挂成僵尸进程。
            if (!server.IsConnected)
            {
                var connect = server.WaitForConnectionAsync(CancellationToken.None);
                if (!connect.Wait(TimeSpan.FromSeconds(30)))
                {
                    Console.Error.WriteLine("vst-bridge: host never connected; exiting.");
                    return 4;
                }
            }

            // ── 共享音频环（音频数据不走管道，见 BridgeSharedMemory 的说明）──
            // 名字由管道名派生：宿主用同一套名字创建这两段内存，两端就对得上。
            // 桥只 Open 不 Create——大小与布局由宿主那一侧定，这里只负责按同样的
            // 名字找到它。名字对不上时直接退出，不静默。
            string inName = pipeName + ".in";
            string outName = pipeName + ".out";

            SharedAudioRing input;
            SharedAudioRing output;

            try
            {
                input = SharedAudioRing.Open(inName, null);
                output = SharedAudioRing.Open(outName, null);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"vst-bridge: cannot open the shared audio sections: {ex.Message}");
                return 5;
            }

            using var session = new BridgeSession(server, input, output);
            session.Run();

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("vst-bridge: " + ex);
            return 5;
        }
    }

    // [P5.1] 存活看门狗：阻塞读宿主那根存活管道的读端。宿主进程一死（正常 / 崩溃 / SIGKILL），
    // 它持有的写端被内核回收 → 我们这里 read 返回 0（EOF）→ 带走整个进程组。
    //
    // 【为什么独立线程】插件卡在 process / Create 里时桥的主循环读不到控制管道，但这条线程
    // 只在自己的管道上阻塞，照样会醒。这是"宿主死了桥一定跟着没"的关键（§9.4）。
    static void StartLivenessWatchdog(string handle)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var pipe = new AnonymousPipeClientStream(PipeDirection.In, handle);
                var buffer = new byte[1];

                // 宿主活着就一直阻塞在这里（read 不返回）；写端一关就是 0。
                while (pipe.Read(buffer, 0, 1) > 0)
                {
                }
            }
            catch (Exception)
            {
                // 管道断开也当作"宿主没了"。
            }

            UnixProcess.KillOwnProcessGroupOrSelf();
        })
        {
            IsBackground = true,
            Name = "bridge-liveness",
        };

        thread.Start();
    }
}

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}


