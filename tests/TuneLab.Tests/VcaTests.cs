using System;
using System.Collections.Generic;
using TuneLab.Audio;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.Foundation;
using Xunit;

namespace TuneLab.Tests;

// [EP-MIXER-26] VCA 控制组的接缝测试。
//
// VCA 不是音频节点：它没有自己的缓冲，也不在混音图里。它只做一件事 ——
// 成员的 Volume 乘上它的增益。所以这里验的是"乘法确实发生了、且只发生在成员身上"。
//
// 用 AudioGraph 而不是直接构造 TrackChannelState：状态类是宿主的内部实现，
// 而**接缝**（VcaId → VcaBus 的解析、SetVcas 的时机）才是容易做错的地方。
public class VcaTests
{
    const int SampleRate = 48000;

    sealed class FakeData(float[] data, int offset, int count) : IAudioData
    {
        public int Count => count;
        public float GetLeft(int index) => index + offset < data.Length ? data[index + offset] : 0;
        public float GetRight(int index) => index + offset < data.Length ? data[index + offset] : 0;
    }

    sealed class FakeSource(int count, int seed) : IAudioSource
    {
        readonly float[] mData = MakeData(count, seed);

        static float[] MakeData(int count, int seed)
        {
            var data = new float[count];
            var random = new Random(seed);
            for (int i = 0; i < count; i++)
                data[i] = (float)(random.NextDouble() * 2 - 1) * 0.25f;
            return data;
        }

        public double StartTime => 0;
        public int SampleRate => VcaTests.SampleRate;
        public int SampleCount => mData.Length;
        public IAudioData GetAudioData(int offset, int count) => new FakeData(mData, offset, count);
        public void OnSampleRateChanged() { }
    }

    sealed class FakeTrack : IAudioTrack
    {
        public FakeTrack(double volume, params IAudioSource[] sources)
        {
            Volume = volume;
            AudioSources = sources;
        }

        public bool IsMute { get; init; }
        public bool IsSolo { get; init; }
        public bool IsSoloSafe { get; init; }
        public double Volume { get; }
        public double Pan { get; init; }
        public string VcaId { get; init; } = string.Empty;
        public double EndTime => 1000;
        public IEnumerable<IAudioSource> AudioSources { get; }

        public MixerRoute Output { get; init; } = MixerRoute.Master;
        public IReadOnlyList<Send> Sends { get; init; } = [];
        public IReadOnlyDataObjectList<InsertSlot> Inserts { get; init; } = new DataObjectList<InsertSlot>();
    }

    static VcaBus MakeVca(Project project, string id, double gainDb, bool mute = false)
    {
        var vca = new VcaBus(id, "VCA " + id, project);
        vca.Gain.SetInfo(gainDb);
        vca.IsMute.SetInfo(mute);
        return vca;
    }

    static float[] Render(AudioGraph graph, int frames)
    {
        var buffer = new float[frames * 2];
        graph.MixData(0, frames, true, buffer, 0);
        return buffer;
    }

    static float Peak(float[] buffer)
    {
        float peak = 0;
        foreach (float sample in buffer)
            peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }

    [Fact]
    public void VcaGain_MultipliesIntoItsMembers()
    {
        const int frames = 256;

        var withVcaProject = new Project();
        var withVca = new AudioGraph { SampleRate = SampleRate };
        withVca.SetVcas([MakeVca(withVcaProject, "vca-1", -6.0206)]);   // ≈ ×0.5
        withVca.AddTrack(new FakeTrack(1.0, new FakeSource(frames, 11)) { VcaId = "vca-1" });

        // 参照：同一个信号、同一个增益，但作为轨道自己的 Volume —— 结果必须逐位相同。
        var reference = new AudioGraph { SampleRate = SampleRate };
        reference.AddTrack(new FakeTrack(MusicTheory.dB2Level(-6.0206), new FakeSource(frames, 11)));

        var actual = Render(withVca, frames);
        AssertBitEqual(Render(reference, frames), actual);

        // 顺带钉住"确实是半幅"，免得两边一起错。
        var unity = new AudioGraph { SampleRate = SampleRate };
        unity.AddTrack(new FakeTrack(1.0, new FakeSource(frames, 11)));
        Assert.InRange(Peak(actual) / Peak(Render(unity, frames)), 0.49, 0.51);
    }

    [Fact]
    public void VcaAtUnity_IsBitIdenticalToNoVca()
    {
        // 0 dB 的 VCA 必须是彻底的 no-op（乘 1 不能引入任何误差）。
        const int frames = 256;

        var withVca = new AudioGraph { SampleRate = SampleRate };
        withVca.SetVcas([MakeVca(new Project(), "vca-1", 0)]);
        withVca.AddTrack(new FakeTrack(1.0, new FakeSource(frames, 5)) { VcaId = "vca-1" });

        var without = new AudioGraph { SampleRate = SampleRate };
        without.AddTrack(new FakeTrack(1.0, new FakeSource(frames, 5)));

        AssertBitEqual(Render(without, frames), Render(withVca, frames));
    }

    [Fact]
    public void VcaMute_SilencesMembers()
    {
        const int frames = 128;

        var graph = new AudioGraph { SampleRate = SampleRate };
        graph.SetVcas([MakeVca(new Project(), "vca-1", 0, mute: true)]);
        graph.AddTrack(new FakeTrack(1.0, new FakeSource(frames, 3)) { VcaId = "vca-1" });

        foreach (float sample in Render(graph, frames))
            Assert.Equal(0f, sample);
    }

    [Fact]
    public void VcaMute_DoesNotTouchChannelsOutsideTheVca()
    {
        // 关键边界：VCA 静音只作用于成员。非成员必须与"没有 VCA"逐位相同。
        const int frames = 128;

        var graph = new AudioGraph { SampleRate = SampleRate };
        graph.SetVcas([MakeVca(new Project(), "vca-1", 0, mute: true)]);
        graph.AddTrack(new FakeTrack(1.0, new FakeSource(frames, 7)) { VcaId = "vca-1" });
        graph.AddTrack(new FakeTrack(1.0, new FakeSource(frames, 8)));

        var reference = new AudioGraph { SampleRate = SampleRate };
        reference.AddTrack(new FakeTrack(1.0, new FakeSource(frames, 8)));

        AssertBitEqual(Render(reference, frames), Render(graph, frames));
    }

    [Fact]
    public void VcaGain_ScalesPostFaderSendsToo()
    {
        // VCA 乘在通道的 Volume 上，而 post-fader send 取的就是推子后的信号 ——
        // 所以 VCA 拉低时 send 也应该跟着低（这正是"控制组"的预期行为）。
        const int frames = 256;

        var project = new Project();
        var aux = new AuxBus("aux-1", "AUX 1", project);
        var send = new Send(project);
        send.Target.SetInfo(new MixerRoute(MixerOutputKind.Aux, "aux-1", "AUX 1"));

        var graph = new AudioGraph { SampleRate = SampleRate };
        graph.SetVcas([MakeVca(project, "vca-1", -6.0206)]);
        graph.AddAux(aux);
        graph.AddTrack(new FakeTrack(1.0, new FakeSource(frames, 13)) { VcaId = "vca-1", Sends = [send] });

        // 参照：把 VCA 的增益折进轨道自己的 Volume，其余完全一样。
        var referenceProject = new Project();
        var referenceAux = new AuxBus("aux-1", "AUX 1", referenceProject);
        var referenceSend = new Send(referenceProject);
        referenceSend.Target.SetInfo(new MixerRoute(MixerOutputKind.Aux, "aux-1", "AUX 1"));

        var reference = new AudioGraph { SampleRate = SampleRate };
        reference.AddAux(referenceAux);
        reference.AddTrack(new FakeTrack(MusicTheory.dB2Level(-6.0206), new FakeSource(frames, 13)) { Sends = [referenceSend] });

        AssertBitEqual(Render(reference, frames), Render(graph, frames));
    }

    [Fact]
    public void UnknownVcaId_BehavesAsNoVca()
    {
        // 删掉 VCA 之后成员还留着旧 id（撤销删除时会用到）。解析查不到 = 没有 VCA，
        // 不能崩、也不能把增益算成 0。
        const int frames = 128;

        var graph = new AudioGraph { SampleRate = SampleRate };
        graph.SetVcas([]);
        graph.AddTrack(new FakeTrack(1.0, new FakeSource(frames, 17)) { VcaId = "vca-404" });

        var reference = new AudioGraph { SampleRate = SampleRate };
        reference.AddTrack(new FakeTrack(1.0, new FakeSource(frames, 17)));

        AssertBitEqual(Render(reference, frames), Render(graph, frames));
    }

    [Fact]
    public void AuxCanBeAVcaMember()
    {
        // 把几条 AUX 绑在一起也是真实需求（例如"所有混响返回一起收"）。
        const int frames = 256;

        var project = new Project();
        var aux = new AuxBus("aux-1", "AUX 1", project);
        aux.VcaId.SetInfo("vca-1");

        var send = new Send(project);
        send.Target.SetInfo(new MixerRoute(MixerOutputKind.Aux, "aux-1", "AUX 1"));

        var graph = new AudioGraph { SampleRate = SampleRate };
        graph.SetVcas([MakeVca(project, "vca-1", -6.0206)]);
        graph.AddAux(aux);
        graph.AddTrack(new FakeTrack(1.0, new FakeSource(frames, 19)) { Sends = [send] });

        // 参照：VCA 的增益折进 AUX 自己的推子。
        var referenceProject = new Project();
        var referenceAux = new AuxBus("aux-1", "AUX 1", referenceProject);
        referenceAux.Gain.SetInfo(-6.0206);

        var referenceSend = new Send(referenceProject);
        referenceSend.Target.SetInfo(new MixerRoute(MixerOutputKind.Aux, "aux-1", "AUX 1"));

        var reference = new AudioGraph { SampleRate = SampleRate };
        reference.AddAux(referenceAux);
        reference.AddTrack(new FakeTrack(1.0, new FakeSource(frames, 19)) { Sends = [referenceSend] });

        AssertBitEqual(Render(reference, frames), Render(graph, frames));
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
}
