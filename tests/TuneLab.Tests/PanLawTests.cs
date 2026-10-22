using System;
using TuneLab.ExperimentalPatch.MixerEngine;
using Xunit;

namespace TuneLab.Tests;

// 声向法则的数值验证（roadmap 2.5）。
//
// 核心性质：等功率法则在 pan 扫过全程时**总功率不变**（这正是"等功率"的含义），
// 而线性法则在中央有一个 3 dB 的凹陷。
public class PanLawTests
{
    static double Power(PanLaw law, double pan)
    {
        var (left, right) = PanLawMath.Resolve(1, pan, law);
        return left * left + right * right;
    }

    [Fact]
    public void Linear_MatchesTheLegacyHostFormulaIncludingItsQuirk()    {
        // 线性法则 = 上游宿主的老公式：left = v(1-p)、right = v(1+p)。
        //
        // 这个公式有个**已知的怪癖**：硬偏时那一侧是 2×（+6 dB），
        // 中央时两侧各 1×。也就是说 pan 扫过去音量会变（中央比硬偏响 3 dB）。
        // 专业 DAW 不会这样 —— 但 TuneLab 上游一直是这样，
        // 而 DESIGN §7.2 要求混音器与旧路径**逐位相同**。
        //
        // 所以线性法则必须原样保留，等功率法则是给用户的一个**选项**：
        // 想要"pan 移动时音量不变"的人自己切过去。这正是本功能存在的理由。
        Assert.Equal(0, PanLawMath.Resolve(1, 1, PanLaw.Linear0Db).Left, 12);
        Assert.Equal(2, PanLawMath.Resolve(1, 1, PanLaw.Linear0Db).Right, 12);
        Assert.Equal(1, PanLawMath.Resolve(1, 0, PanLaw.Linear0Db).Left, 12);
        Assert.Equal(1, PanLawMath.Resolve(1, 0, PanLaw.Linear0Db).Right, 12);

        // 总功率：硬偏 4、中央 2 —— 中央比硬偏**轻** 3 dB（这才是"凹陷"的正确方向）。
        Assert.Equal(4.0, Power(PanLaw.Linear0Db, 1), 6);
        Assert.Equal(2.0, Power(PanLaw.Linear0Db, 0), 6);
    }

    [Fact]
    public void ConstantPower_KeepsPowerAcrossTheSweep()
    {
        // 等功率：pan 从 −1 扫到 +1，总功率恒定（= 中央的目标功率）。
        double center = Power(PanLaw.ConstantPower3Db, 0);

        foreach (double pan in new[] { -1.0, -0.75, -0.5, -0.25, 0.25, 0.5, 0.75, 1.0 })
            Assert.Equal(center, Power(PanLaw.ConstantPower3Db, pan), 6);
    }

    [Fact]
    public void ConstantPower_HitsTheDocumentedCenterLevel()
    {
        // −3 dB 法则：中央每声道 = −3.01 dB（0.7071），这是"等功率 3 dB"的定义。
        var (left, right) = PanLawMath.Resolve(1, 0, PanLaw.ConstantPower3Db);

        Assert.Equal(0.7071, left, 3);
        Assert.Equal(left, right, 6);
    }

    [Fact]
    public void ConstantPower4_5_IsLowerThan3()
    {
        var (left3, _) = PanLawMath.Resolve(1, 0, PanLaw.ConstantPower3Db);
        var (left45, _) = PanLawMath.Resolve(1, 0, PanLaw.ConstantPower4_5Db);

        // 4.5 dB 法则中央更轻（补偿更激进）。
        Assert.True(left45 < left3, $"4.5 dB center {left45} should be below 3 dB center {left3}");
    }

    [Fact]
    public void Extremes_HardPanIsUnity()
    {
        // pan 到边：一侧 0、一侧 1（等功率）或 2（线性，上游公式的怪癖）。
        // 三种法则在"哪一侧有声"上一致。
        var (left, right) = PanLawMath.Resolve(1, 1, PanLaw.ConstantPower3Db);
        Assert.Equal(0, left, 5);
        Assert.Equal(1, right, 5);

        (left, right) = PanLawMath.Resolve(1, -1, PanLaw.ConstantPower3Db);
        Assert.Equal(1, left, 5);
        Assert.Equal(0, right, 5);

        // 线性法则照旧（2×），这是与上游逐位相同的代价。
        (left, right) = PanLawMath.Resolve(1, 1, PanLaw.Linear0Db);
        Assert.Equal(0, left, 5);
        Assert.Equal(2, right, 5);
    }

    [Fact]
    public void Volume_ScalesBothChannels()
    {
        var (left, right) = PanLawMath.Resolve(0.5, 0.25, PanLaw.ConstantPower3Db);
        var (leftRef, rightRef) = PanLawMath.Resolve(1, 0.25, PanLaw.ConstantPower3Db);

        Assert.Equal(leftRef * 0.5, left, 6);
        Assert.Equal(rightRef * 0.5, right, 6);
    }

    [Fact]
    public void Linear_MatchesTheLegacyFormulaExactly()
    {
        // 默认法则必须与旧公式逐位相同（DESIGN §7.2 的硬验收）。
        foreach (double volume in new[] { 1.0, 0.7, 0.25, 1.6 })
        foreach (double pan in new[] { 0.0, 0.15, -0.3, 1.0 })
        {
            var (left, right) = PanLawMath.Resolve(volume, pan, PanLaw.Linear0Db);

            Assert.Equal(volume * (1 - pan), left, 12);
            Assert.Equal(volume * (1 + pan), right, 12);
        }
    }

    // ── 经过混音器的端到端 ──
    //
    // 上面几条验的是纯函数；这两条验"混音器真的把它用上了"。

    static float[] RenderWithLaw(PanLaw law, double pan)
    {
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new MeterState());
        builder.Connect(builder.AddNode("track-0", MixNodeKind.Track,
            new MeterState { Volume = 1, Pan = pan }, new ConstantMeterSource(0.5f, 0.5f)), master);

        var mixer = new Mixer { SampleRate = 48000, PanLaw = law };
        mixer.SetSnapshot(builder.Build());

        var output = new float[64 * 2];
        mixer.Render(0, 64, output, 0);
        return output;
    }

    sealed class MeterState : IMixChannelState
    {
        public double Volume { get; set; } = 1;
        public double Pan { get; set; }
        public bool IsSilent => false;
    }

    sealed class ConstantMeterSource(float left, float right) : IMixSource
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

    [Fact]
    public void Mixer_DefaultLaw_MatchesTheLegacyRender()
    {
        // 不显式设 PanLaw（= 默认 Linear0Db）→ 与上游老公式一致。
        var output = RenderWithLaw(PanLaw.Linear0Db, 0.3);

        // left = 0.5 × (1 − 0.3) = 0.35，right = 0.5 × (1 + 0.3) = 0.65
        Assert.Equal(0.35f, output[0], 5);
        Assert.Equal(0.65f, output[1], 5);
    }

    [Fact]
    public void Mixer_ConstantPower_ChangesTheBalance()
    {
        // 同一个 pan，等功率法则的左右分配必须不同（而且中央两侧相等）。
        var linear = RenderWithLaw(PanLaw.Linear0Db, 0);
        var power = RenderWithLaw(PanLaw.ConstantPower3Db, 0);

        // 线性中央：左右都 0.5。等功率中央：左右都 0.5 × 0.7071 = 0.3536。
        Assert.Equal(0.5f, linear[0], 5);
        Assert.Equal(0.5f, linear[1], 5);

        Assert.Equal(0.5 * Math.Sqrt(0.5), power[0], 5);
        Assert.Equal(power[0], power[1], 5);
    }
}
