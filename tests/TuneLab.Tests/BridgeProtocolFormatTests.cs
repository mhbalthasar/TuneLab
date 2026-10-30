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
}
