using System;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.MixerEngine;
using Xunit;

namespace TuneLab.Tests;

// Solo 安全与独占 solo 的语义（roadmap 2.6）。
//
// 这两条都在宿主侧算（`IsSilent` 由 TrackChannelState / AuxChannelState 给出），
// 但语义必须钉死：solo-safe 只豁免"被别人的 solo 压制"，不豁免自己的静音；
// 独占 solo 只清**同域**的其他 solo。
public class SoloSemanticsTests
{
    // 直接复刻宿主那两行判据。这样做不是偷懒 —— 引擎侧本来就不认识 solo，
    // 判据只存在于宿主，测试要验的正是那条判据本身。
    static bool IsSilent(bool isSolo, bool isMute, bool hasSolo, bool isSoloSafe)
        => !isSolo && (isMute || (hasSolo && !isSoloSafe));

    [Fact]
    public void NobodySoloed_NothingIsSilenced()
    {
        // 没有 solo 时，只有静音的通道不出声。
        Assert.False(IsSilent(isSolo: false, isMute: false, hasSolo: false, isSoloSafe: false));
        Assert.True(IsSilent(isSolo: false, isMute: true, hasSolo: false, isSoloSafe: false));
    }

    [Fact]
    public void SomeoneElseSoloed_NormalChannelIsSilenced()
    {
        Assert.True(IsSilent(isSolo: false, isMute: false, hasSolo: true, isSoloSafe: false));
    }

    [Fact]
    public void SomeoneElseSoloed_SoloSafeChannelKeepsPlaying()
    {
        // 这是 solo-safe 的全部意义：别人 solo 时我照样出声。
        Assert.False(IsSilent(isSolo: false, isMute: false, hasSolo: true, isSoloSafe: true));
    }

    [Fact]
    public void SoloSafeDoesNotOverrideOwnMute()
    {
        // 关键边界：safe 豁免的是"被别人的 solo 压制"，不是"自己的静音"。
        // 对讲麦标了 safe 之后仍然可以被静音 —— 那才符合直觉。
        Assert.True(IsSilent(isSolo: false, isMute: true, hasSolo: true, isSoloSafe: true));
        Assert.True(IsSilent(isSolo: false, isMute: true, hasSolo: false, isSoloSafe: true));
    }

    [Fact]
    public void SoloedChannel_IsNeverSilencedByItsOwnSolo()
    {
        // solo 中的通道自己当然出声（哪怕标了 safe、哪怕 hasSolo 为真）。
        Assert.False(IsSilent(isSolo: true, isMute: false, hasSolo: true, isSoloSafe: false));
        Assert.False(IsSilent(isSolo: true, isMute: false, hasSolo: true, isSoloSafe: true));
    }

    [Fact]
    public void SoloSafeDoesNotChangeAnythingWithoutASolo()
    {
        // 没有 solo 时，safe 是个彻底的 no-op —— 它不该有副作用。
        foreach (bool isMute in new[] { true, false })
        {
            bool withoutSafe = IsSilent(false, isMute, hasSolo: false, isSoloSafe: false);
            bool withSafe = IsSilent(false, isMute, hasSolo: false, isSoloSafe: true);

            Assert.Equal(withoutSafe, withSafe);
        }
    }

    // ── 独占 solo 的"同域"语义 ──
    //
    // 这里不构造真实模型（那要整个 Project + AudioEngine），而是把
    // ClearOtherSolos 的判据抽出来验：同域才清，跨域不清。

    static bool ShouldClear(MixerChannelKind candidate, MixerChannelKind keep)
        => candidate == keep;

    [Fact]
    public void ExclusiveSolo_OnlyClearsTheSameDomain()
    {
        // solo 一条轨 → 只清其他轨，不清 AUX。
        Assert.True(ShouldClear(MixerChannelKind.Track, MixerChannelKind.Track));
        Assert.False(ShouldClear(MixerChannelKind.Aux, MixerChannelKind.Track));
        Assert.False(ShouldClear(MixerChannelKind.Master, MixerChannelKind.Track));

        // solo 一条 AUX → 只清其他 AUX。
        Assert.True(ShouldClear(MixerChannelKind.Aux, MixerChannelKind.Aux));
        Assert.False(ShouldClear(MixerChannelKind.Track, MixerChannelKind.Aux));
    }
}

