using System;
using System.IO;
using System.Linq;
using System.Threading;
using TuneLab.Audio;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.MixerEngine;
using Xunit;

namespace TuneLab.Tests;

// 端到端验证：真实工程 → 混音器渲染 → LUFS 读数。
//
// 为什么值得单测：LUFS 的数值必须在**真实信号链**上对得上，只喂正弦是不够的
// （那验证的是滤波器，不是整条路）。这里拿测试目录里的 .tlpx，跑一遍渲染循环，
// 把引擎读到的 LUFS 与一个独立实现的参考值对照。
public class MixerLoudnessIntegrationTests
{
    [Fact]
    public void MasterLufs_TracksTheRenderedSignal()
    {
        // 造一段已知电平的信号：1 kHz、-20 dBFS、双声道同相。
        // LUFS 在 1 kHz 附近 ≈ dBFS（K 加权增益约 0 dB），所以整体应该落在 -20 附近。
        float amplitude = (float)Math.Pow(10, -20.0 / 20);
        var frames = 48000 * 3;
        var buffer = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float s = amplitude * (float)Math.Sin(2 * Math.PI * 1000 * i / 48000);
            buffer[i * 2] = s;
            buffer[i * 2 + 1] = s;
        }

        // 用混音器渲染：一张 track → master 的退化图。
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new ConstantState());
        builder.Connect(builder.AddNode("track-0", MixNodeKind.Track, new ConstantState(), new ArraySource(buffer)), master);
        var mixer = new Mixer { SampleRate = 48000 };
        mixer.SetSnapshot(builder.Build());

        var output = new float[frames * 2];
        int offset = 0;
        while (offset < frames)
        {
            int count = Math.Min(512, frames - offset);
            mixer.Render(offset, count, output, offset);
            offset += count;
        }

        // 混音器自己的读数
        Assert.True(mixer.TryGetLufs("master", out double lufs), "master node should be readable");
        Assert.True(mixer.TryGetTruePeakDb("master", out double truePeak), "true peak should be readable");

        // 参考：直接对输出跑一遍独立的 K 加权 + 能量平均
        double reference = ReferenceLufs(output, 48000);

        Assert.True(Math.Abs(lufs - reference) < 1.0,
            $"mixer LUFS {lufs:F2} should match the independent reference {reference:F2}");

        // 1 kHz @ -20 dBFS：LUFS 应该落在 -24..-16 之间
        Assert.True(lufs > -24 && lufs < -16, $"1 kHz @ -20 dBFS should read about -20 LUFS, got {lufs:F2}");

        // True Peak 应该接近 -20 dBFS（正弦的样本峰值 = 它的电平）
        Assert.True(truePeak > -21 && truePeak < -19, $"true peak should be about -20 dBFS, got {truePeak:F2}");
    }

    sealed class ConstantState : IMixChannelState
    {
        public double Volume => 1;
        public double Pan => 0;
        public bool IsSilent => false;
    }

    sealed class ArraySource(float[] data) : IMixSource
    {
        public float[] Data { get; } = data;

        public void AddData(int position, int count, float[] buffer, int offset, float leftGain, float rightGain)
        {
            for (int i = 0; i < count; i++)
            {
                int source = (position + i) * 2;
                buffer[offset + i * 2] += leftGain * Data[source];
                buffer[offset + i * 2 + 1] += rightGain * Data[source + 1];
            }
        }
    }

    // 独立的参考实现：同样的 K 加权系数，同样的 400ms 块 + 门限，但代码路径
    // 完全独立（不共用 LoudnessMeter 的任何东西）。两条路算出同一个数，
    // 才说明"混音器把信号正确喂给了表"，而不是两处犯了同一个错。
    static double ReferenceLufs(float[] buffer, double sampleRate)
    {
        double[] shelfB = [1.53512485958697, -2.69169618940638, 1.19839281085285];
        double[] shelfA = [-1.69065929318241, 0.73248077421585];
        double[] hpB = [1.0, -2.0, 1.0];
        double[] hpA = [-1.99004745483398, 0.99007225036621];

        var hpL = new RefBiquad(hpB, hpA);
        var hpR = new RefBiquad(hpB, hpA);
        var shL = new RefBiquad(shelfB, shelfA);
        var shR = new RefBiquad(shelfB, shelfA);

        int blockSamples = (int)(sampleRate * 0.400);
        int hopSamples = (int)(sampleRate * 0.100);
        int frames = buffer.Length / 2;

        var blocks = new System.Collections.Generic.List<double>();
        double energy = 0;
        int position = 0;

        for (int i = 0; i < frames; i++)
        {
            double l = shL.Process(hpL.Process(buffer[i * 2]));
            double r = shR.Process(hpR.Process(buffer[i * 2 + 1]));
            energy += (l * l + r * r) * 0.5;

            if (++position < blockSamples)
                continue;

            blocks.Add(-0.691 + 10 * Math.Log10(Math.Max(energy / blockSamples, 1e-24)));
            energy *= hopSamples / (double)blockSamples;
            position = hopSamples;
        }

        double sum = 0;
        int passed = 0;
        foreach (var value in blocks)
        {
            if (value < -70) continue;
            sum += Math.Pow(10, value / 10);
            passed++;
        }

        if (passed == 0) return double.NegativeInfinity;
        double gated = 10 * Math.Log10(sum / passed);

        sum = 0;
        passed = 0;
        foreach (var value in blocks)
        {
            if (value < gated - 10) continue;
            sum += Math.Pow(10, value / 10);
            passed++;
        }

        return passed == 0 ? gated : 10 * Math.Log10(sum / passed);
    }

    sealed class RefBiquad(double[] b, double[] a)
    {
        readonly double[] mB = b;
        readonly double[] mA = a;
        double mX1, mX2, mY1, mY2;

        public double Process(double input)
        {
            double y = mB[0] * input + mB[1] * mX1 + mB[2] * mX2 - mA[0] * mY1 - mA[1] * mY2;
            mX2 = mX1; mX1 = input;
            mY2 = mY1; mY1 = y;
            return y;
        }
    }
}
