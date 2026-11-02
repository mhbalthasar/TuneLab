using System.Text.Json;
using TuneLab.ExperimentalPatch.VstBridge;
using Xunit;

namespace TuneLab.Tests;

// 桥协议的"格式分派"相关字段（VST3-BRIDGE.md §3）。
//
// 【为什么值得单测】这些字段是**新旧两端之间的封条**：桥与宿主各自编译一份协议、
// 版本可能对不上。这里钉住两件事：
//   1. 旧宿主发的消息（不带新字段）反序列化后落到安全缺省 —— 老行为一条不变；
//   2. 新字段能往返，不会在序列化时悄悄丢掉。
public class BridgeProtocolFormatTests
{
    [Fact]
    public void LoadPlugin_WithoutFormat_DefaultsToVst2()
    {
        // 旧宿主 / 旧测试客户端发的消息一定没有 format。缺省必须是 vst2，
        // 否则一个老客户端会把插件当 VST3 递给只有 VST2 宿主实现的桥。
        var message = JsonSerializer.Deserialize<LoadPluginMessage>(
            """{"slot":1,"path":"C:\\x\\a.dll"}""");

        Assert.NotNull(message);
        Assert.Equal(PluginFormats.Vst2, message!.Format);
    }

    [Fact]
    public void LoadPlugin_Vst3Format_RoundTrips()
    {
        string json = JsonSerializer.Serialize(new LoadPluginMessage
        {
            Slot = 7,
            Path = @"C:\x\a.vst3",
            Format = PluginFormats.Vst3,
            SampleRate = 48000,
            MaxBlock = 512,
        });

        var back = JsonSerializer.Deserialize<LoadPluginMessage>(json);

        Assert.NotNull(back);
        Assert.Equal(PluginFormats.Vst3, back!.Format);
        Assert.Equal(7, back.Slot);
    }

    [Fact]
    public void ParameterInfo_DefaultValue_RoundTrips()
    {
        string json = JsonSerializer.Serialize(new ParameterInfo
        {
            Id = 3,
            Name = "Drive",
            Min = 0,
            Max = 1,
            Value = 0.25,
            DefaultValue = 0.5,
        });

        var back = JsonSerializer.Deserialize<ParameterInfo>(json);

        Assert.NotNull(back);
        Assert.Equal(0.5, back!.DefaultValue);
    }

    [Fact]
    public void HelloAck_WithoutFormats_DefaultsToEmpty()
    {
        // 旧桥的 HelloAck 没有 formats。宿主据此只当它支持 vst2（见 VstBridgeClient）。
        var ack = JsonSerializer.Deserialize<HelloAckMessage>("""{"ok":true}""");

        Assert.NotNull(ack);
        Assert.Empty(ack!.Formats);
    }

    [Fact]
    public void HelloAck_Formats_RoundTrip()
    {
        string json = JsonSerializer.Serialize(new HelloAckMessage
        {
            Ok = true,
            Formats = [PluginFormats.Vst2, PluginFormats.Vst3],
        });

        var back = JsonSerializer.Deserialize<HelloAckMessage>(json);

        Assert.NotNull(back);
        Assert.Equal(new[] { PluginFormats.Vst2, PluginFormats.Vst3 }, back!.Formats);
    }

    [Fact]
    public void LoadPluginResult_NeedsOpenGl_RoundTrips()
    {
        string json = JsonSerializer.Serialize(new LoadPluginResultMessage
        {
            Slot = 1,
            Ok = true,
            HasEditor = true,
            NeedsOpenGl = true,
        });

        var back = JsonSerializer.Deserialize<LoadPluginResultMessage>(json);

        Assert.NotNull(back);
        Assert.True(back!.NeedsOpenGl);
        Assert.True(back.HasEditor);
    }

    [Fact]
    public void OpenEditorResult_WindowHandle_RoundTrips()
    {
        string json = JsonSerializer.Serialize(new OpenEditorResultMessage
        {
            Slot = 1,
            Ok = true,
            WindowHandle = 0x1234_5678,
        });

        var back = JsonSerializer.Deserialize<OpenEditorResultMessage>(json);

        Assert.NotNull(back);
        Assert.Equal(0x1234_5678, back!.WindowHandle);
    }

    // ── CLAP 扫描（P-C1）──
    //
    // 这三条钉住"加格式不能动老语义"：
    //   · 旧宿主发的 ScanPlugins 没有 format / paths → 必须还是扫 VST3；
    //   · 新字段能往返（否则桥会收到一条不带清单的 CLAP 扫描，扫出空结果）；
    //   · 结果的可用性缺省为 true（旧桥不会带这两个字段）。

    [Fact]
    public void ScanPlugins_WithoutFormat_DefaultsToVst3WithoutPaths()
    {
        var message = JsonSerializer.Deserialize<ScanPluginsMessage>("""{"deep":true}""");

        Assert.NotNull(message);
        Assert.True(message!.Deep);
        Assert.Equal(PluginFormats.Vst3, message.Format);
        Assert.Null(message.Paths);
    }

    [Fact]
    public void ScanPlugins_ClapFormatAndPaths_RoundTrip()
    {
        string json = JsonSerializer.Serialize(new ScanPluginsMessage
        {
            Deep = false,
            Format = PluginFormats.Clap,
            Paths = [@"C:\Program Files\Common Files\CLAP\surge.clap"],
        });

        var back = JsonSerializer.Deserialize<ScanPluginsMessage>(json);

        Assert.NotNull(back);
        Assert.Equal(PluginFormats.Clap, back!.Format);
        Assert.Single(back.Paths!);
        Assert.EndsWith("surge.clap", back.Paths![0]);
    }

    [Fact]
    public void ScannedPlugin_AvailabilityDefaultsToAvailable()
    {
        var entry = JsonSerializer.Deserialize<ScannedPluginInfo>(
            """{"format":"clap","pluginId":"clap:com.x.y","name":"Y"}""");

        Assert.NotNull(entry);
        Assert.True(entry!.IsAvailable);
        Assert.Null(entry.UnavailableReason);
    }

    [Fact]
    public void PluginList_Format_DefaultsToVst3AndRoundTrips()
    {
        var old = JsonSerializer.Deserialize<PluginListMessage>("""{"deep":true}""");

        Assert.NotNull(old);
        Assert.Equal(PluginFormats.Vst3, old!.Format);

        var back = JsonSerializer.Deserialize<PluginListMessage>(
            JsonSerializer.Serialize(new PluginListMessage
            {
                Format = PluginFormats.Clap,
                Entries = [new ScannedPluginInfo { Format = "CLAP", PluginId = "clap:a", Name = "A" }],
            }));

        Assert.NotNull(back);
        Assert.Equal(PluginFormats.Clap, back!.Format);
        Assert.Single(back.Entries);
        Assert.Equal("CLAP", back.Entries[0].Format);
    }

    // 一帧 state 的实际上限（P5.5：隔离默认开后，state 一律走这条帧）。
    //
    // 帧 = 4 字节长度 + UTF-8 JSON + base64 的 state，两端都会拿 MaxFrameBytes 判非法。
    // 超上限的帧**不能写**：对端只读走那 4 个字节就返回，剩下的字节留在管道里，
    // 之后每一帧都对不上 —— 等于把桥废掉。所以两端都改成"回一条错误、这一次操作失败"。
    // 这里钉住"多大数据起过不去"，免得哪天有人调小 MaxFrameBytes 却不知道代价。
    [Fact]
    public void StateFrameLimitIsSixteenMegabytesOfJson()
    {
        static int FrameBytes(int stateBytes)
            => System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new SetStateMessage
            {
                Slot = 0,
                Base64 = Convert.ToBase64String(new byte[stateBytes]),
            }));

        // 一兆的 state（远比常见 VST3 大：多数只有几十 KB）轻松过。
        Assert.True(FrameBytes(1_000_000) < BridgeProtocol.MaxFrameBytes);

        // 十三兆的 state 过不去 —— 这就是实际天花板（16 MB 帧 ≈ 12 MB 原始数据）。
        Assert.True(FrameBytes(13_000_000) > BridgeProtocol.MaxFrameBytes);
    }
}
