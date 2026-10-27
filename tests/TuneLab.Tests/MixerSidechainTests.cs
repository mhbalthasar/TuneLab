using System;
using System.Collections.Generic;
using TuneLab.ExperimentalPatch.MixerEngine;
using Xunit;

namespace TuneLab.Tests;

// 侧链输入（roadmap 2.8）的引擎封条。
//
// 这里只测**引擎自己的契约**（键的走向、PDC 对齐、成环拒绝、包装器转发）。
// "插件到底有没有第二个输入"属于后端的事（Vst2Insert / Vst3Insert），需要真插件，
// 不在单测里跑。
public class MixerSidechainTests
{
    // ── 测试替身 ──

    sealed class TestState : IMixChannelState
    {
        public double Volume { get; set; } = 1;
        public double Pan { get; set; }
        public bool IsSilent { get; set; }
        public double TrimDb { get; set; }
        public bool PhaseInvert { get; set; }
        public double HpfHz { get; set; }
    }

    sealed class ArraySource(float[] data) : IMixSource
    {
        public void AddData(int position, int count, float[] buffer, int offset, float leftGain, float rightGain)
        {
            for (int i = 0; i < count; i++)
            {
                int frame = position + i;
                if (frame < 0 || frame * 2 + 1 >= data.Length)
                    continue;

                buffer[offset + i * 2] += leftGain * data[frame * 2];
                buffer[offset + i * 2 + 1] += rightGain * data[frame * 2 + 1];
            }
        }
    }

    // 纯延迟插入：把主信号延后 N 帧，报同样的延迟给 PDC。
    sealed class DelayInsert : IMixInsert
    {
        readonly float[] mBuffer;
        readonly int mDelay;
        int mWrite;

        public DelayInsert(int delay)
        {
            mDelay = delay;
            mBuffer = new float[delay * 2];
        }

        public string Name => "delay";
        public bool IsBypassed => false;
        public int LatencySamples => mDelay;

        public void SetBypassed(bool bypassed) { }

        public void Process(Span<float> buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int at = mWrite * 2;

                float left = mBuffer[at];
                float right = mBuffer[at + 1];

                mBuffer[at] = buffer[offset + i * 2];
                mBuffer[at + 1] = buffer[offset + i * 2 + 1];

                buffer[offset + i * 2] = left;
                buffer[offset + i * 2 + 1] = right;

                mWrite = (mWrite + 1) % mDelay;
            }
        }
    }

    // 探针：记录主信号与键，什么也不改。3 参数重载被调用时 sidechain 记 NaN，
    // 用来区分"引擎走了旧路径"与"传了一个空 span"。
    sealed class ProbeInsert : IMixInsert
    {
        public List<float> MainLeft { get; } = [];
        public List<float> SidechainLeft { get; } = [];
        public bool SawSidechainCall { get; private set; }

        public string Name => "probe";
        public bool IsBypassed => false;
        public int LatencySamples => 0;
        public int InputChannels => 4;
        public int SidechainChannels => 2;

        public void SetBypassed(bool bypassed) { }

        public void Process(Span<float> buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                MainLeft.Add(buffer[offset + i * 2]);
                SidechainLeft.Add(float.NaN);
            }
        }

        public void Process(Span<float> buffer, int offset, int count, ReadOnlySpan<float> sidechain)
        {
            SawSidechainCall = true;

            for (int i = 0; i < count; i++)
            {
                MainLeft.Add(buffer[offset + i * 2]);
                SidechainLeft.Add(sidechain.IsEmpty ? float.NaN : sidechain[i * 2]);
            }
        }
    }

    static float[] Impulse(int frames, int at)
    {
        var data = new float[frames * 2];
        data[at * 2] = 1f;
        data[at * 2 + 1] = 1f;
        return data;
    }

    static float[] Zeros(int frames) => new float[frames * 2];

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

    // ── 对齐：键与"读它的那个插入"的主信号停在同一帧 ──

    [Fact]
    public void SidechainKeyIsAlignedWithTheInsertThatReadsIt()
    {
        const int frames = 64;

        // 源数据在**第 4 帧**有脉冲：PDC 会把音源读提前 maxLatency=4 帧，
        // 于是它在这一块的第 0 帧出现（见 Mixer.RenderChunk 的 SeekOffset）。
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());

        var source = builder.AddNode("track-0", MixNodeKind.Track, new TestState(),
            new ArraySource(Impulse(frames, 4)));

        var probe = new ProbeInsert();
        var target = builder.AddNode("track-1", MixNodeKind.Track, new TestState(),
            new ArraySource(Impulse(frames, 4)), [new DelayInsert(4), probe]);

        builder.Connect(source, master, 1f);
        builder.Connect(target, master, 1f);

        // 键喂给第 2 个插入（下标 1），前面那个 4 样本延迟插入会把它自己的主信号也推后 4 帧。
        builder.ConnectSidechain(source, target, 1);

        var snapshot = builder.Build();
        Assert.Equal(4, snapshot.MaxLatencySamples);
        Assert.Equal(1, snapshot.SidechainBufferCount);

        var mixer = new Mixer();
        mixer.SetSnapshot(snapshot);

        var output = new float[frames * 2];
        mixer.Render(0, frames, output, 0);

        Assert.True(probe.SawSidechainCall, "the insert with a sidechain must be called through the 4-argument overload");

        // 两边都该在第 4 帧：主信号被 DelayInsert 推后 4，键被侧链延迟线推后 4。
        Assert.Equal(1f, probe.MainLeft[4]);
        Assert.Equal(1f, probe.SidechainLeft[4]);

        // 其余帧都静音（对齐错一帧这里就会红）。
        for (int i = 0; i < frames; i++)
        {
            if (i == 4)
                continue;

            Assert.Equal(0f, probe.MainLeft[i]);
            Assert.Equal(0f, probe.SidechainLeft[i]);
        }
    }

    [Fact]
    public void SidechainKeyStaysAlignedWhenTheTargetIsNotOnTheLongestPath()
    {
        const int frames = 64;

        // 第三条轨的 8 样本插入把 maxLatency 拉到 8，但 target 自己没有任何插入、
        // 直连 Master（down[target] = 0）。旧公式会给键补 8 帧，键比主信号晚 8 帧；
        // 新公式用 target 的实际入口延迟（0），键与主信号都在第 0 帧。
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());

        var source = builder.AddNode("track-0", MixNodeKind.Track, new TestState(),
            new ArraySource(Impulse(frames, 8)));

        var probe = new ProbeInsert();
        var target = builder.AddNode("track-1", MixNodeKind.Track, new TestState(),
            new ArraySource(Impulse(frames, 8)), [probe]);

        var slow = builder.AddNode("track-2", MixNodeKind.Track, new TestState(),
            new ArraySource(Zeros(frames)), [new DelayInsert(8)]);

        builder.Connect(source, master, 1f);
        builder.Connect(target, master, 1f);
        builder.Connect(slow, master, 1f);

        builder.ConnectSidechain(source, target, 0);

        var snapshot = builder.Build();
        Assert.Equal(8, snapshot.MaxLatencySamples);

        var mixer = new Mixer();
        mixer.SetSnapshot(snapshot);

        var output = new float[frames * 2];
        mixer.Render(0, frames, output, 0);

        Assert.True(probe.SawSidechainCall);

        // 音源脉冲在第 8 帧；SeekOffset = 8 → 主信号在第 0 帧到达 target 的插入输入。
        // 键也必须停在第 0 帧（旧公式会停在第 8 帧）。
        Assert.Equal(1f, probe.MainLeft[0]);
        Assert.Equal(1f, probe.SidechainLeft[0]);

        for (int i = 1; i < frames; i++)
        {
            Assert.Equal(0f, probe.MainLeft[i]);
            Assert.Equal(0f, probe.SidechainLeft[i]);
        }
    }

    // ── 侧链不参与主混音的延迟补偿 ──

    [Fact]
    public void SidechainEdgeDoesNotChangeMainMixLatencyOrSamples()
    {
        const int frames = 128;

        static (MixSnapshot Snapshot, float[] Output) Render(bool withSidechain, int frames)
        {
            var builder = new MixGraphBuilder();
            var master = builder.AddNode("master", MixNodeKind.Master, new TestState());

            // 源静音：两条图里唯一的贡献来自 target，这样"渲染顺序变了"不会影响加法顺序。
            var source = builder.AddNode("track-0", MixNodeKind.Track, new TestState(),
                new ArraySource(Zeros(frames)));

            var target = builder.AddNode("track-1", MixNodeKind.Track, new TestState(),
                new ArraySource(Impulse(frames, 3)), [new DelayInsert(5)]);

            builder.Connect(source, master, 1f);
            builder.Connect(target, master, 1f);

            if (withSidechain)
                builder.ConnectSidechain(source, target, 0);

            var snapshot = builder.Build();

            var mixer = new Mixer();
            mixer.SetSnapshot(snapshot);

            var output = new float[frames * 2];
            mixer.Render(0, frames, output, 0);

            return (snapshot, output);
        }

        var plain = Render(false, frames);
        var withKey = Render(true, frames);

        // 加一条侧链不改变 maxLatency / down[]：同一个工程"接不接侧链"的主延迟一样。
        Assert.Equal(plain.Snapshot.MaxLatencySamples, withKey.Snapshot.MaxLatencySamples);
        Assert.Equal(5, withKey.Snapshot.MaxLatencySamples);

        AssertBitEqual(plain.Output, withKey.Output);
    }

    // ── 成环在 Build 时拒绝 ──

    [Fact]
    public void MutuallyConnectedSidechainsAreRejectedAsACycle()
    {
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
        var a = builder.AddNode("track-0", MixNodeKind.Track, new TestState(), null, [new ProbeInsert()]);
        var b = builder.AddNode("track-1", MixNodeKind.Track, new TestState(), null, [new ProbeInsert()]);

        builder.Connect(a, master, 1f);
        builder.Connect(b, master, 1f);

        // 单向没问题：b 的出口喂 a 的第 0 个插入。
        builder.ConnectSidechain(b, a, 0);

        // 再加反向 = 环。Kahn 排不完 → Build 抛。
        builder.ConnectSidechain(a, b, 0);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void SelfSidechainIsRejectedImmediately()
    {
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
        var a = builder.AddNode("track-0", MixNodeKind.Track, new TestState());
        builder.Connect(a, master, 1f);

        Assert.Throws<ArgumentException>(() => builder.ConnectSidechain(a, a, 0));
    }

    // ── 没有侧链 = 老路径（不传空 span，而是根本不走 4 参数重载） ──

    [Fact]
    public void GraphWithoutSidechainNeverAllocatesKeyBuffers()
    {
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
        var probe = new ProbeInsert();
        var track = builder.AddNode("track-0", MixNodeKind.Track, new TestState(),
            new ArraySource(Zeros(32)), [probe]);
        builder.Connect(track, master, 1f);

        var snapshot = builder.Build();
        Assert.Equal(0, snapshot.SidechainBufferCount);

        var mixer = new Mixer();
        mixer.SetSnapshot(snapshot);

        var output = new float[32 * 2];
        mixer.Render(0, 32, output, 0);

        Assert.False(probe.SawSidechainCall);
    }

    // ── 包装器必须转发键（漏了只有 mix < 1 / trim ≠ 0 时才会丢，极难发现） ──

    [Fact]
    public void WetDryWrapperForwardsTheSidechain()
    {
        var probe = new ProbeInsert();
        var wrapper = new WetDryInsert(probe, 16) { Mix = 0.5f };

        Assert.Equal(4, wrapper.InputChannels);
        Assert.Equal(2, wrapper.SidechainChannels);

        var buffer = new float[16];
        var key = new float[16];
        Array.Fill(buffer, 0.5f);
        Array.Fill(key, 0.25f);

        wrapper.Process(buffer, 0, 8, key);

        Assert.True(probe.SawSidechainCall);
        Assert.Equal(8, probe.SidechainLeft.Count);
        Assert.All(probe.SidechainLeft, value => Assert.Equal(0.25f, value));
    }

    [Fact]
    public void TrimWrapperForwardsTheSidechain()
    {
        var probe = new ProbeInsert();
        var wrapper = new TrimInsert(probe) { GainDb = 6 };

        Assert.Equal(4, wrapper.InputChannels);
        Assert.Equal(2, wrapper.SidechainChannels);

        var buffer = new float[16];
        var key = new float[16];
        Array.Fill(buffer, 0.5f);
        Array.Fill(key, 0.25f);

        wrapper.Process(buffer, 0, 8, key);

        Assert.True(probe.SawSidechainCall);
        Assert.All(probe.SidechainLeft, value => Assert.Equal(0.25f, value));
    }
}
