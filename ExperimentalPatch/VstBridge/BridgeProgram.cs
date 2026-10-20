using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using TuneLab.ExperimentalPatch.VstBridge;

// 桥进程的入口（DESIGN.md §5.3）。
//
// 【它要守住的全部东西】
//   1. 宿主说连才连；连上以后**只**听宿主的。
//   2. 宿主消失（无论是正常退出、崩溃还是被任务管理器杀掉）→ 本进程必须立刻消失。
//      机制：把自己塞进宿主的 JobObject，句柄由宿主持有。宿主一死，作业句柄被系统关闭，
//      JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE 生效，内核杀我们，轮不到我们反应。
//   3. 不写标准输出以外的鬼东西：宿主可能把我们的 stdout 接走，任何日志都要能关掉。
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

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--pipe")
                pipeName = args[i + 1];
        }

        if (pipeName == null)
        {
            Console.Error.WriteLine("vst-bridge: missing --pipe; this process only runs under TuneLab.");
            return 2;
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
}

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
