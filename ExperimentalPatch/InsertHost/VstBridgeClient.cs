using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TuneLab.ExperimentalPatch.VstBridge;

// 宿主侧的桥客户端（DESIGN.md §5.3）。
//
// 【两条硬纪律的实现位置】
//   ① 只按需启动：一切从 VstBridgeClient.EnsureStarted(pluginPath) 开始，
//      而它只在"真的要加载一个需要桥的插件"时才被调用。
//   ② 宿主单方面决定生死：CreateJobObject + JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE，
//      桥进程以 --job <handle> 启动并 AssignProcessToJobObject。**句柄由我们持有**：
//      我们退出/崩溃，句柄被系统关闭 → 桥被内核杀掉。
//      反过来我们绝不能把桥加进"带 KILL_ON_JOB_CLOSE 又能影响我们"的作业里。
//
// 【线程】加载/参数/状态在 UI 线程；Process 从音频线程调用（引擎的音频回调）。
// 所以通道是双工的：控制消息走 await，音频走一个不被 await 阻塞的专用路径
// —— 见 SendProcess 的说明。
internal sealed class VstBridgeClient : IDisposable
{
    const string BridgeExecutableName = "TuneLab.ExperimentalPatch.VstBridge.exe";

    Process? mProcess;
    JobHandle? mJob;
    NamedPipeClientStream? mPipe;

    // 音频数据的通路（与 mPipe 分工：管道只走"指令"，音频走这两段共享内存）。
    SharedAudioRing? mInput;    // 宿主 → 桥
    SharedAudioRing? mOutput;   // 桥 → 宿主

    readonly SemaphoreSlim mWriteLock = new(1, 1);
    bool mDisposed;

    // 等一块音频的上限。20ms 是一个"宁可漏一块也不能让音频线程无限等"的取舍：
    // 桥卡死时（插件死循环）这一块直通，下一块照常发；记日志由调用方做。
    const int BlockWaitMs = 20;

    public static VstBridgeClient Instance { get; } = new();

    // 桥是否在跑（且管道还连着）。
    public bool IsRunning => mPipe?.IsConnected == true;

    /// <summary>
    /// 确保桥进程活着。只有要加载 32 位插件时才该调它。
    /// </summary>
    public async Task<bool> EnsureStartedAsync(CancellationToken cancellation = default)
    {
        if (mDisposed)
            return false;

        if (IsRunning)
            return true;

        // ── 1. 先建作业：必须在启动子进程**之前**，
        //    否则"我们崩在启动与加入之间"的那一瞬，桥就成孤儿了。
        mJob ??= JobHandle.Create();
        if (mJob == null)
            return false;

        // ── 2. 找桥的可执行文件。就在我们自己的 exe 旁边（一起发布）。
        string bridge = FindBridgeExecutable();
        if (bridge == null)
            return false;

        // ── 3. 启动。把作业句柄与管道名递给它。
        string pipeName = BridgeProtocol.PipeName + "-" + Environment.ProcessId;

        // 共享音频段在启动**之前**建好：桥起来后会 Open 这两段，找不到直接退出。
        // 名字由管道名派生，两端永远不会对不上。
        string inName = pipeName + ".in";
        string outName = pipeName + ".out";

        mInput?.Dispose();
        mOutput?.Dispose();

        try
        {
            // 宿主→桥那条不装 gate：纯自旋等桥腾槽位（概率极低，见 WaitForWritable 的注释）。
            // 桥→宿主这条装 gate：宿主的音频线程要能被"数据到了"唤醒，
            // 而不是每块都空转几千周期——那是白烧一个核。
            mInput = SharedAudioRing.Create(inName, null);
            mOutput = SharedAudioRing.Create(outName, pipeName + ".ready");
        }
        catch (Exception)
        {
            mInput = null;
            mOutput = null;
            return false;
        }

        var start = new ProcessStartInfo
        {
            FileName = bridge,
            // 桥是我们启动的私有实例，不共用一个固定的管道名 —— 两个 TuneLab 同时开会撞。
            Arguments = $"--job {mJob.Handle.ToInt64()} --pipe {pipeName}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        mProcess = Process.Start(start);
        if (mProcess == null)
            return false;

        // ── 4. 连上管道。桥最多等我们 30 秒，反过来也一样：
        //    连不上就把刚起的进程杀掉，别留一个没人要的桥。
        mPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await mPipe.ConnectAsync(3000, cancellation).ConfigureAwait(false);

            // 握手：确认桥还是我们以为的那个东西（位数、协议版本）。
            await SendControlAsync(JsonSerializer.Serialize(new HelloMessage
            {
                Version = BridgeProtocol.ProtocolVersion,
                Bitness = Environment.Is64BitProcess ? 64 : 32,
                Formats = ["vst2"],
            }), cancellation).ConfigureAwait(false);

            var ack = await ReadControlAsync(cancellation).ConfigureAwait(false);
            if (ack == null || !ack.Contains("\"ok\""))
            {
                KillBridge();
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            KillBridge();
            return false;
        }
    }

    public async Task<bool> TryLoadAsync(int slot, string pluginPath, double sampleRate, int maxBlock, bool bypass,
        CancellationToken cancellation = default)
    {
        if (!IsRunning)
            return false;

        var message = JsonSerializer.Serialize(new LoadPluginMessage
        {
            Slot = slot,
            Path = pluginPath,
            SampleRate = sampleRate,
            MaxBlock = maxBlock,
            Bypass = bypass,
        });

        try
        {
            await SendControlAsync(message, cancellation).ConfigureAwait(false);
            string? reply = await ReadControlAsync(cancellation).ConfigureAwait(false);
            return reply != null && reply.Contains("\"ok\":true");
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 音频路径。**不走 await**：它从音频线程调，那里不能等 Task。
    // 数据走共享内存（见 BridgeSharedMemory）：这一侧负责"写输入环、通知桥、等输出环"。
    //
    // 【为什么音频线程可以等】同步语义下就是要等桥把这一块做完——没有别的地方能等。
    // 共享内存让等待变得可超时（桥卡死 20ms 就放弃这块并直通），
    // 而命名管道那条路上同样的等待是不可控的（见文件头的说明）。
    public bool SendProcess(int slot, float[][] inPlanes, float[][] outPlanes, int channels, int frames, bool bypass)
    {
        if (mInput == null || mOutput == null)
            return false;

        // 1) 把输入推进宿主→桥的环，然后通知桥"有一块等你"。
        if (!mInput.TryPublish(slot, inPlanes, channels, frames, BlockWaitMs))
            return false;

        if (!SendControlBlocking(JsonSerializer.Serialize(new ProcessRequest
            {
                Slot = slot,
                Frames = frames,
                Channels = channels,
                Bypass = bypass,
            })))
        {
            return false;
        }

        // 2) 等桥的结果。桥处理完会把结果推进桥→宿主的环，然后回一个短回执。
        string? reply = ReadControlBlocking();
        if (reply == null)
            return false;

        if (!mOutput.TryRead(BlockWaitMs, out int outSlot, out var planes, out int outChannels, out int outFrames))
            return false;

        try
        {
            // 平面 →交织由调用方（引擎）做；这里只把平面数据搬到它的缓冲上。
            for (int ch = 0; ch < Math.Min(channels, outChannels); ch++)
                Array.Copy(planes[ch], outPlanes[ch], Math.Min(frames, outFrames));

            return true;
        }
        finally
        {
            mOutput.Release();
        }
    }

    /// <summary>宿主主动关闭桥（例如关闭混音台窗口）。</summary>
    public void KillBridge()
    {
        try
        {
            mPipe?.Dispose();
        }
        catch (Exception)
        {
        }

        mPipe = null;

        if (mProcess is { HasExited: false })
        {
            try
            {
                mProcess.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }

        mProcess?.Dispose();
        mProcess = null;

        // 共享段也一起放。桥已经死了，没有对端，留着只是占用一段命名内存。
        mInput?.Dispose();
        mOutput?.Dispose();
        mInput = null;
        mOutput = null;
    }

    public void Dispose()
    {
        if (mDisposed)
            return;

        mDisposed = true;
        KillBridge();

        // 最后放作业：这一步保证我们自己退出时桥一定跟着没。
        mJob?.Dispose();
        mJob = null;
    }

    // ── 内部 ──

    static string? FindBridgeExecutable()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, BridgeExecutableName);
        if (File.Exists(beside))
            return beside;

        // 部署上别处去了：让它跟着 TuneLab 的目录找。
        string? nextTo = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty, BridgeExecutableName);
        return File.Exists(nextTo) ? nextTo : null;
    }

    // 同步发一条控制消息（音频线程用）。与 SendControlAsync 同一条管道，
    // 但等待方式不同：音频线程不能 await，也不能被甩到别的线程上。
    bool SendControlBlocking(string json)
    {
        if (mPipe == null || !mPipe.IsConnected)
            return false;

        byte[] bytes = Encoding.UTF8.GetBytes(json);

        mWriteLock.Wait();
        try
        {
            mPipe.Write(BitConverter.GetBytes(bytes.Length));
            mPipe.Write(bytes);
            mPipe.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            mWriteLock.Release();
        }
    }

    async Task SendControlAsync(string json, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(mPipe);

        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await mWriteLock.WaitAsync(cancellation).ConfigureAwait(false);

        try
        {
            await mPipe.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellation).ConfigureAwait(false);
            await mPipe.WriteAsync(bytes, cancellation).ConfigureAwait(false);
            await mPipe.FlushAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            mWriteLock.Release();
        }
    }

    async Task<string?> ReadControlAsync(CancellationToken cancellation)
        => await ReadControlBlockingAsync(cancellation).ConfigureAwait(false);

    Task<string?> ReadControlBlockingAsync(CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(mPipe);

        return Task.Run(async () =>
        {
            var length = new byte[4];
            if (!await ReadExactAsync(mPipe, length, cancellation).ConfigureAwait(false))
                return null;

            int total = BitConverter.ToInt32(length);
            if (total <= 0 || total > BridgeProtocol.MaxFrameBytes)
                return null;

            var frame = new byte[total];
            if (!await ReadExactAsync(mPipe, frame, cancellation).ConfigureAwait(false))
                return null;

            return DecodeFrame(frame);
        }, cancellation);
    }

    // 同步版：音频线程用。不能 await，所以也不能用 Task.Run（那会把工作甩到别的线程，
    // 而音频要的是"现在、这里、这一块"）。
    string? ReadControlBlocking()
    {
        ArgumentNullException.ThrowIfNull(mPipe);

        var length = new byte[4];
        if (!ReadExact(mPipe, length))
            return null;

        int total = BitConverter.ToInt32(length);
        if (total <= 0 || total > BridgeProtocol.MaxFrameBytes)
            return null;

        var frame = new byte[total];
        if (!ReadExact(mPipe, frame))
            return null;

        return DecodeFrame(frame);
    }

    static string? DecodeFrame(byte[] frame)
    {
        // 带附带的帧：[JSON][0x00][二进制]。
        int zero = Array.IndexOf(frame, (byte)0);

        return zero >= 0
            ? Encoding.UTF8.GetString(frame, 0, zero)
            : Encoding.UTF8.GetString(frame);
    }

    static bool ReadExact(PipeStream pipe, byte[] buffer, int? count = null)
    {
        int target = count ?? buffer.Length;
        int read = 0;

        while (read < target)
        {
            int n = pipe.Read(buffer, read, target - read);
            if (n <= 0)
                return false;

            read += n;
        }

        return true;
    }

    static async Task<bool> ReadExactAsync(PipeStream pipe, byte[] buffer, CancellationToken cancellation)
    {
        int read = 0;

        while (read < buffer.Length)
        {
            int n = await pipe.ReadAsync(buffer.AsMemory(read), cancellation).ConfigureAwait(false);
            if (n <= 0)
                return false;

            read += n;
        }

        return true;
    }
}

// Windows 作业对象：本层的"宿主死、桥跟着死"全靠它。
//
// 【为什么不用父子进程关系】.NET 的 Process 退出不会杀子进程，Windows 也没有默认的
// 进程树销毁。JobObject 是唯一的内核级保证：句柄一关，属于这个作业的进程立刻消失，
// 不需要我们反应、不需要桥配合、也扛得住我们被任务管理器强杀。
internal sealed class JobHandle : IDisposable
{
    IntPtr mHandle;

    JobHandle(IntPtr handle)
    {
        mHandle = handle;
    }

    public IntPtr Handle => mHandle;

    public static JobHandle? Create()
    {
        IntPtr job = Native.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return null;

        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new Native.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                // 关键的一条：作业句柄全部关闭时，杀掉作业里的所有进程。
                // 我们的 Dispose 会关掉这个句柄，崩溃时系统替我们关 —— 两条路都通。
                LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        if (!Native.SetInformationJobObject(job, Native.JOBOBJECTINFOCLASS.ExtendedLimitInformation,
            ref info, Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            Native.CloseHandle(job);
            return null;
        }

        return new JobHandle(job);
    }

    public void Dispose()
    {
        if (mHandle != IntPtr.Zero)
        {
            // 这一下就是"桥不能独活"的开关。
            Native.CloseHandle(mHandle);
            mHandle = IntPtr.Zero;
        }
    }

    static class Native
    {
        internal const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        internal enum JOBOBJECTINFOCLASS
        {
            ExtendedLimitInformation = 9,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public IntPtr MinimumWorkingSetSize;
            public IntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public IntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public IntPtr ProcessMemoryLimit;
            public IntPtr JobMemoryLimit;
            public IntPtr PeakProcessMemoryUsed;
            public IntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(IntPtr hJob, JOBOBJECTINFOCLASS infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
