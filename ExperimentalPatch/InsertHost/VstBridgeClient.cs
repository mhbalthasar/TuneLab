using System;
using System.Collections.Generic;
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
    // 桥的可执行文件名。两个位数各一份（同名会让部署时互相覆盖）。
    const string BridgeExecutableName32 = "TuneLab.ExperimentalPatch.VstBridge.exe";
    const string BridgeExecutableName64 = "TuneLab.ExperimentalPatch.VstBridge.x64.exe";

    Process? mProcess;
    JobHandle? mJob;
    NamedPipeClientStream? mPipe;

    // 音频数据的通路（与 mPipe 分工：管道只走"指令"，音频走这两段共享内存）。
    SharedAudioRing? mInput;    // 宿主 → 桥
    SharedAudioRing? mOutput;   // 桥 → 宿主

    readonly SemaphoreSlim mWriteLock = new(1, 1);

    // 桥的 stderr（最近 64 行）。加载失败时把它拼进原因里 —— 否则
    // "VST2 bridge could not be started" 对用户和我们都等于没说。
    readonly List<string> mErrorLines = [];

    public string? LastBridgeError
    {
        get
        {
            lock (mErrorLines)
            {
                if (LastStartError is { Length: > 0 })
                    return mErrorLines.Count == 0
                        ? LastStartError
                        : LastStartError + " | " + string.Join(" | ", mErrorLines);

                return mErrorLines.Count == 0 ? null : string.Join(" | ", mErrorLines);
            }
        }
    }

    public void ClearBridgeError()
    {
        lock (mErrorLines)
            mErrorLines.Clear();

        LastStartError = null;
    }

    // 启动阶段失败的原因（beyond the bridge's own stderr）。
    public string? LastStartError { get; private set; }

    // 一次"发一条指令 + 读一条回复"必须原子：管道是单条流，
    // UI 线程改参数、音频线程发块，两条来回一交错就会把回复读错人。
    readonly SemaphoreSlim mRoundTrip = new(1, 1);

    readonly int mBitness;
    bool mDisposed;

    // 等一块音频的上限。20ms 是一个"宁可漏一块也不能让音频线程无限等"的取舍：
    // 桥卡死时（插件死循环）这一块直通，下一块照常发；记日志由调用方做。
    const int BlockWaitMs = 20;

    public VstBridgeClient(int bitness)
    {
        if (bitness != 32 && bitness != 64)
            throw new ArgumentOutOfRangeException(nameof(bitness));

        mBitness = bitness;
    }

    // **位数决定实例**：32 位插件只能进 x86 桥，64 位只能进 x64 桥。
    // 两个进程互相独立 —— 一个被插件搞死，另一个照常服务别的插件。
    public static VstBridgeClient ForBitness(int bitness) => bitness == 32 ? sX86 : sX64;

    static readonly VstBridgeClient sX86 = new(32);
    static readonly VstBridgeClient sX64 = new(64);

    // 桥是否在跑（且管道还连着）。
    public bool IsRunning => mPipe?.IsConnected == true;

    // 桥里的槽位号（插件的装载位）。每个桥进程各有一套，从 1 开始。
    int mNextSlot;

    public int AllocateSlot() => System.Threading.Interlocked.Increment(ref mNextSlot);

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
        string? bridge = FindBridgeExecutable(mBitness);
        if (bridge == null)
            return false;

        // ── 3. 启动。把作业句柄与管道名递给它。
        //     管道名带位数：两个桥可以同时活着，不能撞。
        string pipeName = BridgeProtocol.PipeName + "-" + mBitness + "-" + Environment.ProcessId;

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
            Arguments = $"--pipe {pipeName}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,

            // 桥的诊断信息全在 stderr 上（"找不到共享段"、"作业加入失败"…）。
            // 不接就没有任何办法知道它为什么没起来 —— 用户只会看到"插件加载失败"。
            RedirectStandardError = true,
        };

        mProcess = Process.Start(start);
        if (mProcess == null)
            return false;

        // 立刻把它收进作业。作业句柄不能靠命令行递给子进程（那是进程本地的句柄），
        // 只能由我们在自己这边拿到子进程句柄再加进去。
        //
        // 【那一小段竞争】"启动 → 加入"之间我们若崩，桥就成了孤儿。窗口是微秒级，
        // 而且是**我们崩**这种已经很难看的情形；比起"句柄传递处处不生效"，
        // 这个取舍划算。加入失败就把刚起的进程杀掉，绝不留一个游离的桥。
        if (mJob == null || !mJob.Assign(mProcess))
        {
            KillBridge();
            LastStartError = $"assigning the bridge to the job object failed ({Marshal.GetLastWin32Error()})";
            return false;
        }

        mProcess.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data))
                return;

            lock (mErrorLines)
            {
                mErrorLines.Add(e.Data);

                // 只留最近几十行：桥是长命进程，日志不能无限长。
                if (mErrorLines.Count > 64)
                    mErrorLines.RemoveAt(0);
            }
        };

        mProcess.BeginErrorReadLine();

        // ── 4. 连上管道。桥最多等我们 30 秒，反过来也一样：
        //    连不上就把刚起的进程杀掉，别留一个没人要的桥。
        mPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await mPipe.ConnectAsync(3000, cancellation).ConfigureAwait(false);

            // 握手：确认桥还是我们以为的那个东西（位数、协议版本）。
            string? ack = await ExchangeAsync(JsonSerializer.Serialize(new HelloMessage
            {
                Version = BridgeProtocol.ProtocolVersion,
                Bitness = mBitness,
                Formats = ["vst2"],
            }), cancellation).ConfigureAwait(false);
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

    // 加载插件并拿回它的元数据（名字/厂商/延迟/声道数）。
    // 返回 null = 管道断了 / 桥没起来；Ok=false = 插件自己拒绝（原因在 Reason）。
    public async Task<LoadPluginResultMessage?> TryLoadAsync(int slot, string pluginPath, double sampleRate,
        int maxBlock, bool bypass, CancellationToken cancellation = default)
    {
        if (!IsRunning)
            return null;

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
            string? reply = await ExchangeAsync(message, cancellation).ConfigureAwait(false);
            return reply == null ? null : JsonSerializer.Deserialize<LoadPluginResultMessage>(reply);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ── 下面这些都是"一问一答"，消息类型见 BridgeProtocol ──

    public async Task<bool> TryUnloadAsync(int slot, CancellationToken cancellation = default)
    {
        if (!IsRunning)
            return false;

        try
        {
            string? reply = await ExchangeAsync(
                JsonSerializer.Serialize(new UnloadPluginMessage { Slot = slot }),
                cancellation).ConfigureAwait(false);

            return reply != null
                && JsonSerializer.Deserialize<UnloadPluginResultMessage>(reply)?.Ok == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<byte[]?> TryGetStateAsync(int slot, CancellationToken cancellation = default)
    {
        if (!IsRunning)
            return null;

        try
        {
            string? reply = await ExchangeAsync(
                JsonSerializer.Serialize(new GetStateMessage { Slot = slot }),
                cancellation).ConfigureAwait(false);

            if (reply == null)
                return null;

            var chunk = JsonSerializer.Deserialize<StateChunkMessage>(reply);
            return chunk == null || string.IsNullOrEmpty(chunk.Base64)
                ? null
                : Convert.FromBase64String(chunk.Base64);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> TrySetStateAsync(int slot, byte[] data, CancellationToken cancellation = default)
    {
        if (!IsRunning || data.Length == 0)
            return false;

        try
        {
            string? reply = await ExchangeAsync(JsonSerializer.Serialize(new SetStateMessage
            {
                Slot = slot,
                Base64 = Convert.ToBase64String(data),
            }), cancellation).ConfigureAwait(false);

            return reply != null
                && JsonSerializer.Deserialize<SetStateResultMessage>(reply)?.Ok == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ParameterInfo>?> TryGetParametersAsync(int slot,
        CancellationToken cancellation = default)
    {
        if (!IsRunning)
            return null;

        try
        {
            string? reply = await ExchangeAsync(
                JsonSerializer.Serialize(new GetParametersMessage { Slot = slot }),
                cancellation).ConfigureAwait(false);

            if (reply == null)
                return null;

            return JsonSerializer.Deserialize<ParameterListMessage>(reply)?.Parameters;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> TrySetParameterAsync(int slot, long id, double value,
        CancellationToken cancellation = default)
    {
        if (!IsRunning)
            return false;

        try
        {
            string? reply = await ExchangeAsync(JsonSerializer.Serialize(new SetParameterMessage
            {
                Slot = slot,
                Id = id,
                Value = value,
            }), cancellation).ConfigureAwait(false);

            return reply != null
                && JsonSerializer.Deserialize<SetParameterResultMessage>(reply)?.Ok == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── UI 线程用的同步包装 ──
    //
    // 【为什么套一层 Task.Run】上面的 await 一旦直接 .GetAwaiter().GetResult() 在 UI 线程上，
    // 会把 UI 线程自己卡死（await 的续体要回 UI 线程执行，而 UI 线程正等着）。
    // Task.Run 把整条链甩到线程池，UI 线程空出来跑续体。
    public bool EnsureStartedBlocking()
        => RunBlocking(() => EnsureStartedAsync());

    public LoadPluginResultMessage? LoadBlocking(int slot, string path, double sampleRate, int maxBlock, bool bypass)
        => RunBlocking(() => TryLoadAsync(slot, path, sampleRate, maxBlock, bypass));

    public bool UnloadBlocking(int slot) => RunBlocking(() => TryUnloadAsync(slot));

    public byte[]? GetStateBlocking(int slot) => RunBlocking(() => TryGetStateAsync(slot));

    public bool SetStateBlocking(int slot, byte[] data) => RunBlocking(() => TrySetStateAsync(slot, data));

    public IReadOnlyList<ParameterInfo>? GetParametersBlocking(int slot)
        => RunBlocking(() => TryGetParametersAsync(slot));

    public bool SetParameterBlocking(int slot, long id, double value)
        => RunBlocking(() => TrySetParameterAsync(slot, id, value));

    static T RunBlocking<T>(Func<Task<T>> action) => Task.Run(action).GetAwaiter().GetResult();

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

        // 整段（发块 + 等回复 + 取输出）都在这把锁里：
        // 否则 UI 线程正好在改参数时，两个回复会串台。
        if (!mRoundTrip.Wait(BlockWaitMs))
            return false;

        try
        {
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

            if (!mOutput.TryRead(BlockWaitMs, out _, out var planes, out int outChannels, out int outFrames))
                return false;

            try
            {
                for (int ch = 0; ch < Math.Min(channels, outChannels); ch++)
                    Array.Copy(planes[ch], outPlanes[ch], Math.Min(frames, outFrames));

                return true;
            }
            finally
            {
                mOutput.Release();
            }
        }
        finally
        {
            mRoundTrip.Release();
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

    static string? FindBridgeExecutable(int bitness)
    {
        string name = bitness == 64 ? BridgeExecutableName64 : BridgeExecutableName32;
        string rid = bitness == 64 ? "win-x64" : "win-x86";

        string baseDirectory = AppContext.BaseDirectory;

        // 优先：按位数的子目录（自包含发布的正式布局）。
        // 两个位数各自的运行时文件必须分开放 —— x86 与 x64 的 hostfxr.dll / coreclr.dll
        // 同名不同架构，混在一个目录里必炸。
        string[] candidates =
        [
            Path.Combine(baseDirectory, "bridge", rid, name),
            Path.Combine(baseDirectory, name),
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty, "bridge", rid, name),
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty, name),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
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

    // 所有"发一条、收一条"都走这里：拿到往返锁，保证回复不会被别的线程读走。
    async Task<string?> ExchangeAsync(string json, CancellationToken cancellation)
    {
        await mRoundTrip.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await SendControlAsync(json, cancellation).ConfigureAwait(false);
            return await ReadControlAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            mRoundTrip.Release();
        }
    }

    // 同步版（音频线程）；拿不到锁就放弃这一块，让调用方直通。
    string? ExchangeBlocking(string json)
    {
        if (!mRoundTrip.Wait(BlockWaitMs))
            return null;

        try
        {
            return SendControlBlocking(json) ? ReadControlBlocking() : null;
        }
        finally
        {
            mRoundTrip.Release();
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

    // 把一个刚启动的子进程收进作业。**必须在我们这边调**（见 VstBridgeClient 的说明）。
    public bool Assign(System.Diagnostics.Process process)
        => mHandle != IntPtr.Zero && Native.AssignProcessToJobObject(mHandle, process.Handle);

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

    // 把子进程加进作业。**必须由宿主在自己这边调**：作业句柄是进程本地的，
    // 把它的数值当参数传给子进程，子进程拿到的只是"一个不存在的句柄"
    // （ERROR_INVALID_HANDLE = 6）—— 第一版就是这么写的，桥一起来就退出。
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(IntPtr hJob, JOBOBJECTINFOCLASS infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}

