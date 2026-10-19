using System;
using System.Collections.Generic;
using System.Linq;
using TuneLab.Audio;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.Foundation;
using Xunit;

namespace TuneLab.Tests;

// 宿主接缝的封条：`AudioGraph.MixData` 走图之后，结果必须与"逐轨 AddData 到同一个缓冲"完全一致。
//
// 引擎自己的单测（MixerEngineTests）证明的是"图的算术与旧算法等价"；
// 这一组证明的是**接缝没有翻译错**——编译器、源适配器、Master 增益的位置。
// 两者都要有：前者防引擎回归，后者防接缝回归（接缝是上游合并后最容易被改坏的地方）。
public class MixerAudioSeamTests
{
    const int SampleRate = 48000;

    sealed class FakeData(float[] data, int offset, int count) : IAudioData
    {
        public int Count => count;

        public float GetLeft(int index) => index + offset < data.Length ? data[index + offset] : 0;

        public float GetRight(int index) => index + offset < data.Length ? data[index + offset] : 0;
    }

    sealed class FakeSource : IAudioSource
    {
        readonly float[] mData;
        readonly int mStartSample;

        public FakeSource(int startSample, int count, int seed)
        {
            mStartSample = startSample;
            mData = new float[count];
            var random = new Random(seed);
            for (int i = 0; i < count; i++)
                mData[i] = (float)(random.NextDouble() * 2 - 1);

            SampleCount = count;
        }

        public double StartTime => (double)mStartSample / SampleRate;
        public int SampleRate => MixerAudioSeamTests.SampleRate;
        public int SampleCount { get; }

        public IAudioData GetAudioData(int offset, int count) => new FakeData(mData, offset, count);

        public void OnSampleRateChanged() { }
    }

    sealed class FakeTrack : IAudioTrack
    {
        public FakeTrack(double volume, double pan, bool mute = false, bool solo = false, params IAudioSource[] sources)
        {
            Volume = volume;
            Pan = pan;
            IsMute = mute;
            IsSolo = solo;
            AudioSources = sources;
        }

        public bool IsMute { get; }
        public bool IsSolo { get; }
        public double Volume { get; }
        public double Pan { get; }
        public double EndTime => 1000;
        public IEnumerable<IAudioSource> AudioSources { get; }

        // 路由：默认直接去 Master、没有 send。
        public MixerRoute Output { get; init; } = MixerRoute.Master;
        public IReadOnlyList<Send> Sends { get; init; } = [];
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

    [Fact]
    public void MixData_MatchesPerTrackAddData()
    {
        const int frames = 300;

        var graph = new AudioGraph { SampleRate = SampleRate };

        // 两条轨、其中一条的片段从第 100 个样本开始（验证 offset 换算）。
        var first = new FakeTrack(0.8, -0.2, false, false, new FakeSource(0, frames, 7));
        var second = new FakeTrack(1.0, 0.5, false, false, new FakeSource(100, frames, 9));

        graph.AddTrack(first);
        graph.AddTrack(second);

        // 参照：逐轨 AddData（= 旧 MixData 的循环体），再乘 Master 增益。
        var expected = new float[frames * 2];
        graph.AddData(first, 0, frames, true, expected, 0);
        graph.AddData(second, 0, frames, true, expected, 0);

        float masterGain = (float)MusicTheory.dB2Level(AudioEngine.MasterGain);
        if (masterGain != 1f)
        {
            for (int i = 0; i < expected.Length; i++)
                expected[i] *= masterGain;
        }

        var actual = new float[frames * 2];
        graph.MixData(0, frames, true, actual, 0);

        AssertBitEqual(expected, actual);
    }

    [Fact]
    public void MixData_SkipsMutedTrack()
    {
        const int frames = 128;

        var graph = new AudioGraph { SampleRate = SampleRate };
        graph.AddTrack(new FakeTrack(1.0, 0, mute: true, solo: false, new FakeSource(0, frames, 3)));
        graph.AddTrack(new FakeTrack(1.0, 0, mute: false, solo: false, new FakeSource(0, frames, 4)));

        var buffer = new float[frames * 2];
        graph.MixData(0, frames, true, buffer, 0);

        // 静音那条不该有任何贡献：结果必须与"只渲染第二条"完全相同。
        var onlySecond = new AudioGraph { SampleRate = SampleRate };
        onlySecond.AddTrack(new FakeTrack(1.0, 0, mute: false, solo: false, new FakeSource(0, frames, 4)));

        var expected = new float[frames * 2];
        onlySecond.MixData(0, frames, true, expected, 0);

        AssertBitEqual(expected, buffer);
    }

    [Fact]
    public void MixData_SoloSilencesOtherTracks()
    {
        const int frames = 128;

        var graph = new AudioGraph { SampleRate = SampleRate };
        graph.AddTrack(new FakeTrack(1.0, 0, mute: false, solo: false, new FakeSource(0, frames, 3)));
        graph.AddTrack(new FakeTrack(1.0, 0, mute: false, solo: true, new FakeSource(0, frames, 4)));

        var buffer = new float[frames * 2];
        graph.MixData(0, frames, true, buffer, 0);

        var onlySolo = new AudioGraph { SampleRate = SampleRate };
        onlySolo.AddTrack(new FakeTrack(1.0, 0, mute: false, solo: true, new FakeSource(0, frames, 4)));

        var expected = new float[frames * 2];
        onlySolo.MixData(0, frames, true, expected, 0);

        AssertBitEqual(expected, buffer);
    }

    [Fact]
    public void MixData_WithoutTracks_IsSilent()
    {
        var graph = new AudioGraph { SampleRate = SampleRate };
        var buffer = new float[64];

        graph.MixData(0, 32, true, buffer, 0);

        Assert.All(buffer, sample => Assert.Equal(0f, sample));
    }

    // ── 路由（M4）──

    [Fact]
    public void TrackRoutedToAux_GoesThroughTheAuxFader()
    {
        const int frames = 256;

        var graph = new AudioGraph { SampleRate = SampleRate };
        var project = new Project();
        var aux = new AuxBus("aux-1", "AUX 1", project);
        aux.Gain.SetInfo(-6);
        graph.AddAux(aux);

        var track = new FakeTrack(1.0, 0, false, false, new FakeSource(0, frames, 7))
        {
            Output = new MixerRoute(MixerOutputKind.Aux, "aux-1", "AUX 1"),
        };
        graph.AddTrack(track);

        var actual = new float[frames * 2];
        graph.MixData(0, frames, true, actual, 0);

        // 期望：轨道信号 × AUX 推子（−6 dB）。
        var expected = new float[frames * 2];
        graph.AddData(track, 0, frames, true, expected, 0);

        float gain = (float)MusicTheory.dB2Level(-6);
        for (int i = 0; i < expected.Length; i++)
            expected[i] *= gain;

        AssertBitEqual(expected, actual);
    }

    // AUX 的 solo 只压其他 AUX，不影响轨道（DESIGN.md §3.3 的两个 solo 域）。
    [Fact]
    public void AuxSolo_SilencesOtherAuxesButNotTracks()
    {
        const int frames = 128;

        var graph = new AudioGraph { SampleRate = SampleRate };
        var project = new Project();

        var aux1 = new AuxBus("aux-1", "AUX 1", project);
        var aux2 = new AuxBus("aux-2", "AUX 2", project);
        aux2.IsSolo.SetInfo(true);

        graph.AddAux(aux1);
        graph.AddAux(aux2);

        var toAux = new FakeTrack(1, 0, false, false, new FakeSource(0, frames, 11))
        {
            Output = new MixerRoute(MixerOutputKind.Aux, "aux-1", "AUX 1"),
        };
        var toMaster = new FakeTrack(1, 0, false, false, new FakeSource(0, frames, 12));

        graph.AddTrack(toAux);
        graph.AddTrack(toMaster);

        var actual = new float[frames * 2];
        graph.MixData(0, frames, true, actual, 0);

        // 期望：只有去 Master 的那条轨（aux-1 被 aux-2 的 solo 压掉；轨道本身不受影响）。
        var expected = new float[frames * 2];
        graph.AddData(toMaster, 0, frames, true, expected, 0);

        AssertBitEqual(expected, actual);
    }
}
