using System;
using TuneLab.ExperimentalPatch.MixerEngine;
using Xunit;

namespace TuneLab.Tests;

// send 的 pre/post、声向、静音（roadmap 2.2）。
//
// 核心性质：**pre-fader 的 send 不受通道推子影响**，post-fader 的受影响。
// 这条要靠引擎"把推子从 AddData 的折叠里拆出来"才成立 —— 折叠的话 buffer
// 一诞生就带推子，再也取不回推子前的信号。
public class MixerSendStageTests
{
    sealed class TestState : IMixChannelState
    {
        public double Volume { get; set; } = 1;
        public double Pan { get; set; }
        public bool IsSilent { get; set; }
        public double TrimDb { get; set; }
        public bool PhaseInvert { get; set; }
        public double HpfHz { get; set; }
    }

    // 常量音源：验证"推子影响不影响 send"用固定值最直白。
    sealed class ConstantSource(float left, float right) : IMixSource
    {
        public void AddData(int position, int count, float[] buffer, int offset, float leftGain, float rightGain)
        {
            for (int i = 0; i < count; i++)
            {
                buffer[offset + i * 2] += leftGain * left;
                buffer[offset + i * 2 + 1] += rightGain * right;
            }
        }
    }

    sealed class EdgeGain(float left, float right, bool silent) : IMixEdgeGain
    {
        public float Gain => left;
        public float LeftGain { get; } = left;
        public float RightGain { get; } = right;
        public bool IsSilent { get; } = silent;
    }

    const int Frames = 64;

    const float SourceLevel = 0.5f;

    // 建一张 track → {aux, master} 的图。track 的推子/声向和 send 的边由 caller 定。
    // 返回 (aux 输出, master 输出)：send 的效果看 aux，直达看 master。
    static (float[] Aux, float[] Master) Render(
        Action<MixGraphBuilder, MixGraphBuilder.NodeDraft, MixGraphBuilder.NodeDraft> build,
        TestState? trackState = null)
    {
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState { Volume = 1 });
        var aux = builder.AddNode("aux", MixNodeKind.Aux, new TestState { Volume = 1 });
        builder.Connect(aux, master);

        var track = builder.AddNode("track", MixNodeKind.Track,
            trackState ?? new TestState { Volume = 0.5, Pan = 0 },
            new ConstantSource(SourceLevel, SourceLevel));

        build(builder, track, aux);

        var mixer = new Mixer();
        mixer.SetSnapshot(builder.Build());
        var output = new float[Frames * 2];
        mixer.Render(0, Frames, output, 0);

        return (output, output);
    }

    [Fact]
    public void PreFaderSend_IgnoresTheChannelFader()
    {
        // 推子拉到底，pre-fader 的 send 仍把原样信号送进 AUX。
        var (aux, _) = Render((builder, track, auxNode) =>
        {
            builder.Connect(track, auxNode, 1f, 0, new EdgeGain(1f, 1f, false), isPreFader: true);
        }, new TestState { Volume = 0.001, Pan = 0 });

        Assert.Equal(SourceLevel, aux[0], 4);
        Assert.Equal(SourceLevel, aux[1], 4);
    }

    [Fact]
    public void PostFaderSend_FollowsTheChannelFader()
    {
        // 同一个推子、同一个 send 增益，只是 post-fader —— 结果必须按推子衰减。
        var (aux, _) = Render((builder, track, auxNode) =>
        {
            builder.Connect(track, auxNode, 1f, 0, new EdgeGain(1f, 1f, false), isPreFader: false);
        }, new TestState { Volume = 0.5, Pan = 0 });

        // 0.5（源） × 0.5（推子） = 0.25
        Assert.Equal(SourceLevel * 0.5f, aux[0], 4);
        Assert.Equal(SourceLevel * 0.5f, aux[1], 4);
    }

    [Fact]
    public void SendPan_SteersLeftAndRightIndependently()
    {
        // send 自己的声向：全左 → 左满右零。与通道条的 pan 无关。
        var (aux, _) = Render((builder, track, auxNode) =>
        {
            builder.Connect(track, auxNode, 1f, 0, new EdgeGain(1f, 0f, false), isPreFader: true);
        }, new TestState { Volume = 1, Pan = 0 });

        Assert.Equal(SourceLevel, aux[0], 4);
        Assert.Equal(0f, aux[1], 4);
    }

    [Fact]
    public void MutedSend_ContributesNothing()
    {
        var (aux, _) = Render((builder, track, auxNode) =>
        {
            builder.Connect(track, auxNode, 1f, 0, new EdgeGain(1f, 1f, true), isPreFader: true);
        }, new TestState { Volume = 1, Pan = 0 });

        Assert.Equal(0f, aux[0], 6);
        Assert.Equal(0f, aux[1], 6);
    }

    [Fact]
    public void SendGain_ScalesBothChannels()
    {
        // -6 dB ≈ 0.5012。与 pre/post 无关，纯粹验证增益换算走的是 LeftGain/RightGain。
        var (aux, _) = Render((builder, track, auxNode) =>
        {
            builder.Connect(track, auxNode, 1f, 0, new EdgeGain(0.5011872f, 0.5011872f, false), isPreFader: true);
        }, new TestState { Volume = 1, Pan = 0 });

        Assert.Equal(SourceLevel * 0.5011872f, aux[0], 4);
        Assert.Equal(SourceLevel * 0.5011872f, aux[1], 4);
    }

    // 建一张图：track 同时连 master（直达）与 aux（send 的目标），aux 再汇进 master。
    // track 的推子/声向和 send 的边由 caller 定。
    static (float[] Aux, float[] Master) RenderWithDirectOut(
        Action<MixGraphBuilder, MixGraphBuilder.NodeDraft, MixGraphBuilder.NodeDraft> build,
        TestState? trackState = null)
    {
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState { Volume = 1 });
        var aux = builder.AddNode("aux", MixNodeKind.Aux, new TestState { Volume = 1 });
        builder.Connect(aux, master);

        var track = builder.AddNode("track", MixNodeKind.Track,
            trackState ?? new TestState { Volume = 0.5, Pan = 0 },
            new ConstantSource(SourceLevel, SourceLevel));

        builder.Connect(track, master);   // 直达
        build(builder, track, aux);

        var mixer = new Mixer();
        mixer.SetSnapshot(builder.Build());
        var output = new float[Frames * 2];
        mixer.Render(0, Frames, output, 0);

        return (output, output);
    }

    [Fact]
    public void PreFaderSend_StillLetsTheTrackReachMaster()
    {
        // pre-fader 的 send 不该"劫走"信号：直达 Master 的那份仍然按推子走。
        var (_, master) = RenderWithDirectOut((builder, track, auxNode) =>
        {
            builder.Connect(track, auxNode, 1f, 0, new EdgeGain(1f, 1f, false), isPreFader: true);
        }, new TestState { Volume = 0.5, Pan = 0 });

        // Master = 直达(0.5×0.5) + AUX 汇入(0.5，pre-fader 不受推子影响)
        Assert.Equal(SourceLevel * 0.5f + SourceLevel, master[0], 4);
    }
}
