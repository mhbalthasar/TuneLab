using System.Collections.Generic;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.VstBridge;
using Xunit;

namespace TuneLab.Tests;

// Vst3BridgeInsert 的行为测试：用一个**记账用的假桥通道**覆盖
// "音频往返 / state / 参数 / 旁通 / 桥失败"这些分支，不碰任何原生代码。
//
// 【为什么值得】这是"宿主侧那一半"的唯一可单测面：真的经桥跑要起进程、要有 x64 插件。
// 假通道把协议往返变成确定的函数，于是填充公式写错、旁通没生效、桥失败没直通
// 这几类"跑起来才知道"的 bug 能在这里第一次就抓住。
public class Vst3BridgeInsertTests
{
    sealed class FakeChannel : IBridgeChannel
    {
        public string? LastBridgeError { get; set; }
        public void ClearBridgeError() { }
        public string[]? BridgeFormats { get; set; } = [PluginFormats.Vst2, PluginFormats.Vst3];

        public bool EnsureStartedBlocking() => true;

        int mNextSlot = 1;
        public int AllocateSlot() => mNextSlot++;

        public LoadPluginResultMessage? LoadBlocking(int slot, string path, double sampleRate, int maxBlock,
            bool bypass, string format = PluginFormats.Vst2) => null;

        public List<int> Unloaded { get; } = [];
        public bool UnloadBlocking(int slot)
        {
            Unloaded.Add(slot);
            return true;
        }

        // 默认：原样把输入平面拷到输出平面（identity 插件）。
        public System.Action<int, float[][], float[][], int, int>? ProcessImpl;

        public int ProcessCalls;
        public int LastChannels;
        public int LastFrames;
        public bool LastBypass;

        public bool SendProcess(int slot, float[][] inPlanes, float[][] outPlanes, int channels, int frames, bool bypass)
        {
            ProcessCalls++;
            LastChannels = channels;
            LastFrames = frames;
            LastBypass = bypass;

            if (ProcessImpl != null)
            {
                ProcessImpl(slot, inPlanes, outPlanes, channels, frames);
                return true;
            }

            for (int ch = 0; ch < channels; ch++)
                System.Array.Copy(inPlanes[ch], outPlanes[ch], frames);

            return true;
        }

        public byte[]? State { get; set; }
        public byte[]? GetStateBlocking(int slot) => State;
        public bool SetStateBlocking(int slot, byte[] data)
        {
            State = data;
            return true;
        }

        public List<ParameterInfo> Parameters { get; } = [];
        public IReadOnlyList<ParameterInfo>? GetParametersBlocking(int slot) => Parameters;

        public Dictionary<long, double> ParameterWrites { get; } = [];
        public bool SetParameterBlocking(int slot, long id, double value)
        {
            ParameterWrites[id] = value;
            return true;
        }

        // [P2] 编辑器往返：默认"成功且没有真实句柄"（句柄为 0 时 IsEditorOpen 不做 IsWindow 校验）。
        public long EditorWindow { get; set; }
        public bool EditorOpened { get; private set; }
        public bool EditorClosed { get; private set; }

        public (bool Ok, string? Reason, int ChildWindows, long WindowHandle) OpenEditorBlocking(int slot, string title)
        {
            EditorOpened = true;
            return (true, null, 0, EditorWindow);
        }

        public bool CloseEditorBlocking(int slot)
        {
            EditorClosed = true;
            return true;
        }
    }

    static InsertPluginInfo Plugin() => new()
    {
        Format = "VST3",
        PluginId = "vst3:test",
        Name = "Test",
        Locator = @"C:\fake\test.vst3",
    };

    static Vst3BridgeInsert Make(IBridgeChannel channel, int inputChannels = 2, bool bypassed = false,
        bool hasEditor = false, bool needsOpenGl = false)
        => Vst3BridgeInsert.CreateForTest(Plugin(), channel, slotId: 7, blockSize: 512,
            latencySamples: 0, hasEditor: hasEditor, needsOpenGl: needsOpenGl, bypassed: bypassed,
            inputChannels: inputChannels);

    [Fact]
    public void IsIsolated_IsTrue()
    {
        Assert.True(Make(new FakeChannel()).IsIsolated);
    }

    [Fact]
    public void Process_SendsPlanarInputAndReadsPlanarOutput()
    {
        var channel = new FakeChannel();
        var insert = Make(channel);

        // 2 帧立体声：L = {1,3}, R = {2,4}。
        var buffer = new float[] { 1, 2, 3, 4 };

        insert.Process(buffer, 0, 2);

        Assert.Equal(1, channel.ProcessCalls);
        Assert.Equal(2, channel.LastChannels);
        Assert.Equal(2, channel.LastFrames);
        Assert.False(channel.LastBypass);

        // identity：输出应与输入逐位一致。
        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, buffer);
    }

    [Fact]
    public void Process_AppliesPluginOutput()
    {
        var channel = new FakeChannel
        {
            // 插件把每个声道放大 2 倍。
            ProcessImpl = (_, inPlanes, outPlanes, _, frames) =>
            {
                for (int i = 0; i < frames; i++)
                {
                    outPlanes[0][i] = inPlanes[0][i] * 2f;
                    outPlanes[1][i] = inPlanes[1][i] * 2f;
                }
            },
        };

        var insert = Make(channel);
        var buffer = new float[] { 1, 2, 3, 4 };
        insert.Process(buffer, 0, 2);

        Assert.Equal(new[] { 2f, 4f, 6f, 8f }, buffer);
    }

    [Fact]
    public void Process_WhenBypassed_DoesNotCallTheBridge()
    {
        var channel = new FakeChannel();
        var insert = Make(channel, bypassed: true);

        var buffer = new float[] { 1, 2, 3, 4 };
        insert.Process(buffer, 0, 2);

        Assert.Equal(0, channel.ProcessCalls);
        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, buffer);
    }

    [Fact]
    public void Process_WhenBridgeFails_LeavesTheBlockUntouched()
    {
        // 桥超时 / 死了：SendProcess 返回 false。整块应保持直通（buffer 不动），
        // 上一层（引擎）下一块还会再试；持续失败由宿主决定是否旁通掉这个插入。
        var failing = new FailingChannel();
        var insert = Make(failing);

        var buffer = new float[] { 1, 2, 3, 4 };
        insert.Process(buffer, 0, 2);

        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, buffer);
    }

    sealed class FailingChannel : IBridgeChannel
    {
        public string? LastBridgeError => null;
        public void ClearBridgeError() { }
        public string[]? BridgeFormats => [PluginFormats.Vst3];
        public bool EnsureStartedBlocking() => true;
        public int AllocateSlot() => 1;
        public LoadPluginResultMessage? LoadBlocking(int slot, string path, double sampleRate, int maxBlock,
            bool bypass, string format = PluginFormats.Vst2) => null;
        public bool UnloadBlocking(int slot) => true;
        public bool SendProcess(int slot, float[][] inPlanes, float[][] outPlanes, int channels, int frames, bool bypass) => false;
        public byte[]? GetStateBlocking(int slot) => null;
        public bool SetStateBlocking(int slot, byte[] data) => false;
        public IReadOnlyList<ParameterInfo>? GetParametersBlocking(int slot) => null;
        public bool SetParameterBlocking(int slot, long id, double value) => false;

        public (bool Ok, string? Reason, int ChildWindows, long WindowHandle) OpenEditorBlocking(int slot, string title)
            => (false, "no editor", 0, 0);

        public bool CloseEditorBlocking(int slot) => false;
    }

    [Fact]
    public void State_RoundTripsThroughTheBridge()
    {
        var channel = new FakeChannel { State = [1, 2, 3] };
        var insert = Make(channel);

        Assert.Equal(new byte[] { 1, 2, 3 }, insert.SaveState());

        Assert.True(insert.LoadState(new byte[] { 9, 9 }));
        Assert.Equal(new byte[] { 9, 9 }, channel.State);
    }

    [Fact]
    public void LoadState_WithEmptyData_IsANoOp()
    {
        var channel = new FakeChannel { State = [7] };
        var insert = Make(channel);

        Assert.False(insert.LoadState([]));
        Assert.Equal(new byte[] { 7 }, channel.State);
    }

    [Fact]
    public void Parameters_MapTheProtocolShape()
    {
        var channel = new FakeChannel();
        channel.Parameters.Add(new ParameterInfo
        {
            Id = 42,
            Name = "Drive",
            Min = 0,
            Max = 1,
            Value = 0.25,
            DefaultValue = 0.5,
        });

        var insert = Make(channel);
        var p = Assert.Single(insert.Parameters);

        Assert.Equal(42, p.Id);
        Assert.Equal("Drive", p.Name);
        Assert.Equal(0.25, p.CurrentValue);
        Assert.Equal(0.5, p.DefaultValue);
    }

    [Fact]
    public void SetParameter_GoesThroughTheBridge()
    {
        var channel = new FakeChannel();
        var insert = Make(channel);

        insert.SetParameter(3, 0.75);

        Assert.Equal(0.75, channel.ParameterWrites[3]);
    }

    [Fact]
    public void Dispose_UnloadsTheSlot()
    {
        var channel = new FakeChannel();
        var insert = Make(channel);

        insert.Dispose();

        Assert.Equal(new[] { 7 }, channel.Unloaded);
    }

    [Fact]
    public void Editor_OpensAndClosesThroughTheBridge()
    {
        // 句柄为 0：IsEditorOpen 不做 IsWindow 校验，于是这条测的是"消息往返 + 状态"。
        var channel = new FakeChannel();
        var insert = Make(channel, hasEditor: true);

        Assert.True(insert.HasEditor);
        Assert.False(insert.IsEditorOpen);

        Assert.True(insert.OpenEditor(System.IntPtr.Zero));
        Assert.True(channel.EditorOpened);
        Assert.True(insert.IsEditorOpen);

        insert.CloseEditor();
        Assert.True(channel.EditorClosed);
        Assert.False(insert.IsEditorOpen);
    }

    [Fact]
    public void Editor_ReturnsTheBridgeWindowHandleForRaising()
    {
        // 桥回传插件窗口句柄，宿主用 FindEditorWindowSince 拿它做置前（窗口在桥进程里，
        // 本地枚举不到）。
        var channel = new FakeChannel { EditorWindow = 0x1234 };
        var insert = Make(channel, hasEditor: true);

        insert.OpenEditor(System.IntPtr.Zero);

        Assert.Equal(new System.IntPtr(0x1234), insert.FindEditorWindowSince(new HashSet<System.IntPtr>()));
        Assert.Empty(insert.SnapshotTopLevelWindows());
    }

    [Fact]
    public void Editor_WhenTheBridgeRefuses_DoesNotMarkItOpen()
    {
        var insert = Make(new FailingChannel(), hasEditor: true);

        Assert.False(insert.OpenEditor(System.IntPtr.Zero));
        Assert.False(insert.IsEditorOpen);
    }

    [Fact]
    public void SidechainChannels_FollowTheDeclaredInputWidth()
    {
        Assert.Equal(0, Make(new FakeChannel(), inputChannels: 2).SidechainChannels);
        Assert.Equal(1, Make(new FakeChannel(), inputChannels: 3).SidechainChannels);
        Assert.Equal(2, Make(new FakeChannel(), inputChannels: 4).SidechainChannels);
    }
}
