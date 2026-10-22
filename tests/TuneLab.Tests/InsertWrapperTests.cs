using System;
using TuneLab.ExperimentalPatch.MixerEngine;
using Xunit;

namespace TuneLab.Tests;

// 插入的湿/干混合与增益微调（roadmap 2.4）。
//
// 两个包装器的共同纪律：**默认值必须完全透明**。mix = 1 时连干信号拷贝都跳过，
// gain = 0 dB 时连一次乘法都不做 —— 这样"没用这两个功能"的工程与旧路径逐位相同
// （DESIGN §7.2 的硬验收在插入链上的延伸）。
public class InsertWrapperTests
{
    // 把输入乘 2 的假插件：湿信号与干信号能一眼分开。
    sealed class Doubler : IMixInsert
    {
        public string Name => "doubler";
        public bool IsBypassed => false;
        public int LatencySamples => 0;
        public int ProcessCalls;

        public void SetBypassed(bool bypassed) { }

        public void Process(Span<float> buffer, int offset, int count)
        {
            ProcessCalls++;
            for (int i = 0; i < count * 2; i++)
                buffer[offset + i] *= 2f;
        }
    }

    const int Frames = 32;

    static float[] Render(IMixInsert insert, float input = 1f)
    {
        var buffer = new float[Frames * 2];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = input;

        insert.Process(buffer, 0, Frames);
        return buffer;
    }

    // ── 湿/干 ──

    [Fact]
    public void WetDry_FullWet_IsTransparent()
    {
        // mix = 1（默认）：包装器不该拷任何东西，直接透传。
        var insert = new WetDryInsert(new Doubler(), Frames) { Mix = 1f };
        var output = Render(insert);

        Assert.Equal(2f, output[0], 5);
    }

    [Fact]
    public void WetDry_FullDry_UsesTheOriginalSignal()
    {
        // mix = 0：插件照样跑（保持内部状态），但输出完全用干信号。
        var doubler = new Doubler();
        var insert = new WetDryInsert(doubler, Frames) { Mix = 0f };
        var output = Render(insert);

        Assert.Equal(1f, output[0], 5);
        Assert.Equal(1, doubler.ProcessCalls);   // 插件确实被调用了
    }

    [Fact]
    public void WetDry_HalfMix_AveragesBoth()
    {
        var insert = new WetDryInsert(new Doubler(), Frames) { Mix = 0.5f };
        var output = Render(insert);

        // 1 * 0.5 + 2 * 0.5 = 1.5
        Assert.Equal(1.5f, output[0], 5);
    }

    [Fact]
    public void WetDry_QuarterMix_WeighsDryHeavier()
    {
        var insert = new WetDryInsert(new Doubler(), Frames) { Mix = 0.25f };
        var output = Render(insert);

        // 1 * 0.75 + 2 * 0.25 = 1.25
        Assert.Equal(1.25f, output[0], 5);
    }

    [Fact]
    public void WetDry_ClampsOutOfRangeMix()
    {
        var insert = new WetDryInsert(new Doubler(), Frames);

        insert.Mix = 5f;
        Assert.Equal(1f, insert.Mix);

        insert.Mix = -3f;
        Assert.Equal(0f, insert.Mix);

        insert.Mix = float.NaN;
        Assert.Equal(1f, insert.Mix);   // NaN = 退回全湿（安全默认）
    }

    [Fact]
    public void WetDry_ForwardsNameBypassAndLatency()
    {
        var inner = new Doubler();
        var insert = new WetDryInsert(inner, Frames);

        Assert.Equal("doubler", insert.Name);
        Assert.Equal(inner.IsBypassed, insert.IsBypassed);
        Assert.Equal(inner.LatencySamples, insert.LatencySamples);
        Assert.Same(inner, insert.Inner);
    }

    // ── 增益 ──

    [Fact]
    public void Gain_ZeroDb_IsTransparent()
    {
        // 0 dB = 默认值：连一次乘法都不做。
        var insert = new TrimInsert(new Doubler(), 0);
        var output = Render(insert);

        Assert.Equal(2f, output[0], 5);
    }

    [Fact]
    public void Gain_SixDb_DoublesTheLevel()
    {
        var insert = new TrimInsert(new Doubler(), 6);
        var output = Render(insert);

        // 插件把 1 变成 2，再 +6 dB（×1.9953）→ 3.9905
        Assert.Equal(2 * Math.Pow(10, 6.0 / 20), output[0], 4);
    }

    [Fact]
    public void Gain_MinusSixDb_HalvesTheLevel()
    {
        var insert = new TrimInsert(new Doubler(), -6);
        var output = Render(insert);

        // 1 → 2 → ×0.5012 ≈ 1.0024
        Assert.Equal(1.0024f, output[0], 3);
    }

    [Fact]
    public void Gain_ClampsToPlusMinus24()
    {
        var insert = new TrimInsert(new Doubler());

        insert.GainDb = 100;
        Assert.Equal(24, insert.GainDb);

        insert.GainDb = -100;
        Assert.Equal(-24, insert.GainDb);

        insert.GainDb = double.NaN;
        Assert.Equal(0, insert.GainDb);
    }

    // ── 组合 ──

    [Fact]
    public void BothWrappers_ComposeInTheDocumentedOrder()
    {
        // trim 在湿/干**之后**：它作用在混合结果上。
        //
        // 为什么不是"trim 只作用在湿信号"：trim 的用途是"这个插入让电平变了，
        // 把它调回原位"，而"变了"的量本来就是混合后的结果。反过来做意味着
        // mix = 0 时 trim 完全失效 —— 用户把 mix 拖到 0，电平匹配突然不起作用了。
        var inner = new WetDryInsert(new Doubler(), Frames) { Mix = 0f };
        var insert = new TrimInsert(inner, 6);

        var output = Render(insert);

        // 全干 = 1，trim 在混合之后 → ×1.9953 = 1.9953
        Assert.Equal(1 * Math.Pow(10, 6.0 / 20), output[0], 4);
    }

    [Fact]
    public void BothWrappers_FullWetWithGain()
    {
        var inner = new WetDryInsert(new Doubler(), Frames) { Mix = 1f };
        var insert = new TrimInsert(inner, 6);

        var output = Render(insert);

        // 1 →(插件) 2 →(全湿透传) 2 →(+6 dB ×1.9953) 3.9905
        Assert.Equal(2 * Math.Pow(10, 6.0 / 20), output[0], 4);
    }

    [Fact]
    public void BothWrappers_AtDefaults_MatchTheBarePlugin()
    {
        // 两个包装器都在默认值 → 结果必须与裸插件逐位相同。
        var bare = Render(new Doubler());
        var wrapped = Render(new TrimInsert(new WetDryInsert(new Doubler(), Frames) { Mix = 1f }, 0));

        AssertBitEqual(bare, wrapped);
    }

    static void AssertBitEqual(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);

        for (int i = 0; i < expected.Length; i++)
            Assert.True(expected[i].Equals(actual[i]), $"sample {i}: {expected[i]} != {actual[i]}");
    }
}



