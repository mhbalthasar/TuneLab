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
using TuneLab.ExperimentalPatch.InsertHost;
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
internal sealed class VstBridgeClient : IDisposable, IBridgeChannel
{
    // 桥的可执行文件名（不含扩展名，扩展名按平台补）。两个位数各一份（同名会让部署时互相覆盖）。
    const string BridgeAssemblyName32 = "TuneLab.ExperimentalPatch.VstBridge";
    const string BridgeAssemblyName64 = "TuneLab.ExperimentalPatch.VstBridge.x64";

    // 杀桥后等它真退出再放共享段的上限（见 KillBridge 里那段说明）。正常几毫秒。
    const int KillExitWaitMs = 2000;

    Process? mProcess;
    JobHandle? mJob;
    NamedPipeClientStream? mPipe;

    // [P5.1] Unix 专用：宿主持有的"存活管道"写端。进程一死它被内核回收 → 桥的看门狗读到
    // EOF 并带走自己的进程组。Windows 上为 null（走 JobObject，见 §9.4）。
    AnonymousPipeServerStream? mLiveness;

    // [P5.2] 当前桥的管道名（带宿主 PID）。KillBridge 时据此删掉桥留下的 UDS socket 文件。
    string? mPipeName;

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
                var parts = new List<string>();

                if (LastStartError is { Length: > 0 })
                    parts.Add(LastStartError);

                if (LastTimeoutError is { Length: > 0 })
                    parts.Add(LastTimeoutError);

                if (LastFrameError is { Length: > 0 })
                    parts.Add(LastFrameError);

                if (LastProtocolError is { Length: > 0 })
                    parts.Add(LastProtocolError);

                parts.AddRange(mErrorLines);

                return parts.Count == 0 ? null : string.Join(" | ", parts);
            }
        }
    }

    public void ClearBridgeError()
    {
        lock (mErrorLines)
            mErrorLines.Clear();

        LastStartError = null;
        LastEditorError = null;
        LastTimeoutError = null;
        LastFrameError = null;
        LastProtocolError = null;
    }

    // 最近一次"桥回了对不上的结果"（例如要 clap 的扫描，回来的是 vst3 的条目）。
    // 只有版本错配（旧桥不认识新字段）会走到这里；它不是崩溃，但那份结果不能信。
    public string? LastProtocolError { get; private set; }

    // 启动阶段失败的原因（beyond the bridge's own stderr）。
    public string? LastStartError { get; private set; }

    // 最近一次"消息本身超过协议帧上限"的原因（只有 state 可能这么大）。
    //
    // 【为什么要拦】超上限的帧**根本不能写**：对端读长度时会判非法、只读走那 4 个字节
    // 就返回，剩下的字节全留在管道里 —— 之后每一帧都对不上，等于把桥废掉。
    // 拦下来只是"这一次操作失败"，桥还能继续用。
    public string? LastFrameError { get; private set; }

    // 编辑器最近一次失败的原因（超时 / 插件拒绝）。界面可以直接显示给用户。
    public string? LastEditorError { get; private set; }

    // 最近一次"发出去了但没在时限内回来"的原因（加载 / state / 参数 / 卸载超时）。
    // 超时即杀桥，所以这条也意味着"桥已经被杀过、下一次操作会重启一座新的"。
    public string? LastTimeoutError { get; private set; }

    // 一次"发一条指令 + 读一条回复"必须原子：管道是单条流，
    // UI 线程改参数、音频线程发块，两条来回一交错就会把回复读错人。
    readonly SemaphoreSlim mRoundTrip = new(1, 1);

    // 音频往返标签：每发一块 +1。桥原样回传，宿主只认自己刚发的那个 ——
    // 超时留在环里的旧块不会冒充这一块的结果（见 BlockHeader.Tag）。
    int mProcessTag;

    readonly int mBitness;
    bool mDisposed;

    // 等一块音频的上限。由宿主按「效果器插件缓冲时间」设置（InsertAudioTuning.BlockWaitMs）：
    // 它至少要覆盖一块音频的时长，否则桥稍微忙一点就必然超时。桥卡死时这一块直通，
    // 下一块照常发；连续失败到 AudioStallWatch 的阈值后整条插入停用。
    static int BlockWaitMs => InsertAudioTuning.BlockWaitMs;

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

    // [P5.1] 单测用：当前桥进程的 PID（没起 / 已退出 = null）。验证"自成进程组 / 被杀掉"。
    internal int? BridgeProcessIdForTest =>
        mProcess is { HasExited: false } process ? process.Id : null;

    // [P5.1] 单测用：只放掉存活管道的写端（不杀桥），模拟"宿主进程死掉、写端被回收"。
    // 正常的宿主死亡路径就是这一件事：进程没了 → 写端被内核关掉 → 桥的看门狗读到 EOF。
    internal void CloseLivenessForTest() => mLiveness?.Dispose();

    // 桥在 HelloAck 里自报能承载的格式（见 EnsureStartedAsync）。桥没起 / 旧桥 = null。
    public string[]? BridgeFormats { get; private set; }

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

        // [P4 补] 桥进程已经没了、管道却可能还报"连着"（Windows 的 IsConnected 是缓存的标志）。
        // 插件崩溃、或用户在任务管理器里结束子进程，走的就是这条路：必须按"桥已死"处理。
        if (mProcess is { HasExited: true })
            KillBridge();

        if (IsRunning)
            return true;

        // [P4 补] 走到这里说明要起一个新桥：先把上一轮彻底收掉（含音频路的停滞标记，
        // 见 KillBridgeCore 末尾）。插件崩溃时管道自己断开、**没有任何代码调 KillBridge**，
        // 于是 50 块失败把停滞标记打上；不在这里复位的话，新桥起来后每块都被当成
        // "桥赶不上"直接直通 —— 界面显示已隔离，用户却永远听不到这个插入的效果。
        KillBridge();

        // [P5.2] 每进程扫一次残留：上一次宿主/桥被强杀可能留下段文件与 UDS socket。
        BridgeResidue.SweepOnce();

        // ── 1. 先建作业：必须在启动子进程**之前**，
        //    否则"我们崩在启动与加入之间"的那一瞬，桥就成孤儿了。
        //    [P5.1] 作业是 Windows 的孤儿防线；Unix 用存活管道 + 进程组（见下），不建作业。
        if (OperatingSystem.IsWindows())
        {
            mJob ??= JobHandle.Create();
            if (mJob == null)
                return false;
        }

        // ── 2. 找桥的可执行文件。就在我们自己的 exe 旁边（一起发布）。
        string? bridge = FindBridgeExecutable(mBitness);
        if (bridge == null)
        {
            LastStartError = $"no bridge executable for rid {BridgeRid(mBitness)} beside {AppContext.BaseDirectory}";
            return false;
        }

        // ── 3. 启动。把作业句柄与管道名递给它。
        //     管道名带位数：两个桥可以同时活着，不能撞。
        string pipeName = BridgeProtocol.PipeName + "-" + mBitness + "-" + Environment.ProcessId;

        // 共享音频段在启动**之前**建好：桥起来后会 Open 这两段，找不到直接退出。
        // 名字由管道名派生，两端永远不会对不上。
        string inName = pipeName + ".in";
        string outName = pipeName + ".out";

        // [P5.2] 上一轮桥被强杀会留下同名 UDS socket（桥自己没机会 unlink），同名重绑会
        // EADDRINUSE。此刻还没起桥（IsRunning 为假），先删掉再起。
        BridgeResidue.DeletePipeSocket(pipeName);
        mPipeName = pipeName;

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
        catch (Exception ex)
        {
            mInput = null;
            mOutput = null;
            LastStartError = "creating the shared audio rings failed: " + ex.Message;
            return false;
        }

        // [P5.1] Unix：开一根存活管道。写端留在宿主这边，读端（fd）交给桥 —— 宿主怎么死，
        // 写端都被内核回收，桥的看门狗读到 EOF 就会带走自己的进程组（§9.4）。
        string livenessArg = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                mLiveness?.Dispose();
                mLiveness = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                livenessArg = " --liveness " + mLiveness.GetClientHandleAsString();
            }
            catch (Exception ex)
            {
                mLiveness?.Dispose();
                mLiveness = null;
                LastStartError = "creating the liveness pipe failed: " + ex.Message;
                return false;
            }
        }

        var start = new ProcessStartInfo
        {
            FileName = bridge,
            // 桥是我们启动的私有实例，不共用一个固定的管道名 —— 两个 TuneLab 同时开会撞。
            Arguments = $"--pipe {pipeName}{livenessArg}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,

            // 桥的诊断信息全在 stderr 上（"找不到共享段"、"作业加入失败"…）。
            // 不接就没有任何办法知道它为什么没起来 —— 用户只会看到"插件加载失败"。
            RedirectStandardError = true,
        };

        mProcess = Process.Start(start);
        if (mProcess == null)
        {
            LastStartError = "Process.Start returned no process for the bridge";
            return false;
        }

        // 立刻把它收进作业。作业句柄不能靠命令行递给子进程（那是进程本地的句柄），
        // 只能由我们在自己这边拿到子进程句柄再加进去。
        //
        // 【那一小段竞争】"启动 → 加入"之间我们若崩，桥就成了孤儿。窗口是微秒级，
        // 而且是**我们崩**这种已经很难看的情形；比起"句柄传递处处不生效"，
        // 这个取舍划算。加入失败就把刚起的进程杀掉，绝不留一个游离的桥。
        // [P5.1] 只在 Windows 收作业；Unix 的孤儿防线是存活管道（桥侧看门狗），无需这一步。
        if (OperatingSystem.IsWindows() && (mJob == null || !mJob.Assign(mProcess)))
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
            // [EP-MIXER-27] 必须真正解析回执：版本不匹配时桥回的是 Ok=false，
            // 而 `Contains("\"ok\"")` 会把它当成成功 —— 然后按错的共享环布局解读音频。
            string? ack = await ExchangeAsync(JsonSerializer.Serialize(new HelloMessage
            {
                Version = BridgeProtocol.ProtocolVersion,
                Bitness = mBitness,
                Formats = ["vst2"],
            }), cancellation).ConfigureAwait(false);

            HelloAckMessage? helloAck = null;
            try
            {
                helloAck = ack == null ? null : JsonSerializer.Deserialize<HelloAckMessage>(ack);
            }
            catch (JsonException)
            {
                helloAck = null;
            }

            if (helloAck == null || !helloAck.Ok)
            {
                LastStartError = helloAck?.Reason ?? "the VST2 bridge did not accept the handshake";
                KillBridge();
                return false;
            }

            // 桥自报能承载的格式（x64 桥含 vst3）。宿主据此决定要不要把 VST3 交给它隔离
            // （VST3-BRIDGE.md §5.2）。旧桥不带这个字段 → 空数组 → 只当 vst2。
            BridgeFormats = helloAck.Formats;

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
    // format：PluginFormats.Vst2（缺省）/ Vst3 / Clap / Jsfx。桥据此选宿主实现。
    // probeOnly：只验证（JSFX 不跑 @init），扫描用；见 LoadPluginMessage.ProbeOnly。
    public async Task<LoadPluginResultMessage?> TryLoadAsync(int slot, string pluginPath, double sampleRate,
        int maxBlock, bool bypass, CancellationToken cancellation = default, string format = PluginFormats.Vst2,
        string? pluginId = null, string? importRoot = null, bool probeOnly = false)
    {
        if (!IsRunning)
            return null;

        var message = JsonSerializer.Serialize(new LoadPluginMessage
        {
            Slot = slot,
            Path = pluginPath,
            Format = format,

            // 一个 .clap 里可能有多个插件：VST2 / VST3 传 null（一个文件一个插件）。
            PluginId = pluginId,

            // JSFX 的 import 根；其余格式传 null。
            ImportRoot = importRoot,
            SampleRate = sampleRate,
            MaxBlock = maxBlock,
            Bypass = bypass,
            ProbeOnly = probeOnly,
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

    // 打开插件的原生编辑器。窗口在**桥进程**里（见 Vst2EditorWindow），这里只等它的回执。
    // 失败原因原样带回去（"no editor" / "refused"），界面可以据此禁用按钮。
    // ChildWindows 是桥那头"宿主窗口里到底挂了几个子窗口"：0 = 插件返回了却没建 GUI
    // （依赖缺失那一类，用户看到的就是空窗口）。
    public async Task<(bool Ok, string? Reason, int ChildWindows, long WindowHandle)> TryOpenEditorAsync(int slot,
        string title, CancellationToken cancellation = default)
    {
        if (!IsRunning)
            return (false, "the bridge is not running", 0, 0);

        try
        {
            string? reply = await ExchangeAsync(JsonSerializer.Serialize(new OpenEditorMessage
            {
                Slot = slot,
                Title = title,
            }), cancellation).ConfigureAwait(false);

            if (reply == null)
                return (false, "the bridge did not answer", 0, 0);

            var result = JsonSerializer.Deserialize<OpenEditorResultMessage>(reply);
            return result == null
                ? (false, "bad reply", 0, 0)
                : (result.Ok, result.Reason, result.ChildWindows, result.WindowHandle);
        }
        catch (Exception)
        {
            return (false, "the bridge did not answer", 0, 0);
        }
    }

    public async Task<bool> TryCloseEditorAsync(int slot, CancellationToken cancellation = default)
    {
        if (!IsRunning)
            return false;

        try
        {
            string? reply = await ExchangeAsync(
                JsonSerializer.Serialize(new CloseEditorMessage { Slot = slot }),
                cancellation).ConfigureAwait(false);

            return reply != null
                && JsonSerializer.Deserialize<CloseEditorResultMessage>(reply)?.Ok == true;
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

    // ── 扫描（在桥进程里跑）──

    // 让桥去扫（deep = 完整扫描：问每个插件的 factory 要描述）。
    // 返回 null = 桥不可用 / 超时 / 协议错 —— 调用方按"这次扫不出来"处理。
    //
    // format：`vst3`（默认，扫 native 认得的标准位置）或 `clap`（files 是宿主枚举好的
    // .clap 文件清单）。**结果要按 Format 核对**：老桥不认识 format 字段时会照着扫 VST3，
    // 结果会以 vst3 回上来 —— 调用方不核对就会把 VST3 插件当 CLAP 缓存下来。
    public async Task<IReadOnlyList<ScannedPluginInfo>?> TryScanPluginsAsync(bool deep,
        CancellationToken cancellation = default, string format = PluginFormats.Vst3,
        IReadOnlyList<string>? files = null)
    {
        if (!IsRunning)
            return null;

        try
        {
            string? reply = await ExchangeAsync(
                JsonSerializer.Serialize(new ScanPluginsMessage
                {
                    Deep = deep,
                    Format = format,
                    Paths = files == null ? null : new List<string>(files),
                }),
                cancellation).ConfigureAwait(false);

            if (reply == null)
                return null;

            var message = JsonSerializer.Deserialize<PluginListMessage>(reply);

            // 桥没按请求的格式回 = 这一份结果不能信（见上面的说明）。
            if (message != null && format != PluginFormats.Vst3
                && !string.Equals(message.Format, format, StringComparison.OrdinalIgnoreCase))
            {
                LastProtocolError = $"the bridge answered a {format} scan with {message.Format} entries";
                return null;
            }

            return message?.Entries;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // UI 线程用的同步包装。这里给的时间比加载长得多：完整扫描是"逐个问插件要描述"，
    // 几十个插件跑十几秒很正常；给太短会把正常扫描误判成卡死。
    //
    // timeout 可覆盖默认的 ScanTimeout：CLAP 是按文件分批问的（见 ClapBackend），
    // 每一批只装一个插件，卡死一个文件不该让整轮扫描等 90 秒。
    public IReadOnlyList<ScannedPluginInfo>? ScanPluginsBlocking(bool deep, string format = PluginFormats.Vst3,
        IReadOnlyList<string>? files = null, TimeSpan? timeout = null)
        => RunTimed(deep ? "the deep plugin scan" : "the plugin scan",
            () => TryScanPluginsAsync(deep, default, format, files), timeout ?? ScanTimeout,
            (IReadOnlyList<ScannedPluginInfo>?)null);

    // ── UI 线程用的同步包装 ──
    //
    // 【为什么套一层 Task.Run】上面的 await 一旦直接 .GetAwaiter().GetResult() 在 UI 线程上，
    // 会把 UI 线程自己卡死（await 的续体要回 UI 线程执行，而 UI 线程正等着）。
    // Task.Run 把整条链甩到线程池，UI 线程空出来跑续体。
    public bool EnsureStartedBlocking()
        => RunTimed("the bridge handshake", () => EnsureStartedAsync(), StartTimeout, false);

    public LoadPluginResultMessage? LoadBlocking(int slot, string path, double sampleRate, int maxBlock, bool bypass,
        string format = PluginFormats.Vst2, string? pluginId = null, string? importRoot = null, bool probeOnly = false)
        => RunTimed("loading the plugin",
            () => TryLoadAsync(slot, path, sampleRate, maxBlock, bypass, format: format, pluginId: pluginId,
                importRoot: importRoot, probeOnly: probeOnly),
            LoadTimeout, (LoadPluginResultMessage?)null);

    public bool UnloadBlocking(int slot)
    {
        // 卸载要等桥拆编辑器线程（gui->destroy / effEditClose 可能几百毫秒），期间 process 停摆。
        mAudioStall.Suspend();

        try
        {
            return RunTimed("unloading the plugin", () => TryUnloadAsync(slot), TeardownTimeout, false);
        }
        finally
        {
            mAudioStall.Resume();
        }
    }

    // 开编辑器要等插件建 UI（几百毫秒很常见）。给它一个上限：插件在 effEditOpen 里卡死时
    // 不能把宿主 UI 线程一起带走 —— 超时就杀桥，音频本来也已经直通了。
    //
    // 【为什么是 3 秒而不是 15 秒】用户点「界面」之后 UI 线程会等在这里。3 秒以上在交互上
    // 就是"软件卡死了"（用户原话）。真需要更久的插件是少数，而"宁可窗口空着、界面活着"
    // 比"界面冻结十几秒"好 —— 超时后窗口留着，用户还是能继续混音的。
    public (bool Ok, string? Reason, int ChildWindows, long WindowHandle) OpenEditorBlocking(int slot, string title)
    {
        // 桥的主线程在插件建 UI 期间不能处理 process（VST2 的 effEditOpen / CLAP 的
        // gui->create 都要求同一条线程）。这几秒里每一块都拿不到结果，但那不是
        // "桥赶不上了" —— 不挂起判定的话，开一次界面就会把这一行误判成挂起（红 + 永久直通）。
        mAudioStall.Suspend();

        try
        {
            return OpenEditorCore(slot, title);
        }
        finally
        {
            mAudioStall.Resume();
        }
    }

    (bool Ok, string? Reason, int ChildWindows, long WindowHandle) OpenEditorCore(int slot, string title)
    {
        // 让**桥**有资格把窗口带到前台。Windows 只允许"当前前台进程"或获得它授权的进程
        // 调 SetForegroundWindow；桥是被我们启动的子进程，不授权的话它的 SetForegroundWindow
        // 会被静默忽略 —— 表现就是"窗口开了，但藏在宿主窗口下面"（实测 Marvel GEQ）。
        // 用户刚点过我们的按钮，所以我们此刻是前台进程，授权有效。
        try
        {
            if (mProcess is { HasExited: false })
                AllowSetForegroundWindow(mProcess.Id);
        }
        catch (Exception)
        {
            // 授权失败不致命：桥那边还会试一次 SetForegroundWindow，最坏是窗口在下面。
        }
        var task = Task.Run(() => TryOpenEditorAsync(slot, title));

        if (!task.Wait(EditorOpenTimeout))
        {
            LastEditorError = $"the plugin did not open its editor within {EditorOpenTimeout.TotalSeconds:0.#}s";

            // 杀桥：管道里已经错位的回复留着会答错下一个问题（见 EnsureStartedAsync 的注记）。
            KillBridge();
            return (false, LastEditorError, 0, 0);
        }

        return task.Result;
    }

    public bool CloseEditorBlocking(int slot)
    {
        // 关窗要等编辑器线程退完（gui->destroy / effEditClose 在它上面跑），期间 process 停摆。
        mAudioStall.Suspend();

        try
        {
            return RunTimed("closing the editor", () => TryCloseEditorAsync(slot), TeardownTimeout, false);
        }
        finally
        {
            mAudioStall.Resume();
        }
    }

    // 已经开着的窗口再点一次「界面」= 带到前台。桥没开窗（插件没界面 / 已关）时回 false，
    // 界面据此决定是重新开一扇还是什么都不做。
    //
    // 这条**不该**长等：桥侧只是把已有窗口带到前台（SetForegroundWindow / SetWindowPos），
    // 是微秒级的活。等了 3 秒还没回来说明桥卡死了，那时该走的是超时那条路。
    public bool BringEditorToFrontBlocking(int slot)
    {
        var task = Task.Run(async () =>
        {
            if (!IsRunning)
                return false;

            try
            {
                // 复用 OpenEditor：桥侧"已经开着"会走 BringToFront 分支并回 ok。
                string? reply = await ExchangeAsync(JsonSerializer.Serialize(new OpenEditorMessage
                {
                    Slot = slot,
                    Title = string.Empty,
                }), CancellationToken.None).ConfigureAwait(false);

                return reply != null
                    && JsonSerializer.Deserialize<OpenEditorResultMessage>(reply)?.Ok == true;
            }
            catch (Exception)
            {
                return false;
            }
        });

        return task.Wait(RaiseTimeout) && task.Result;
    }

    // 带到前台是微秒级的活：3 秒还没回来说明桥卡死了，那时该走的是超时那条路。
    // ASFW_ANY = -1（允许任意进程）；这里给具体 pid 更克制。
    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    static readonly TimeSpan RaiseTimeout = TimeSpan.FromSeconds(3);

    // 建插件 UI 的上限。3 秒以上在交互上就是"软件卡死了"（用户原话）。
    static readonly TimeSpan EditorOpenTimeout = TimeSpan.FromSeconds(8);

    // 各条"一问一答"的上限。加载 / 卸载要等插件在 Create / effClose 里做完初始化，
    // 给得比参数读写宽；state 可能几百 KB 到 MB 级。
    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan ParameterTimeout = TimeSpan.FromSeconds(5);

    // 扫描的上限：完整扫描 = 几十个插件逐个问描述，十几秒是常态（真机 66 个约 10–20 秒）。
    // 只有"桥卡死在插件里"才会撞到它 —— 那时杀掉桥比让设置页永远转圈好。
    static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(90);

    public byte[]? GetStateBlocking(int slot)
        => RunTimed("reading the plugin state", () => TryGetStateAsync(slot), StateTimeout, (byte[]?)null);

    public bool SetStateBlocking(int slot, byte[] data)
        => RunTimed("writing the plugin state", () => TrySetStateAsync(slot, data), StateTimeout, false);

    public IReadOnlyList<ParameterInfo>? GetParametersBlocking(int slot)
        => RunTimed("reading the plugin parameters", () => TryGetParametersAsync(slot),
            ParameterTimeout, (IReadOnlyList<ParameterInfo>?)null);

    public bool SetParameterBlocking(int slot, long id, double value)
        => RunTimed("writing a plugin parameter", () => TrySetParameterAsync(slot, id, value),
            ParameterTimeout, false);

    // 一条控制消息的统一上限。超时 = 桥里那个插件卡死了（Create / process / state /
    // teardown 里死循环、弹模态框、等一个永远不来的外部资源）——托管侧无从打断它，
    // 只能杀桥。不超时的话，UI 线程会永远停在这里（实测 CHOWTapeModel / ChowMatrix
    // 就卡在 Create，BYOD / ChowCentaur 卡在建第二个实例）。
    T RunTimed<T>(string operation, Func<Task<T>> action, TimeSpan timeout, T fallback)
        => BridgeTimeout.Run(action, timeout, fallback, () =>
        {
            LastTimeoutError = $"{operation} did not answer within {timeout.TotalSeconds:0.#}s; the bridge was killed";
            KillBridge();
        });

    // 音频路径。**不走 await**：它从音频线程调，那里不能等 Task。
    // 数据走共享内存（见 BridgeSharedMemory）：这一侧负责"写输入环、通知桥、等输出环"。
    //
    // 【为什么音频线程可以等】同步语义下就是要等桥把这一块做完——没有别的地方能等。
    // 共享内存让等待变得可超时（桥卡死 20ms 就放弃这块并直通），
    // 而命名管道那条路上同样的等待是不可控的（见文件头的说明）。
    public bool SendProcess(int slot, float[][] inPlanes, float[][] outPlanes, int channels, int frames, bool bypass)
    {
        // 桥已经确定赶不上了：直接直通，不再每块白等 20ms（见 FailAudioBlock）。
        if (mAudioStall.Stalled)
            return false;

        if (mInput == null || mOutput == null)
            return false;

        // 整段（发块 + 等回复 + 取输出）都在这把锁里：
        // 否则 UI 线程正好在改参数时，两个回复会串台。
        if (!mRoundTrip.Wait(BlockWaitMs))
            return FailAudioBlock();

        try
        {
            int tag = unchecked(++mProcessTag);

            // 1) 把输入推进宿主→桥的环，然后通知桥"有一块等你"。
            if (!mInput.TryPublish(slot, inPlanes, channels, frames, BlockWaitMs, tag))
                return FailAudioBlock();

            if (!SendControlBlocking(JsonSerializer.Serialize(new ProcessRequest
                {
                    Slot = slot,
                    Frames = frames,
                    Channels = channels,
                    Bypass = bypass,
                    Tag = tag,
                })))
            {
                return FailAudioBlock();
            }

            // 2) 等桥的结果。**只等共享环**（这一步有 20ms 上限）。
            //
            // 这里原来还要读一条控制管道的回执，而那次读没有超时：桥的主循环卡在插件的
            // process 里时回执永远不来，音频线程就被无限期阻塞（实测：真 VST3 卡死 →
            // 整个宿主死锁）。桥侧已经不再为 ProcessRequest 回执（见 BridgeSession），
            // 于是音频路径上不再有任何"等管道"的动作。
            //
            // 只认 tag 对得上的那块：上一次超时留下的输出还在环里，按"下一个"直接取
            // 就会把别的插入 / 上一块的结果当成这一块，串台之后链上两条插入会互相喂
            // 自己上一块的输出（啸叫的根因）。
            if (!mOutput.TryReadMatching(tag, BlockWaitMs, out _, out var planes, out int outChannels,
                out int outFrames))
            {
                return FailAudioBlock();
            }

            try
            {
                for (int ch = 0; ch < Math.Min(channels, outChannels); ch++)
                    Array.Copy(planes[ch], outPlanes[ch], Math.Min(frames, outFrames));

                mAudioStall.ReportSuccess();
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

    // 一次音频块失败：连续失败到阈值就把桥的音频路**彻底停掉**（之后一律直通）。
    //
    // 为什么要停而不是一直重试：桥卡死（插件死循环 / 进程被挂起）时每块都要白等 20ms，
    // 而 SDL 的缓冲只有 21ms —— 那会把音频拖成断续爆音，比"这个插件在你这台机器上不生效"糟得多。
    // 停掉之后旁通/移除这个效果器即可，混音台其余部分照常。
    bool FailAudioBlock()
    {
        mAudioStall.ReportFailure();
        return false;
    }

    // [P4] 连续失败到阈值就停用音频路。判定抽成 AudioStallWatch：它能给计数喂失败、
    // 脱离真桥单测"到第几块判死 / 杀桥后复位"；这里只负责把一次块失败喂进去。
    readonly AudioStallWatch mAudioStall = new();

    /// <summary>桥的音频路是否已停用（连续失败过多）。宿主据此把这一个插入标红。</summary>
    public bool AudioStalled => mAudioStall.Stalled;

    // [测试] 直接给停滞计数喂一次失败：这一条不需要真插件，也能钉住
    // "换一个新桥之后停滞标记必须清掉"（见 EnsureStartedAsync 里的 KillBridge）。
    internal void ReportAudioFailureForTest() => mAudioStall.ReportFailure();

    // 两处会同时杀桥：超时线程（BridgeTimeout.Run 的 onTimeout），和被打断的那条交换任务
    // （管道被 Dispose 之后它读到 EOF / 抛 IOException，也会走 KillBridge）。
    // 不串行化的话两边都可能看到非 null 的 mInput / mOutput 并各 Dispose 一次 ——
    // 第二次在 MemoryMappedViewAccessor.Dispose（内部 Flush）里抛 ObjectDisposedException。
    // win-arm64 上稳定复现；win-x64 只是时间窗口更小、没撞上。
    readonly object mKillLock = new object();

    /// <summary>宿主主动关闭桥（例如关闭混音台窗口）。</summary>
    public void KillBridge()
    {
        lock (mKillLock)
            KillBridgeCore();
    }

    void KillBridgeCore()
    {
        try
        {
            mPipe?.Dispose();
        }
        catch (Exception)
        {
        }

        mPipe = null;

        // [P5.1] Unix：先放掉存活管道的写端 —— 桥的看门狗一读到 EOF 就会带走自己的进程组
        // （主动关桥时的"协作式"退出路径）。再显式按进程组 SIGKILL 兜底：桥可能还没跑到
        // 装看门狗那一步，或者看门狗本身也卡住了。
        try
        {
            mLiveness?.Dispose();
        }
        catch (Exception)
        {
        }

        mLiveness = null;

        if (!OperatingSystem.IsWindows() && mProcess is { HasExited: false } unixBridge)
        {
            try
            {
                UnixProcess.KillProcessGroup(unixBridge.Id);
            }
            catch (Exception)
            {
            }
        }

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

        // 等桥真的退出再放共享段。Process.Kill 只是投递信号、Dispose 也不等退出：
        // Windows 上段还活着的那个瞬间再 CreateNew 会抛
        // "Cannot create a file when that file already exists." —— 于是"杀桥 → 立刻重启"
        // 就失败（插件直接掉成不可用）。2 秒是上限，超时也照旧往下走，不能卡死关窗。
        var dyingBridge = mProcess;
        mProcess = null;

        if (dyingBridge != null)
        {
            try
            {
                dyingBridge.WaitForExit(KillExitWaitMs);
            }
            catch (Exception)
            {
            }

            dyingBridge.Dispose();
        }

        // 共享段也一起放。桥已经死了，没有对端，留着只是占用一段命名内存。
        mInput?.Dispose();
        mOutput?.Dispose();
        mInput = null;
        mOutput = null;

        // [P5.2] 桥被 SIGKILL 没机会 unlink 自己的 UDS socket 文件，宿主替它删掉。
        BridgeResidue.DeletePipeSocket(mPipeName);
        mPipeName = null;

        // 音频路的失败计数也清零：桥是新的，之前那串"赶不上"不能算到它头上。
        // （不清的话，一次超时杀桥之后停滞态会一直为 true —— 桥重启了、
        //  插件也重新装上了，音频却还是永远直通。）
        mAudioStall.Reset();
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

    // [P5.3] 当前平台 + 位数对应的桥 RID。
    // Windows：win-x86 / win-x64（VST2 是双位数）。Unix：只有 64 位（VST2 是 Windows-only，
    // VST3 锁 64 位，见 VST3-BRIDGE.md §9.1）——32 位在非 Windows 没有产物，返回 null。
    static string? BridgeRid(int bitness)
    {
        if (OperatingSystem.IsWindows())
            return bitness == 32 ? "win-x86" : "win-x64";

        if (bitness == 32)
            return null;

        bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        if (OperatingSystem.IsMacOS())
            return arm ? "osx-arm64" : "osx-x64";

        return arm ? "linux-arm64" : "linux-x64";
    }

    // [P5.3] 可执行文件名：Windows 带 .exe，Unix 的 apphost 不带扩展名。
    static string BridgeExecutableName(int bitness)
    {
        string stem = bitness == 32 ? BridgeAssemblyName32 : BridgeAssemblyName64;
        return OperatingSystem.IsWindows() ? stem + ".exe" : stem;
    }

    // [P5.5] 只查文件、不起进程：设置页的"能不能隔离"与"默认是否开隔离"都用它。
    // 正式布局是 bridge/<rid>/<exe>（见 FindBridgeExecutable）。
    public static bool BridgeExecutableExists => FindBridgeExecutable(64) != null;

    static string? FindBridgeExecutable(int bitness)
    {
        string? rid = BridgeRid(bitness);
        if (rid == null)
            return null;

        string name = BridgeExecutableName(bitness);

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

        // 环境变量指定的桥目录：给测试、便携布局、排查用
        // （TUNELAB_BRIDGE_DIR 可以指到"含各 rid 子目录"的那一层，也可以直接指到某个 rid 目录）。
        // 放在最后：正常安装下永远走上面的固定布局，不会被环境里的残留值带偏。
        string? fromEnv = Environment.GetEnvironmentVariable("TUNELAB_BRIDGE_DIR");
        if (!string.IsNullOrEmpty(fromEnv))
        {
            string[] envCandidates =
            [
                Path.Combine(fromEnv, rid, name),
                Path.Combine(fromEnv, name),
            ];

            foreach (string candidate in envCandidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }
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

        // 有上限地等这把锁：它由 UI 线程与音频线程共用，而 UI 线程可能在写一份很大的状态
        // （采样的 state 是 MB 级）。拿不到就放弃这一块（直通），绝不让音频线程无限等。
        if (!mWriteLock.Wait(BlockWaitMs))
            return false;

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
        // 超上限的帧不能写（见 LastFrameError 的注释）。真实的触发点只有一处：
        // 工程里存着一份超过 16 MB 帧的 VST3 state（base64 后约 12 MB 原始数据）。
        // 判不过就当场失败，让那一次操作如实报错，而不是把整座桥撞死。
        if (!FitsFrame(json, out int frameBytes))
        {
            LastFrameError =
                $"a {frameBytes}-byte message exceeds the bridge frame limit of {BridgeProtocol.MaxFrameBytes} bytes";

            return null;
        }

        await mRoundTrip.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            string? reply;
            try
            {
                await SendControlAsync(json, cancellation).ConfigureAwait(false);
                reply = await ReadControlAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 管道自己的写/读抛异常，同样是"桥死了"。
                //
                // Unix 上这一点是**必需**的：Windows 命名管道的 IsConnected 在服务端进程
                // 消失后会变 false，TryLoad* 开头的 `if (!IsRunning)` 就挡掉了；而 Unix
                // （UDS 模拟）的 IsConnected 一直为真，于是一路走到写，拿到 EPIPE →
                // IOException。不在这里判死，异常会飘到 TryLoad* 的 catch 里只返回 null，
                // 桥明明死了 IsRunning 却还是真，之后每次加载都失败。
                KillBridge();
                return null;
            }

            // 控制管道 EOF（reply == null）说明桥已经没了，而 IsRunning 只看 mPipe.IsConnected
            // —— 偏偏这正是桥崩溃时的形态：进程在，管道断。不收尾的话 IsRunning 永远为 true，
            // 之后每一次 EnsureStarted 都直接返回 true 去用一个断掉的管道，这个位数上的桥
            // 就永久不可用了（要重启宿主）。所以这里当场把桥判死，下一次 EnsureStarted 重建。
            if (reply == null)
                KillBridge();

            return reply;
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

    // 一帧控制消息放不放得下（协议上限同时是两端的"可信长度"闸）。
    static bool FitsFrame(string json, out int bytes)
    {
        bytes = Encoding.UTF8.GetByteCount(json);
        return bytes <= BridgeProtocol.MaxFrameBytes;
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





