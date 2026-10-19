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
        // 参数契约：宿主传 --job <handle> --pipe <name>。缺任何一个就当"被人手点开了"，
        // 立刻退出——一个单独运行的桥没有任何意义（它的插件句柄是宿主管的）。
        string? jobHandle = null;
        string? pipeName = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--job")
                jobHandle = args[i + 1];
            else if (args[i] == "--pipe")
                pipeName = args[i + 1];
        }

        if (jobHandle == null || pipeName == null)
        {
            Console.Error.WriteLine("vst-bridge: missing --job/--pipe; this process only runs under TuneLab.");
            return 2;
        }

        if (!long.TryParse(jobHandle, out long jobValue) || jobValue == 0)
        {
            Console.Error.WriteLine("vst-bridge: invalid job handle.");
            return 2;
        }

        // 进宿主的作业。这一步就是"不能独活"的全部实现：
        // 从此刻起我们的命挂在宿主的作业句柄上，他关我们就没。
        IntPtr job = new(jobValue);
        if (!Native.AssignProcessToJobObject(job, Native.GetCurrentProcess()))
        {
            int error = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"vst-bridge: AssignProcessToJobObject failed ({error}); exiting.");
            return 3;
        }

        // 注意：不要把宿主**加进**这个作业。宿主创建它时已经指定了 KILL_ON_JOB_CLOSE，
        // 那是"杀我们"用的。反过来做就会变成"桥能决定宿主何时死"——那不是同步，那是劫持。

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
