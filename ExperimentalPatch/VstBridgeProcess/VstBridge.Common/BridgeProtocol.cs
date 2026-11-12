using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace TuneLab.ExperimentalPatch.VstBridge;

// 宿主（64 位，AnyCPU）↔ 桥（32 位，x86）之间的协议。
//
// 【为什么是这个形状】桥进程的唯一职责是"加载 32 位插件、收音频、吐音频"。
// 于是协议里只有三类消息：
//   · hello        —— 握手，约定格式与最大块长
//   · 加载 / 卸载   —— 结构变化，低频，允许分配与失败
//   · 每块一次处理 —— 高频，这条路上不分配
//
// 【传输】named pipe，JSON 帧 + 长度前缀。不用二进制是有意的取舍：
// 帧的数量级是"每秒几十个控制消息"，JSON 的额外 CPU 可以忽略；
// 而它让抓包/手写测试客户端/排查协议问题都变成一件容易的事。
// 真正的大块音频**不走 JSON**：见 ProcessRequest.Frames 的注释。
//
// 【为什么 bridge 与宿主能各写一半】这里只放 record / enum，没有平台 API、
// 没有进程对象、没有日志。两端各自引用本文件编译出来的同一套类型
// （宿主侧经 VstBridgeClient 引用），改协议时两边一起编译失败，不会悄悄漂移。
public static class BridgeProtocol
{
    public const string PipeName = "TuneLab.VstBridge";

    // [EP-MIXER-27] 1 → 2：共享音频环的布局变了（MaxChannels 2 → 4）。
    //
    // 为什么必须抬版本：旧桥按 2 声道布局算槽位大小，新宿主按 4 声道写 ——
    // 能连上、能握手，然后按错的步长解读同一段内存，症状是爆音或整段错位。
    // 所以 Hello 里校验版本，不匹配就拒绝（见 BridgeSession.HandleHello）。
    //
    // [啸叫] 2 → 3：ProcessRequest 增加 Tag，块头的 Reserved 改成 Tag。旧桥不认这个
    // 字段，会把超时留下的过期块当成新块处理（两条插入互相喂上一块输出 = 啸叫）。
    //
    // [缓冲] 3 → 4：MaxBlockFrames 2048 → 4096（默认 50 ms 缓冲 @44.1k ≈ 2205 帧，
    // 混音图内部又按 4096 分块）。旧桥按 2048 算槽位大小，新宿主按 4096 写 —— 同一类
    // 布局错位，必须在 Hello 握手时拒绝。
    public const int ProtocolVersion = 4;

    // 约定：一条消息 = 4 字节小端长度 + UTF-8 JSON。
    // 上限 16 MB：足够装一个自己的大 state chunk，也挡住了"长度头读坏了当巨长"这种事故。
    public const int MaxFrameBytes = 16 * 1024 * 1024;
}

// 插件格式名。桥、宿主、扫描缓存都用这几个字面量；写死字符串迟早漂移。
//
// 这里只放**协议值**（小写）。界面上的显示名（"VST3" / "CLAP"）是另一套词，
// 在 IInsertBackend.FormatName 与扫描结果里，两者不要混用。
public static class PluginFormats
{
    public const string Vst2 = "vst2";
    public const string Vst3 = "vst3";

    // CLAP。纯 C ABI：不需要 per-RID 的 native 宿主库，桥自己就能加载 .clap。
    public const string Clap = "clap";

    // JSFX。桥通过 ysfx 的 C ABI 加载 .jsfx 文本；宿主只负责枚举文件与路径设置。
    public const string Jsfx = "jsfx";
}

public enum BridgeMessageKind
{
    // ── 生命周期与握手 ──
    Hello,
    HelloAck,
    Ping,

    // ── 插件管理（低频，允许分配与失败）──
    LoadPlugin,
    LoadPluginResult,
    UnloadPlugin,
    UnloadPluginResult,
    ListPlugins,

    // ── 实时处理（高频）──
    ProcessRequest,
    ProcessResult,

    // ── 插件自带的状态 / 参数 ──
    GetState,
    StateChunk,
    SetState,
    SetStateResult,
    GetParameters,
    ParameterList,
    SetParameter,
    SetParameterResult,

    // ── 自述 ──
    Shutdown,
    Error,

    // ── 插件编辑器（低频，开窗是慢操作）──
    // 追加在末尾：枚举按数值序列化，插在中间会让旧桥把 Shutdown 认成别的消息。
    OpenEditor,
    OpenEditorResult,
    CloseEditor,
    CloseEditorResult,

    // ── 扫描（跑在桥进程里）──
    // 请求复用已有的 ListPlugins；回复单独一个值（同样追加在末尾）。
    PluginList,
}

// 握手。桥起来的头一件事：告诉宿主"我是什么、我能干什么"。
public sealed class HelloMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.Hello;
    [JsonPropertyName("version")]
    public int Version { get; init; } = BridgeProtocol.ProtocolVersion;

    // 桥自己的位数。宿主拿它跟插件的位数比——不一致就拒绝加载，
    // 而不是把错误一路带到音频线程上才发现。
    [JsonPropertyName("bitness")]
    public int Bitness { get; init; }

    // 桥宣布自己支持哪种格式。将来加 VST3-32 位或 JSFX 都在这里加值。
    [JsonPropertyName("formats")]
    public string[] Formats { get; init; } = [];
}

public sealed class HelloAckMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.HelloAck;

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    // 桥能承载的插件格式。x86 桥是 ["vst2"]；x64 桥是 ["vst2","vst3"]（带 ownvst3）。
    // 宿主据此决定"要不要把 VST3 交给这个桥隔离"（VST3-BRIDGE.md §5.2）。
    [JsonPropertyName("formats")]
    public string[] Formats { get; init; } = [];
}

// 加载一个插件。pluginPath 是桥这一侧的本地路径（宿主持有的工程里存的是
// 插件 id/相对路径，桥收到的永远是一个具体文件路径——定位归宿主）。
public sealed class LoadPluginMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.LoadPlugin;

    // 宿主持有的槽位号。之后每个 ProcessRequest 都带它。
    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    // 插件格式（`vst2` / `vst3`）。缺省 `vst2`：旧宿主不带这个字段，
    // 反序列化后保留初值，老行为一条不变。桥据此选哪个宿主实现（见 BridgeSession）。
    [JsonPropertyName("format")]
    public string Format { get; init; } = PluginFormats.Vst2;

    // 一个文件里可能有多个插件（CLAP 的 .clap 可以声明好几个）。VST2 / VST3 一个文件一个插件，
    // 不用这个字段；CLAP 用它指定要建哪一个（descriptor id，不带 `clap:` 前缀）。
    [JsonPropertyName("pluginId")]
    public string? PluginId { get; init; }

    // JSFX 的 import 根目录（用户配置的 Effects 目录）。VST2 / VST3 / CLAP 不用。
    // 缺省 null：桥会让 ysfx 按 .jsfx 文件自身的位置猜。
    [JsonPropertyName("importRoot")]
    public string? ImportRoot { get; init; }

    [JsonPropertyName("sampleRate")]
    public double SampleRate { get; init; }

    [JsonPropertyName("maxBlock")]
    public int MaxBlock { get; init; }

    // 初始旁通态。之后旁通只在 ProcessRequest 里带（每块一个布尔，比"改旁通"消息快）。
    [JsonPropertyName("bypass")]
    public bool Bypass { get; init; }

    // true = 只验证能不能用，不跑插件的初始化代码。
    //
    // 现在只有 JSFX 认这个字段：扫描时要知道"这个 .jsfx 文件 ysfx 到底加载/编译得动吗、
    // 是不是 2 进 2 出"，但不该为了回答这个问题把每个文件的 @init 都执行一遍
    // （@init 是用户代码：读采样文件、开大缓冲都可能有）。探针只到"编译通过、引脚对"为止。
    //
    // 其他格式忽略这个字段（它们没有"不跑 init"这个概念）。旧桥反序列化时也忽略未知字段，
    // 于是退化成一次完整的加载 —— 结论仍然正确，只是慢一点。
    [JsonPropertyName("probe")]
    public bool ProbeOnly { get; init; }
}

public sealed class LoadPluginResultMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.LoadPluginResult;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    // 失败原因。界面上直接显示给用户的那句（例如"位数不匹配"）。
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("vendor")]
    public string? Vendor { get; init; }

    [JsonPropertyName("latency")]
    public int LatencySamples { get; init; }

    // 插件实际吃几个声道（桥按它准备平面缓冲）。
    [JsonPropertyName("inputChannels")]
    public int InputChannels { get; init; }

    [JsonPropertyName("outputChannels")]
    public int OutputChannels { get; init; }

    // AEffect.flags 里的 effFlagsIsSynth（乐器）。扫描时用它把乐器从插入清单里剔掉 ——
    // VST2 没有静态元数据，问插件自己是唯一可靠的办法（过桥问，不在宿主进程里直载）。
    [JsonPropertyName("isSynth")]
    public bool IsSynth { get; init; }

    // AEffect.flags 里的 effFlagsHasEditor（带编辑器）。混音台的「界面」键据此启用/置灰。
    [JsonPropertyName("hasEditor")]
    public bool HasEditor { get; init; }

    // VST3 的界面是否**必须 OpenGL 1.3+**（DPF / NanoVG 那类）。只有 vst3 会置 true；
    // 宿主据此决定"开 GUI 还是退回参数面板"（见 Vst3Insert.NeedsOpenGl）。
    [JsonPropertyName("needsOpenGl")]
    public bool NeedsOpenGl { get; init; }
}

public sealed class UnloadPluginMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.UnloadPlugin;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }
}

public sealed class UnloadPluginResultMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.UnloadPluginResult;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}

// ── 实时处理 ──
//
// 【音频怎么走】音频数据**不放在 JSON 里**。它作为同一帧消息的"附带二进制段"
// 跟在 JSON 头后面，格式见 BridgeAudioFrames。
// 这样做的理由：每秒几千个 float 过 JSON 解析器是把 CPU 烧在错误的地方，
// 而音频线程上每多一毫秒都是一次可听见的毛刺风险。
public sealed class ProcessRequest
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.ProcessRequest;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    // 这一块的帧数。紧跟其后的音频段长度 = Frames × 声道数 × 4 字节。
    [JsonPropertyName("frames")]
    public int Frames { get; init; }

    [JsonPropertyName("channels")]
    public int Channels { get; init; }

    [JsonPropertyName("bypass")]
    public bool Bypass { get; init; }

    // 本块的往返标签（见 BlockHeader.Tag）。桥只处理标签对得上的那块输入，
    // 过期的（上一次超时留下的）直接丢掉；输出原样带回同一个标签。
    [JsonPropertyName("tag")]
    public int Tag { get; init; }
}

public sealed class ProcessResult
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.ProcessResult;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    // false = 插件这一块没处理成功；宿主据此决定是否把该插件旁通掉（记日志）。
    [JsonPropertyName("processed")]
    public bool Processed { get; init; }

    // 桥实际写回共享环的帧数 / 声道数。宿主据此知道该取多少。
    [JsonPropertyName("frames")]
    public int Frames { get; init; }

    [JsonPropertyName("channels")]
    public int Channels { get; init; }
}

// 附带音频段的编帧。每块两个方向各一段（请求带输入、结果带输出）。
// 布局：交织立体声？不——**平面**。VST2 的 AEffect 就是要平面缓冲，
// 桥在收到请求时就地转平面，省掉一次拷贝。
public static class BridgeAudioFrames
{
    // 请求：[L0..Ln, R0..Rn]（若 channels > 2 则依次往后排）。
    public static byte[] Encode(float[][] planes, int channels, int frames)
    {
        var bytes = new byte[channels * frames * 4];
        int offset = 0;

        for (int ch = 0; ch < channels; ch++)
        {
            Buffer.BlockCopy(planes[ch], 0, bytes, offset, frames * 4);
            offset += frames * 4;
        }

        return bytes;
    }

    public static void Decode(ReadOnlySpan<byte> bytes, float[][] planes, int channels, int frames)
    {
        int offset = 0;

        for (int ch = 0; ch < channels; ch++)
        {
            frames = Math.Min(frames, planes[ch].Length);

            if (offset + frames * 4 > bytes.Length)
                return;

            bytes.Slice(offset, frames * 4)
                .CopyTo(MemoryMarshal.AsBytes(planes[ch].AsSpan(0, frames)));

            offset += frames * 4;
        }
    }
}

// ── 状态与参数 ──

public sealed class StateChunkMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.StateChunk;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    // base64：JSON 里塞二进制最省事的办法，也避免管道上的长度歧义。
    [JsonPropertyName("base64")]
    public string Base64 { get; init; } = string.Empty;
}

public sealed class SetStateMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.SetState;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("base64")]
    public string Base64 { get; init; } = string.Empty;
}

public sealed class SetStateResultMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.SetStateResult;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}

public sealed class ParameterInfo
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("min")]
    public double Min { get; init; }

    [JsonPropertyName("max")]
    public double Max { get; init; }

    [JsonPropertyName("value")]
    public double Value { get; init; }

    // 插件声明的默认值（VST3 = VST3Parameter.DefaultValue）。参数面板的"复位"要用。
    // VST2 没有这个概念，桥那边填与 Value 同值即可。
    [JsonPropertyName("defaultValue")]
    public double DefaultValue { get; init; }

    // [EP-MIXER-AUTO] true = 选择型参数（枚举 / 步进）：不进插入参数自动化。
    // 缺省 false = 线性：旧桥没有这个字段，反序列化保持初值 —— 优雅降级成"全部可自动化"。
    // JSFX 用 ysfx_slider_is_enum、CLAP 用 CLAP_PARAM_IS_STEPPED 填；VST3 的 vendored
    // 包装层拿不到 stepCount（native ABI 没带）、VST2 没有参数元数据，两者暂报 false。
    [JsonPropertyName("isDiscrete")]
    public bool IsDiscrete { get; init; }
}

public sealed class ParameterListMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.ParameterList;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("parameters")]
    public List<ParameterInfo> Parameters { get; init; } = [];
}

public sealed class SetParameterMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.SetParameter;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("value")]
    public double Value { get; init; }
}

public sealed class SetParameterResultMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.SetParameterResult;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}

// 任何一种失败（协议错、插件崩、管道断）都用它回话。
// 宿主侧所有 await 调用都要处理"收到的是 Error 而不是想要的 ack"。
public sealed class ErrorMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.Error;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

// Ping —— 心跳。宿主用它判断"桥还活着"（进程在、管道通、没有卡死在插件里）。
public sealed class PingAck
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.Ping;

    [JsonPropertyName("ok")]
    public bool Ok { get; init; } = true;
}

// 取某个槽位的插件状态块（VST2 getChunk）。
public sealed class GetStateMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.GetState;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }
}

// 列某个槽位的全部参数（界面画插件自己的参数条用）。
public sealed class GetParametersMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.GetParameters;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }
}

// 宿主让桥退出。正常关闭路径上的最后一句话（异常关闭靠 JobObject，见 BridgeProgram）。
public sealed class ShutdownAck
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.Shutdown;

    [JsonPropertyName("ok")]
    public bool Ok { get; init; } = true;
}

// ── 插件编辑器 ──

public sealed class OpenEditorMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.OpenEditor;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    // 窗口标题（插件名）。桥是另一个进程，拿不到宿主的界面语言/命名逻辑，所以由宿主给。
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;
}

public sealed class OpenEditorResultMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.OpenEditorResult;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    // 失败原因（"插件没有界面" / "拒绝打开"）。界面可以直接显示。
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    // 宿主窗口里挂了多少个子窗口。0 = 插件返回了却什么也没建（依赖缺失 / GUI 框架
    // 初始化失败：这类故障用户看到的就是"窗口是空的，但没报错"）。宿主据此给一句明话。
    [JsonPropertyName("childWindows")]
    public int ChildWindows { get; init; }

    // 插件窗口的 HWND（VST3 专有）。VST2 的窗口由桥自己建、桥自己置前，这里是 0；
    // VST3 的窗口也由插件自己建，但宿主需要句柄才能把它带到前台 / 查询可见性。
    // 跨进程传句柄只在本机有意义 —— 与这套桥的处境一致。
    [JsonPropertyName("windowHandle")]
    public long WindowHandle { get; init; }
}

public sealed class CloseEditorMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.CloseEditor;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }
}

public sealed class CloseEditorResultMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.CloseEditorResult;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}

// ── 扫描 ──
//
// 【为什么扫描也要过桥】"问插件要一份描述"听起来无害，实际是把插件的二进制加载进
// 本进程 —— 坏插件在这一步就能 abort 掉整个宿主（真机 66 个一起扫时复现过）。
// 所以扫描（尤其完整扫描）同样只能在桥里做：崩了、卡死了都只是桥没了。
//
// 【三种格式的差别】
//   · VST3：宿主只做**纯文件枚举**（.vst3 包、moduleinfo.json、二进制类别标记），
//     带目录的扫描根本不过桥；过桥的这条只负责 native 扫描器认得的标准位置。
//   · CLAP：没有"不加载就能读到的元数据"（描述是运行时的字符串），所以必须过桥加载。
//     宿主仍然负责枚举文件（纯文件读取），把文件清单放在 Paths 里发过来 ——
//     "扫哪 = 用户配的清单"这条不变量因此留在宿主侧。
//   · VST2：不走这条消息（它有自己的加载式扫描，见 Vst2Backend）。
public sealed class ScanPluginsMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.ListPlugins;

    // true = 完整扫描（ScanMode.Full，问描述）；false = 快速扫描（标准位置）。
    // 只对 VST3 有意义；CLAP 的描述本来就是问出来的，两种模式同一条路。
    [JsonPropertyName("deep")]
    public bool Deep { get; init; }

    // 插件格式（`vst3` / `clap`）。缺省 `vst3`：旧宿主不带这个字段，
    // 反序列化后保留初值，老行为一条不变。
    [JsonPropertyName("format")]
    public string Format { get; init; } = PluginFormats.Vst3;

    // 明确的插件**文件**清单（目前只有 CLAP 用）。空 / null = 用该格式的标准位置。
    [JsonPropertyName("paths")]
    public List<string>? Paths { get; init; }
}

// 一条扫描结果。刻意与 InsertPluginInfo 同形，但**不复用**它：
// 这份文件要能被两端各自独立编译，不应该依赖 InsertHost 的类型。
public sealed class ScannedPluginInfo
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = PluginFormats.Vst3;

    [JsonPropertyName("pluginId")]
    public string PluginId { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("vendor")]
    public string Vendor { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("locator")]
    public string Locator { get; init; } = string.Empty;

    [JsonPropertyName("isInstrument")]
    public bool IsInstrument { get; init; }

    // 扫到了、但装不进来（例如 .clap 里没有 clap_entry）。与 InsertPluginInfo 的同名字段同义：
    // 菜单据此灰掉并写出原因，而不是让用户点了才发现。
    [JsonPropertyName("available")]
    public bool IsAvailable { get; init; } = true;

    [JsonPropertyName("unavailableReason")]
    public string? UnavailableReason { get; init; }
}

public sealed class PluginListMessage
{
    [JsonPropertyName("kind")]
    public BridgeMessageKind Kind => BridgeMessageKind.PluginList;

    [JsonPropertyName("deep")]
    public bool Deep { get; init; }

    // 回显请求的格式：宿主据此确认这一份结果不是"桥没看懂格式、拿 VST3 的顶上来"。
    [JsonPropertyName("format")]
    public string Format { get; init; } = PluginFormats.Vst3;

    // 桥这一侧失败时的原因（native 缺失 / 扫描器版本不对）。空 = 结果可信。
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("entries")]
    public List<ScannedPluginInfo> Entries { get; init; } = [];
}

