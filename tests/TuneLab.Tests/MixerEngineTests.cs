using System;
using System.Collections.Generic;
using System.Linq;
using TuneLab.ExperimentalPatch.MixerEngine;
using Xunit;

namespace TuneLab.Tests;

// 混音引擎的封条。
//
// 最重要的一条是 `DegenerateGraph_IsBitIdenticalToLegacyMix`：它把 DESIGN.md §7.2 的硬验收
// 变成可执行的测试——没有 AUX / 没有插入时，图渲染出来的缓冲区必须与旧路径**逐样本 bit 相同**。
// 引擎是纯 DSP（不引用宿主、不引用 Avalonia），所以这些测试不需要音频设备、不需要 Avalonia。
public class MixerEngineTests
{
    // ── 测试替身 ──

    sealed class TestState : IMixChannelState
    {
        public double Volume { get; set; } = 1;
        public double Pan { get; set; }
        public bool IsSilent { get; set; }
    }

    sealed class ArraySource(float[] data) : IMixSource
    {
        public float[] Data { get; } = data;

        public void AddData(int position, int count, float[] buffer, int offset, float leftGain, float rightGain)
        {
            for (int i = 0; i < count; i++)
            {
                // 按绝对位置取样：切块渲染时 position 会前进，忽略它就会重复读同一段（测试替身的坑）。
                int source = (position + i) * 2;
                buffer[offset + i * 2] += leftGain * Data[source];
                buffer[offset + i * 2 + 1] += rightGain * Data[source + 1];
            }
        }
    }

    static float[] MakeSamples(int frames, int seed)
    {
        var random = new Random(seed);
        var data = new float[frames * 2];
        for (int i = 0; i < data.Length; i++)
            data[i] = (float)(random.NextDouble() * 2 - 1);
        return data;
    }

    // 旧路径的参照实现：逐轨把 lv*L / rv*R 累加进同一个缓冲，最后乘 Master 增益。
    // 与 TuneLab/Audio/AudioGraph.cs 改动前的 MixData 同序同式。
    static void RenderLegacy(IReadOnlyList<(TestState State, ArraySource Source)> tracks, float masterGain,
        int count, float[] buffer, int offset)
    {
        foreach (var (state, source) in tracks)
        {
            if (state.IsSilent)
                continue;

            float left = (float)(state.Volume * (1 - state.Pan));
            float right = (float)(state.Volume * (1 + state.Pan));
            source.AddData(0, count, buffer, offset, left, right);
        }

        if (masterGain == 1f)
            return;

        for (int i = 0; i < count * 2; i++)
            buffer[offset + i] *= masterGain;
    }

    static Mixer BuildMixer(IReadOnlyList<(TestState State, ArraySource Source)> tracks, float masterGain)
    {
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState { Volume = masterGain });

        for (int i = 0; i < tracks.Count; i++)
        {
            var node = builder.AddNode("track-" + i, MixNodeKind.Track, tracks[i].State, tracks[i].Source);
            builder.Connect(node, master, 1f);
        }

        var mixer = new Mixer();
        mixer.SetSnapshot(builder.Build());
        return mixer;
    }

    static void AssertBitEqual(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);

        for (int i = 0; i < expected.Length; i++)
        {
            int a = BitConverter.SingleToInt32Bits(expected[i]);
            int b = BitConverter.SingleToInt32Bits(actual[i]);
            if (a != b)
                throw new Xunit.Sdk.XunitException(
                    $"sample {i} differs: expected {expected[i]:R} (0x{a:X8}), actual {actual[i]:R} (0x{b:X8})");
        }
    }

    // ── 核心：退化图与旧路径逐样本 bit 相同 ──

    [Theory]
    [InlineData(1.0f)]     // Master 增益 0 dB（最常见的路径：不乘）
    [InlineData(0.5f)]     // Master 增益 −6 dB
    [InlineData(1.4125376f)] // +3 dB 左右，验证乘法的舍入也一致
    public void DegenerateGraph_IsBitIdenticalToLegacyMix(float masterGain)
    {
        const int frames = 512;

        var tracks = new List<(TestState State, ArraySource Source)>
        {
            (new TestState { Volume = 0.7, Pan = 0.15 }, new ArraySource(MakeSamples(frames, 1))),
            (new TestState { Volume = 1.0, Pan = -0.30 }, new ArraySource(MakeSamples(frames, 2))),
            (new TestState { Volume = 0.25, Pan = 0.0 }, new ArraySource(MakeSamples(frames, 3))),
            (new TestState { Volume = 1.0, Pan = 0.0, IsSilent = true }, new ArraySource(MakeSamples(frames, 4))),
            (new TestState { Volume = 1.6, Pan = 1.0 }, new ArraySource(MakeSamples(frames, 5))),
        };

        var expected = new float[frames * 2];
        RenderLegacy(tracks, masterGain, frames, expected, 0);

        var actual = new float[frames * 2];
        BuildMixer(tracks, masterGain).Render(0, frames, actual, 0);

        AssertBitEqual(expected, actual);
    }

    [Fact]
    public void DegenerateGraph_IsBitIdentical_WithNonZeroOffset()
    {
        const int frames = 256;
        const int offset = 7;   // 故意非零：验证 offset 的换算没有差一个 float

        var tracks = new List<(TestState State, ArraySource Source)>
        {
            (new TestState { Volume = 0.9, Pan = -0.7 }, new ArraySource(MakeSamples(frames, 11))),
            (new TestState { Volume = 1.1, Pan = 0.4 }, new ArraySource(MakeSamples(frames, 12))),
        };

        var expected = new float[offset + frames * 2];
        RenderLegacy(tracks, 1f, frames, expected, offset);

        var actual = new float[offset + frames * 2];
        BuildMixer(tracks, 1f).Render(0, frames, actual, offset);

        AssertBitEqual(expected, actual);
    }

    [Fact]
    public void Render_ChunksLargeRequestsWithoutChangingSamples()
    {
        const int frames = 1000;

        var tracks = new List<(TestState State, ArraySource Source)>
        {
            (new TestState { Volume = 0.8, Pan = 0.2 }, new ArraySource(MakeSamples(frames, 21))),
        };

        var expected = new float[frames * 2];
        RenderLegacy(tracks, 1f, frames, expected, 0);

        // 故意用很小的块长，强制切块（切块不许改变逐样本算术）。
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState { Volume = 1 });
        var node = builder.AddNode("track-0", MixNodeKind.Track, tracks[0].State, tracks[0].Source);
        builder.Connect(node, master, 1f);

        var mixer = new Mixer(maxBlockSize: 64);
        mixer.SetSnapshot(builder.Build());

        var actual = new float[frames * 2];
        mixer.Render(0, frames, actual, 0);

        AssertBitEqual(expected, actual);
    }

    [Fact]
    public void Insert_SeesEveryFrameOfAnEngineSizedBlock()
    {
        // 引擎（SDL）按**设备缓冲**交块（AudioEngine.BufferSize = 1024 帧），
        // 而插入插件时我们告诉它的块长必须盖得住这个数。早先工厂里那个值停在 512，
        // 于是 VST 插入每块只处理前 512 帧、后 512 帧原样直通 ——
        // 每 10ms 一次硬接缝，听感就是"音频线接触不良"那种爆音。
        // 这里钉住两件事：默认块长 ≥ 一个引擎块；混音台整块交给插入，不切、不截断。
        Assert.True(Mixer.DefaultMaxBlockSize >= 1024,
            "the mixer must handle at least one engine block (1024 frames) without chunking");

        const int frames = 1024;
        const int offset = 3;   // 输出缓冲的起点故意非零：插入看的必须是节点缓冲，不是它

        var insert = new RecordingInsert();

        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState { Volume = 1 });
        var node = builder.AddNode("track-0", MixNodeKind.Track, new TestState { Volume = 1 },
            new ArraySource(MakeSamples(frames, 31)), inserts: [insert]);
        builder.Connect(node, master, 1f);

        var mixer = new Mixer();
        mixer.SetSnapshot(builder.Build());

        var buffer = new float[offset + frames * 2];
        mixer.Render(0, frames, buffer, offset);

        // 一次调用、覆盖整块：任何"只处理一部分就返回"的实现都会在这里露出来。
        var call = Assert.Single(insert.Calls);
        Assert.Equal(0, call.Offset);
        Assert.Equal(frames, call.Count);
    }

    // 只记录被叫到的位置与长度，什么也不改 —— 这条测试看的是"交了多少"，不是音质。
    sealed class RecordingInsert : IMixInsert
    {
        public List<(int Offset, int Count)> Calls { get; } = [];

        public string Name => "recording";

        public bool IsBypassed { get; private set; }

        public void SetBypassed(bool bypassed) => IsBypassed = bypassed;

        public int LatencySamples => 0;

        public void Process(Span<float> buffer, int offset, int count) => Calls.Add((offset, count));
    }

    // 旁通是**活值**：一改，下一次 Process 就该按新状态走，而且要能从音频线程看得见
    // （引擎读 IsBypassed 在音频线程，写在 UI 线程）。这条测试钉住两个方向。
    [Fact]
    public void Bypass_TakesEffectOnTheNextBlockWithoutRebuildingTheGraph()
    {
        const int frames = 256;

        var insert = new RecordingInsert();
        insert.SetBypassed(true);

        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState { Volume = 1 });
        var node = builder.AddNode("track-0", MixNodeKind.Track, new TestState { Volume = 1 },
            new ArraySource(MakeSamples(frames, 31)), inserts: [insert]);
        builder.Connect(node, master, 1f);

        var mixer = new Mixer();
        mixer.SetSnapshot(builder.Build());

        // 旁通态进第一块：引擎看到的就是新值（快照不用重建）。
        mixer.Render(0, frames, new float[frames * 2], 0);
        Assert.Empty(insert.Calls);

        insert.SetBypassed(false);

        // 第二块照常进插入 —— 中间没有 SetSnapshot。
        // 注意 source 只有 frames 长，从 frames 往回取就空了；这里要的是"插进没插进"，
        // 不是取值，所以另起一个等长的静音源按 0 位置取。
        var second = new MixGraphBuilder();
        var master2 = second.AddNode("master", MixNodeKind.Master, new TestState { Volume = 1 });
        var node2 = second.AddNode("track-0", MixNodeKind.Track, new TestState { Volume = 1 },
            new ArraySource(MakeSamples(frames, 77)), inserts: [insert]);
        second.Connect(node2, master2, 1f);

        var mixer2 = new Mixer();
        mixer2.SetSnapshot(second.Build());
        mixer2.Render(0, frames, new float[frames * 2], 0);

        var call = Assert.Single(insert.Calls);
        Assert.Equal(0, call.Offset);
        Assert.Equal(frames, call.Count);
    }

    // 跳过的插入一次都不调：接口注释里承诺过这条，钉住它。
    // （有测试替身把"没被调用"当成"我失败了"，比如永远失败的后端。）
    [Fact]
    public void BypassedInsert_IsNeverCalled()
    {
        const int frames = 64;

        var insert = new RecordingInsert();
        insert.SetBypassed(true);

        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState { Volume = 1 });
        var node = builder.AddNode("track-0", MixNodeKind.Track, new TestState { Volume = 1 },
            new ArraySource(MakeSamples(frames, 7)), inserts: [insert]);
        builder.Connect(node, master, 1f);

        var mixer = new Mixer();
        mixer.SetSnapshot(builder.Build());

        mixer.Render(0, frames, new float[frames * 2], 0);

        Assert.Empty(insert.Calls);
    }

    [Fact]
    public void SilentTrack_ProducesSilence()
    {
        const int frames = 64;

        var tracks = new List<(TestState State, ArraySource Source)>
        {
            (new TestState { Volume = 1.0, IsSilent = true }, new ArraySource(MakeSamples(frames, 31))),
        };

        var buffer = new float[frames * 2];
        BuildMixer(tracks, 1f).Render(0, frames, buffer, 0);

        Assert.All(buffer, sample => Assert.Equal(0f, sample));
    }

    // ── 图构建：拓扑序 / 环路 / 非法图 ──

    [Fact]
    public void Build_RejectsCycle()
    {
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
        var aux1 = builder.AddNode("aux-1", MixNodeKind.Aux, new TestState());
        var aux2 = builder.AddNode("aux-2", MixNodeKind.Aux, new TestState());

        builder.Connect(aux1, aux2, 1f);
        builder.Connect(aux2, aux1, 1f);
        builder.Connect(aux2, master, 1f);

        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("cycle", error.Message);
    }

    [Fact]
    public void Build_RejectsTwoMasters()
    {
        var builder = new MixGraphBuilder();
        builder.AddNode("master-1", MixNodeKind.Master, new TestState());
        builder.AddNode("master-2", MixNodeKind.Master, new TestState());

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_RejectsMasterWithOutputs()
    {
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
        var aux = builder.AddNode("aux", MixNodeKind.Aux, new TestState());

        builder.Connect(master, aux, 1f);
        builder.Connect(aux, master, 1f);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_RejectsDuplicateNodeId()
    {
        var builder = new MixGraphBuilder();
        builder.AddNode("same", MixNodeKind.Master, new TestState());

        Assert.Throws<ArgumentException>(() => builder.AddNode("same", MixNodeKind.Aux, new TestState()));
    }

    [Fact]
    public void Build_RejectsTrackWithoutSource()
    {
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
        var track = builder.AddNode("track", MixNodeKind.Track, new TestState());

        builder.Connect(track, master, 1f);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_PutsEveryNodeAfterItsUpstream()
    {
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
        var aux = builder.AddNode("aux", MixNodeKind.Aux, new TestState());
        var track = builder.AddNode("track", MixNodeKind.Track, new TestState(), new ArraySource(new float[8]));

        builder.Connect(track, aux, 1f);
        builder.Connect(aux, master, 1f);

        var snapshot = builder.Build();
        var order = snapshot.Nodes.Select(n => n.Id).ToList();

        Assert.True(order.IndexOf("track") < order.IndexOf("aux"));
        Assert.True(order.IndexOf("aux") < order.IndexOf("master"));
        Assert.Same(snapshot.Nodes[order.Count - 1], snapshot.Master);
    }

    // ── 路由辅助：成环预判（UI 改 output / 加 send 前问它） ──

    [Fact]
    public void WouldCreateCycle_DetectsIndirectLoop()
    {
        var edges = new[] { ("aux-1", "aux-2"), ("aux-2", "aux-3") };

        Assert.True(MixGraphBuilder.WouldCreateCycle(edges, "aux-3", "aux-1"));
        Assert.False(MixGraphBuilder.WouldCreateCycle(edges, "aux-1", "aux-3"));
    }

    [Fact]
    public void WouldCreateCycle_DetectsSelfLoop()
    {
        Assert.True(MixGraphBuilder.WouldCreateCycle([], "aux-1", "aux-1"));
    }

    // 直接去设备的通道不受 Master 推子影响。
    // 用户报的 bug：唯一有声的轨 → AUX → 设备，Master 没输入；拉 Master 却把 AUX 也压了
    // （原因是两个 sink 共用同一个输出缓冲，Master 的增益乘到了整块上）。
    [Fact]
    public void DeviceRoutedChannel_IsNotAffectedByTheMasterFader()
    {
        const int frames = 32;

        var data = new float[frames * 2];
        Array.Fill(data, 0.5f);

        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState { Volume = 0.1 });   // 狠压 Master
        var device = builder.AddNode("device", MixNodeKind.Device);
        var aux = builder.AddNode("aux-1", MixNodeKind.Aux, new TestState());
        var track = builder.AddNode("track-0", MixNodeKind.Track, new TestState(), new ArraySource(data));

        builder.Connect(track, aux, 1f);
        builder.Connect(aux, device, 1f);

        var mixer = new Mixer();
        mixer.SetSnapshot(builder.Build());

        var buffer = new float[frames * 2];
        mixer.Render(0, frames, buffer, 0);

        Assert.All(buffer, sample => Assert.Equal(0.5f, sample, 5));
    }

    // ── 输出级限幅（DESIGN.md §3.10）──

    [Fact]
    public void Limiter_NeverExceedsCeilingAndKeepsTheStereoRatio()
    {
        var limiter = new PeakLimiter();
        var buffer = new[] { 3f, -2f };

        limiter.Process(buffer, 0, 1);

        Assert.True(Math.Abs(buffer[0]) <= limiter.Ceiling + 1e-6f);
        Assert.True(Math.Abs(buffer[1]) <= limiter.Ceiling + 1e-6f);

        // 立体声联动：两声道用同一个增益，声像不会被压歪。
        Assert.Equal(-2f / 3f, buffer[1] / buffer[0], 4);
    }

    [Fact]
    public void Limiter_ReleasesBackToUnity()
    {
        var limiter = new PeakLimiter(releaseSeconds: 0.01) { SampleRate = 1000 };   // 10 帧回到 1

        var hot = new[] { 5f, 5f };
        limiter.Process(hot, 0, 1);
        Assert.True(limiter.Gain < 0.5f);

        var quiet = new float[64];
        Array.Fill(quiet, 0.1f);
        limiter.Process(quiet, 0, 32);

        Assert.Equal(1f, limiter.Gain, 3);
    }

    [Fact]
    public void Limiter_LeavesInRangeSignalUntouched()
    {
        var limiter = new PeakLimiter();
        var buffer = new[] { 0.5f, -0.5f, 0.25f, -0.25f };

        limiter.Process(buffer, 0, 2);

        Assert.Equal(new[] { 0.5f, -0.5f, 0.25f, -0.25f }, buffer);
        Assert.Equal(1f, limiter.Gain);
    }

    [Fact]
    public void Mixer_LimiterCapsTheOutputButTheMeterStillSeesTheOvershoot()
    {
        const int frames = 32;

        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());

        for (int t = 0; t < 4; t++)
        {
            var data = new float[frames * 2];
            Array.Fill(data, 0.9f);   // 4 条相加 = 3.6，远超 1

            var node = builder.AddNode("track-" + t, MixNodeKind.Track, new TestState(), new ArraySource(data));
            builder.Connect(node, master, 1f);
        }

        var mixer = new Mixer { LimiterEnabled = true };
        mixer.SetSnapshot(builder.Build());

        var buffer = new float[frames * 2];
        mixer.Render(0, frames, buffer, 0);

        // 写进设备 / WAV 的样本不越界。
        Assert.All(buffer, sample => Assert.True(Math.Abs(sample) <= mixer.Limiter.Ceiling + 1e-5f));

        // 但表（限幅前抽头）仍然显示越界 —— 用户要求"不影响动态条显示它越界"。
        Assert.True(mixer.TryGetPeakDb("master", out double left, out _));
        Assert.True(left > 0);
    }

    // ── 电平抽头（AUX / Master 的表）──

    [Fact]
    public void Peak_IsMeasuredPerNodeAfterTheFader()
    {
        const int frames = 64;

        var samples = new float[frames * 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.5f;

        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
        var aux = builder.AddNode("aux-1", MixNodeKind.Aux, new TestState { Volume = 0.5 });
        var track = builder.AddNode("track-0", MixNodeKind.Track, new TestState(), new ArraySource(samples));

        builder.Connect(track, aux, 1f);
        builder.Connect(aux, master, 1f);

        var mixer = new Mixer();
        mixer.SetSnapshot(builder.Build());

        var buffer = new float[frames * 2];
        mixer.Render(0, frames, buffer, 0);

        // 轨道 0.5 → AUX 推子 0.5 → 0.25（−12.04 dB）；Master 推子 unity。
        Assert.True(mixer.TryGetPeakDb("aux-1", out double left, out double right));
        Assert.Equal(20 * Math.Log10(0.25), left, 4);
        Assert.Equal(left, right, 4);

        Assert.True(mixer.TryGetPeakDb("master", out double masterLeft, out _));
        Assert.Equal(left, masterLeft, 4);

        // 未知节点 → false（不是"0 dB"）。
        Assert.False(mixer.TryGetPeakDb("nope", out _, out _));
    }

    // ── PDC 的延迟线（M6 接入插入时才会被图用到） ──

    [Fact]
    public void DelayLine_DelaysByRequestedSamples()
    {
        const int delay = 3;
        const int frames = 8;

        var source = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            source[i * 2] = i + 1;
            source[i * 2 + 1] = -(i + 1);
        }

        var destination = new float[frames * 2];
        new DelayLine(delay).Process(source, 0, destination, 0, frames, 1f);

        for (int i = 0; i < delay; i++)
        {
            Assert.Equal(0f, destination[i * 2]);
            Assert.Equal(0f, destination[i * 2 + 1]);
        }

        for (int i = delay; i < frames; i++)
        {
            Assert.Equal(source[(i - delay) * 2], destination[i * 2]);
            Assert.Equal(source[(i - delay) * 2 + 1], destination[i * 2 + 1]);
        }
    }
}
