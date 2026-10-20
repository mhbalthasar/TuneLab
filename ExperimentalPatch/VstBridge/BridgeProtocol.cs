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
    public const int ProtocolVersion = 1;

    // 约定：一条消息 = 4 字节小端长度 + UTF-8 JSON。
    // 上限 16 MB：足够装一个自己的大 state chunk，也挡住了"长度头读坏了当巨长"这种事故。
    public const int MaxFrameBytes = 16 * 1024 * 1024;
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

    [JsonPropertyName("sampleRate")]
    public double SampleRate { get; init; }

    [JsonPropertyName("maxBlock")]
    public int MaxBlock { get; init; }

    // 初始旁通态。之后旁通只在 ProcessRequest 里带（每块一个布尔，比"改旁通"消息快）。
    [JsonPropertyName("bypass")]
    public bool Bypass { get; init; }
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

