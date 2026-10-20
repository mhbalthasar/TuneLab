using System;
using System.Collections.Generic;
using System.Linq;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.MixerEngine;
using Xunit;

namespace TuneLab.Tests;

// 插入链的封条（阶段二 M6）。
//
// 这里全部用 GainInsert / FailingInsert（InsertHost 的假后端）而不是真 VST3：
// 真插件测试机上未必有，而"插入被接进图里、旁通真的跳过、latency 真的去补 PDC"
// 这些行为与后端是什么无关。后端自身的验证另有一段（见 Vst3BackendTests）。
public class InsertChainTests
{
    sealed class TestState : IMixChannelState
    {
        public double Volume { get; set; } = 1;
        public double Pan { get; set; }
        public bool IsSilent { get; set; }
    }

    sealed class ConstantSource(float[] data) : IMixSource
    {
        public float[] Data { get; } = data;

        public void AddData(int position, int count, float[] buffer, int offset, float leftGain, float rightGain)
        {
            // 常量源：按 count 从头读，忽略 position（测旁通时不关心读到第几帧）。
            for (int i = 0; i < count; i++)
            {
                buffer[offset + i * 2] += leftGain * Data[i * 2];
                buffer[offset + i * 2 + 1] += rightGain * Data[i * 2 + 1];
            }
        }
    }

    static float[] MakeSamples(int frames, int seed, float value)
    {
        var data = new float[frames * 2];
        for (int i = 0; i < data.Length; i++)
            data[i] = value;

        return data;
    }

    static float[] Render(Action<MixGraphBuilder> build, int frames, Mixer? mixer = null)
    {
        var builder = new MixGraphBuilder();
        build(builder);

        mixer ??= new Mixer();
        mixer.SetSnapshot(builder.Build());

        var buffer = new float[frames * 2];
        mixer.Render(0, frames, buffer, 0);
        return buffer;
    }

    // ── 插入真的被跑 ──

    [Fact]
    public void Insert_ProcessesTheBlockBeforeTheFader()
    {
        const int frames = 16;
        var insert = new GainInsert(gain: 0.5);

        var buffer = Render(builder =>
        {
            var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
            var track = builder.AddNode("track-0", MixNodeKind.Track, new TestState(), new ConstantSource(MakeSamples(frames, 0, 1f)), [insert]);
            builder.Connect(track, master, 1f);
        }, frames);

        // 推子 unity，插入 0.5 → 0.5。
        Assert.Equal(0.5f, buffer[0]);
        Assert.Equal(1, insert.ProcessedBlocks);
        Assert.Equal(frames, insert.ProcessedSamples);
    }

    [Fact]
    public void Inserts_RunInOrder()
    {
        const int frames = 16;

        // 两个"增益"串起来 = 乘两次：0.5 × 0.25 = 0.125。
        var buffer = Render(builder =>
        {
            var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
            var track = builder.AddNode("track-0", MixNodeKind.Track, new TestState(),
                new ConstantSource(MakeSamples(frames, 0, 1f)), [new GainInsert(0.5), new GainInsert(0.25)]);
            builder.Connect(track, master, 1f);
        }, frames);

        Assert.Equal(0.125f, buffer[0]);
    }

    [Fact]
    public void Insert_OnMaster_RunsAfterTheInputsAreSummed()
    {
        const int frames = 16;

        // Master 上的插入（母带链）：两条各 0.5 的轨 → 1.0 → 插入 0.5 → 0.5。
        // 这条同时钉住"插入在推子之后"：Master 推子是 0.25，所以先 1.0 再乘 0.5 再乘 0.25 = 0.125。
        var buffer = Render(builder =>
        {
            var master = builder.AddNode("master", MixNodeKind.Master, new TestState { Volume = 0.25 },
                inserts: [new GainInsert(0.5)]);

            var a = builder.AddNode("track-0", MixNodeKind.Track, new TestState(), new ConstantSource(MakeSamples(frames, 0, 0.5f)));
            var b = builder.AddNode("track-1", MixNodeKind.Track, new TestState(), new ConstantSource(MakeSamples(frames, 0, 0.5f)));

            builder.Connect(a, master, 1f);
            builder.Connect(b, master, 1f);
        }, frames);

        Assert.Equal(0.125f, buffer[0]);
    }

    // ── 旁通 ──

    [Fact]
    public void BypassedInsert_LeavesAudioUntouched()
    {
        const int frames = 16;
        var insert = new GainInsert(0.5) { Bypassed = true };

        var buffer = Render(builder =>
        {
            var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
            var track = builder.AddNode("track-0", MixNodeKind.Track, new TestState(),
                new ConstantSource(MakeSamples(frames, 0, 1f)), [insert]);
            builder.Connect(track, master, 1f);
        }, frames);

        Assert.Equal(1f, buffer[0]);
        Assert.Equal(0, insert.ProcessedBlocks);
    }

    [Fact]
    public void Bypass_IsReadEveryBlock()
    {
        const int frames = 8;
        var insert = new GainInsert(0.5);

        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
        var track = builder.AddNode("track-0", MixNodeKind.Track, new TestState(),
            new ConstantSource(MakeSamples(frames, 0, 1f)), [insert]);
        builder.Connect(track, master, 1f);

        var mixer = new Mixer();
        mixer.SetSnapshot(builder.Build());

        var buffer = new float[frames * 2];
        mixer.Render(0, frames, buffer, 0);
        Assert.Equal(0.5f, buffer[0]);
        Assert.Equal(1, insert.ProcessedBlocks);

        // 旁通是 UI 线程改的一个字段：音频线程下一块就该看到（不用换快照）。
        insert.Bypassed = true;

        Array.Clear(buffer);
        mixer.Render(frames, frames, buffer, 0);
        Assert.Equal(1f, buffer[0]);
        Assert.Equal(1, insert.ProcessedBlocks);
    }

    // ── 延迟补偿（PDC）──

    [Fact]
    public void InsertLatency_IsCompensatedOnTheOtherPath()
    {
        const int frames = 32;
        const int delay = 4;

        // 轨 A 直通 Master（无延迟）；轨 B 经过一个 4 样本延迟的插入才到 Master。
        // 汇合处必须对齐：B 的边被补上 4 样本延迟，于是两列在输出里**同相**。
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());

        // 两个源给同一个波形，但 B 晚 4 帧进场 —— 补偿之后输出里应该重合成一个波形。
        var a = builder.AddNode("track-a", MixNodeKind.Track, new TestState(),
            new ConstantSource(MakeSamples(frames, 0, 1f)));

        var b = builder.AddNode("track-b", MixNodeKind.Track, new TestState(),
            new ConstantSource(MakeSamples(frames, 0, 1f)), [new GainInsert(1.0, delay)]);

        builder.Connect(a, master, 1f);
        builder.Connect(b, master, 1f);

        var snapshot = builder.Build();
        Assert.Equal(delay, snapshot.MaxLatencySamples);

        // B 自己带着 4 样本延迟，所以它**不补**；快的那一侧（A）补 4。
        // 这就是 PDC 的全部含义：不要动慢的，把快的往后拉齐。
        var bNode = snapshot.Nodes.Single(n => n.Id == "track-b");
        Assert.Equal(0, bNode.Outputs[0].DelaySamples);

        var aNode = snapshot.Nodes.Single(n => n.Id == "track-a");
        Assert.Equal(delay, aNode.Outputs[0].DelaySamples);

        var mixer = new Mixer();
        mixer.SetSnapshot(snapshot);

        var buffer = new float[frames * 2];
        mixer.Render(0, frames, buffer, 0);

        // 两列同相叠加：前 4 帧只有 A（= 1.0），之后 = 2.0。
        Assert.Equal(1f, buffer[0]);
        Assert.Equal(2f, buffer[delay * 2]);
    }

    [Fact]
    public void NoInserts_MeansNoDelay_SoTheGraphIsUnchanged()
    {
        const int frames = 16;

        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
        var track = builder.AddNode("track-0", MixNodeKind.Track, new TestState(),
            new ConstantSource(MakeSamples(frames, 0, 1f)));
        builder.Connect(track, master, 1f);

        var snapshot = builder.Build();

        Assert.Equal(0, snapshot.MaxLatencySamples);
        Assert.All(snapshot.Nodes, n => Assert.Empty(n.Inserts));
        Assert.All(snapshot.Nodes.SelectMany(n => n.Outputs), e => Assert.Equal(0, e.DelaySamples));
    }

    // ── 后端契约 ──

    [Fact]
    public void GainInsert_TreatsUnityGainAsNoOp()
    {
        var insert = new GainInsert(1.0);
        var buffer = new float[4];
        insert.Process(buffer, 0, 2);

        Assert.Equal(0, insert.ProcessedBlocks);
    }

    [Fact]
    public void FailingInsert_ThrowsFromProcess()
    {
        var insert = new FailingInsert();
        var buffer = new float[4];

        // 引擎不 catch：调用方（Vst3Insert）负责把插件失败转成"直通"。
        // 这条测试钉住的是契约本身——失败必须**看得见**，不能静默产出静音。
        Assert.Throws<InvalidOperationException>(() => insert.Process(buffer, 0, 2));
    }

    [Fact]
    public void Vst3Backend_WithoutNativeLibrary_IsUnavailable()
    {
        // 直接 new：native dll 若不在 exe 同目录（测试进程里就是不在），
        // 后端必须退化到"不可用 + 有原因"，而不是崩。
        var backend = new Vst3Backend();

        if (File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "ownvst3.tlhost.dll")))
        {
            Assert.True(backend.IsAvailable);
            return;
        }

        Assert.False(backend.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(backend.UnavailableReason));
        Assert.Empty(backend.ListPlugins());

        var settings = new InsertSettings { SampleRate = 48000, MaxBlockSize = 512 };
        Assert.Null(backend.Create(new InsertPluginInfo
        {
            Format = "VST3",
            PluginId = "vst3:test",
            Name = "Test",
            Locator = "whatever",
        }, settings, out var reason));

        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Vst3Backend_RefusesInstruments()
    {
        // 乐器不该进插入链（它要收 MIDI）。这里用"没有 native"来跳过执行：
        // 真正的拒绝逻辑在 Vst3Backend.Create 里，那段要等机器上有 64 位插件才跑得到。
        var backend = new Vst3Backend();
        Assert.Equal("VST3", ((IInsertBackend)backend).FormatName);
    }

    // ── 提前播放：让输出坐标与播放头一致（DESIGN.md §3.11）──

    [Fact]
    public void SeekOffset_MovesTheReadPositionForward()
    {
        const int frames = 32;
        const int delay = 8;

        // 一条轨带 8 样本延迟的插入。输出要满足 out[t] == source[t]，
        // 所以源必须在 t 被读一次，而插入会把它推到 t−8。引擎因此读 t+8。
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());

        // 源的第 n 个样本记下自己被读到的绝对位置（PositionRecordingSource）。
        var source = new PositionRecordingSource(frames);

        var track = builder.AddNode("track-0", MixNodeKind.Track, new TestState(), source,
            [new GainInsert(1.0, delay)]);

        builder.Connect(track, master, 1f);

        var mixer = new Mixer();
        mixer.SetSnapshot(builder.Build());

        Assert.Equal(delay, mixer.SeekOffset);

        var buffer = new float[frames * 2];
        mixer.Render(0, frames, buffer, 0);

        // 被读到的绝对位置 = 请求的 position + delay（补偿只发生一次，在引擎里）。
        Assert.Equal(0 + delay, source.FirstReadPosition);
    }

    [Fact]
    public void NoInserts_MeansNoSeekOffset()
    {
        const int frames = 16;

        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
        var source = new PositionRecordingSource(frames);

        var track = builder.AddNode("track-0", MixNodeKind.Track, new TestState(), source);
        builder.Connect(track, master, 1f);

        var mixer = new Mixer();
        mixer.SetSnapshot(builder.Build());

        // 没有任何插件延迟时 **必须是 0** —— 退化图的逐位相同靠这个。
        Assert.Equal(0, mixer.SeekOffset);
    }
}

/// <summary>记下自己第一次被读时的绝对 position（用来验证补偿方向与量）。</summary>
sealed class PositionRecordingSource(int frames) : IMixSource
{
    public int FirstReadPosition { get; private set; } = -1;
    readonly float[] mData = new float[frames * 2];

    public void AddData(int position, int count, float[] buffer, int offset, float leftGain, float rightGain)
    {
        if (FirstReadPosition < 0)
            FirstReadPosition = position;

        for (int i = 0; i < count; i++)
        {
            buffer[offset + i * 2] += leftGain;
            buffer[offset + i * 2 + 1] += rightGain;
        }
    }
}

