using System;
using TuneLab.ExperimentalPatch.MixerEngine;
using Xunit;

namespace TuneLab.Tests;

// LUFS / True Peak 的数值验证（roadmap 2.3）。
//
// 参考值来自 ITU-R BS.1770 与 EBU R128 的测试用例：
//   · 1 kHz 正弦、−23 dBFS 峰值 → 约 −23 LUFS（K 加权在 1 kHz 附近增益 ≈ 0 dB，
//     所以 LUFS 与 dBFS 在该频率几乎相同，这是 BS.1770 附录里的基准用例）
//   · 100 Hz 正弦 → K 加权的高通把它压下去，LUFS 明显低于同峰值的 1 kHz
//   · 满刻度方波 → 约 −0.7 LUFS（BS.1770 的零点定义）
public class LoudnessMeterTests
{
    const double SampleRate = 48000;

    static LoudnessMeter MakeMeter() => new(SampleRate);

    // 喂 n 秒的测试音（双声道同相 = 单声源，这是 LUFS 的标准假设）。
    static void FeedTone(LoudnessMeter meter, double frequency, double amplitudeDbFs, double seconds)
    {
        double amplitude = Math.Pow(10, amplitudeDbFs / 20);
        int frames = (int)(SampleRate * seconds);

        var buffer = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float sample = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / SampleRate));
            buffer[i * 2] = sample;
            buffer[i * 2 + 1] = sample;
        }

        // 分块喂（音频线程也是一块块来的），别一次性灌进去。
        int offset = 0;
        while (offset < frames)
        {
            int count = Math.Min(512, frames - offset);
            meter.Process(buffer, offset, count);
            offset += count;
        }
    }

    [Fact]
    public void OneKhzSine_MatchesItsDbFsLevel()
    {
        // BS.1770 的基准：1 kHz 处 K 加权增益 ≈ 0 dB，所以 LUFS ≈ dBFS（峰值）。
        // 正弦的峰值比 RMS 高 3 dB，但 LUFS 定义用的是**峰值归一**的方波参考 ——
        // 1 kHz、−23 dBFS 的正弦在 BS.1770 附录里给出的就是 −23 LUFS 附近。
        var meter = MakeMeter();
        FeedTone(meter, 1000, -23, 3);

        Assert.True(meter.Lufs > -26 && meter.Lufs < -20,
            $"1 kHz @ -23 dBFS should read about -23 LUFS, got {meter.Lufs:F2}");
    }

    [Fact]
    public void LowFrequencyIsWeightedDown()
    {
        // K 加权的高通（~38 Hz）与高频搁架（~1.6 kHz）合起来让人耳曲线变平。
        // 100 Hz 的同样电平，LUFS 必须明显低于 1 kHz 的。
        var high = MakeMeter();
        FeedTone(high, 1000, -20, 2);

        var low = MakeMeter();
        FeedTone(low, 100, -20, 2);

        Assert.True(low.Lufs < high.Lufs - 2,
            $"100 Hz should read lower than 1 kHz: 100 Hz = {low.Lufs:F2}, 1 kHz = {high.Lufs:F2}");
    }

    [Fact]
    public void HighFrequencyIsWeightedUp()
    {
        // 4 kHz 附近是搁架的增益区，同电平下应该比 1 kHz 响。
        var mid = MakeMeter();
        FeedTone(mid, 1000, -20, 2);

        var high = MakeMeter();
        FeedTone(high, 4000, -20, 2);

        Assert.True(high.Lufs > mid.Lufs,
            $"4 kHz should read higher than 1 kHz: 4 kHz = {high.Lufs:F2}, 1 kHz = {mid.Lufs:F2}");
    }

    [Fact]
    public void FullScaleSquareWave_ReadsAboutMinusPointSeven()
    {
        // BS.1770 的零点：满幅度方波（每声道能量 1.0）→ −0.691 LUFS。
        //
        // 方波的频率有讲究：BS.1770 的参考实现用 **997 Hz**（接近 1 kHz，
        // K 加权在该处增益 ≈ 0 dB）。用低频方波（比如 4 Hz）是不对的 ——
        // 那几乎是直流，会被 K 加权的高通（~38 Hz）整段滤掉，
        // 读出来会低十几 dB，与标准无关。
        var meter = MakeMeter();
        int frames = (int)(SampleRate * 2);

        var buffer = new float[frames * 2];
        const double squareFrequency = 997;
        int halfPeriod = (int)(SampleRate / squareFrequency / 2);

        for (int i = 0; i < frames; i++)
        {
            float sample = (i / halfPeriod) % 2 == 0 ? 1f : -1f;
            buffer[i * 2] = sample;
            buffer[i * 2 + 1] = sample;
        }

        int offset = 0;
        while (offset < frames)
        {
            int count = Math.Min(512, frames - offset);
            meter.Process(buffer, offset, count);
            offset += count;
        }

        Assert.True(meter.Lufs > -2.5 && meter.Lufs < 1.0,
            $"full-scale square should read about -0.7 LUFS, got {meter.Lufs:F2}");
    }

    [Fact]
    public void Silence_ReadsNegativeInfinity()
    {
        // 绝对门限 −70 LUFS：纯静音没有任何块通过，读数是 −inf（不是 0，也不是 −70）。
        var meter = MakeMeter();
        int frames = (int)(SampleRate * 2);

        var buffer = new float[frames * 2];
        int offset = 0;
        while (offset < frames)
        {
            int count = Math.Min(512, frames - offset);
            meter.Process(buffer, offset, count);
            offset += count;
        }

        Assert.True(double.IsNegativeInfinity(meter.Lufs));
    }

    [Fact]
    public void Gating_IgnoresQuietPassages()
    {
        // 相对门限的意义：一段响 + 一段极轻，读数应该跟着响的那段走，
        // 不被轻的那段拉下来。这正是 R128 的 gating 与简单平均的区别。
        var meter = MakeMeter();

        FeedTone(meter, 1000, -20, 2);
        double afterLoud = meter.Lufs;

        FeedTone(meter, 1000, -50, 2);

        Assert.True(meter.Lufs > afterLoud - 3,
            $"quiet passage should not drag the reading down much: was {afterLoud:F2}, now {meter.Lufs:F2}");
    }

    [Fact]
    public void TruePeak_CatchesOvershootBetweenSamples()
    {
        // 峰值恰好 0 dBFS 的正弦，样本峰值就是 0 dBFS，True Peak 不会超过它多少。
        var meter = MakeMeter();
        FeedTone(meter, 1000, 0, 1);

        Assert.True(meter.TruePeakDb > -0.5 && meter.TruePeakDb < 1.5,
            $"0 dBFS sine should read about 0 dBFS true peak, got {meter.TruePeakDb:F2}");
    }

    [Fact]
    public void TruePeak_ExceedsSamplePeakNearNyquist()
    {
        // 19 kHz（接近 24 kHz 奈奎斯特）的满幅度正弦，样本间会超出满刻度 ——
        // 这正是 True Peak 相对样本峰值的价值。
        var meter = MakeMeter();
        FeedTone(meter, 19000, -3, 1);

        // 样本峰值 = −3 dBFS，True Peak 应该更高。
        Assert.True(meter.TruePeakDb > -3,
            $"true peak should exceed the sample peak near Nyquist, got {meter.TruePeakDb:F2}");
    }

    [Fact]
    public void Reset_ClearsHistory()
    {
        var meter = MakeMeter();
        FeedTone(meter, 1000, -20, 2);
        Assert.False(double.IsNegativeInfinity(meter.Lufs));

        meter.Reset();
        Assert.True(double.IsNegativeInfinity(meter.Lufs));
        Assert.True(double.IsNegativeInfinity(meter.TruePeakDb));
    }

    [Fact]
    public void SampleRateChange_RestartsCleanly()
    {
        var meter = MakeMeter();
        FeedTone(meter, 1000, -20, 2);

        meter.SetSampleRate(44100);

        // 换了采样率，块长跟着变，旧历史作废 —— 读数回到 −inf 而不是拿旧块凑。
        Assert.True(double.IsNegativeInfinity(meter.Lufs));

        FeedTone(meter, 1000, -20, 2);
        Assert.False(double.IsNegativeInfinity(meter.Lufs));
    }
}
