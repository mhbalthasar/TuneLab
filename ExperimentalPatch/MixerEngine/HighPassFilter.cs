using System;

namespace TuneLab.ExperimentalPatch.MixerEngine;

// 二阶（12 dB/oct）高通，RBJ Audio EQ Cookbook 的系数。
//
// 【为什么自己写】输入级的高通必须**零分配**地跑在音频线程上：每个节点一份实例，
// 内部按声道保存状态（x1/x2/y1/y2），系数只在"采样率或截止频率变了"时重算。
//
// 【为什么不是 6/24 dB/oct】先只做一阶斜率里最常用的 12 dB/oct（Q = 1/√2，最大平坦）。
// 6 dB 与 24 dB 以后要的话，加档位即可，不改这里的结构。
internal sealed class HighPassFilter
{
    // 双声道：0 = 左，1 = 右。插值状态用 double，避免长时间运行累积误差。
    readonly double[] mX1 = new double[2];
    readonly double[] mX2 = new double[2];
    readonly double[] mY1 = new double[2];
    readonly double[] mY2 = new double[2];

    double mRate = -1;
    double mCutoff = -1;

    double mB0, mB1, mB2, mA1, mA2;

    /// <summary>就地处理一块交织立体声。rate / cutoff 与上次相同则只跑滤波，不重算系数。</summary>
    public void Process(float[] buffer, int offset, int count, double rate, double cutoff)
    {
        if (count <= 0 || rate <= 0)
            return;

        if (rate != mRate || cutoff != mCutoff)
            Recompute(rate, cutoff);

        for (int channel = 0; channel < 2; channel++)
        {
            double x1 = mX1[channel], x2 = mX2[channel];
            double y1 = mY1[channel], y2 = mY2[channel];

            for (int i = 0; i < count; i++)
            {
                int index = offset + i * 2 + channel;
                double x = buffer[index];

                double y = mB0 * x + mB1 * x1 + mB2 * x2 - mA1 * y1 - mA2 * y2;

                x2 = x1;
                x1 = x;
                y2 = y1;
                y1 = y;

                buffer[index] = (float)y;
            }

            mX1[channel] = x1;
            mX2[channel] = x2;
            mY1[channel] = y1;
            mY2[channel] = y2;
        }
    }

    // 截止频率被改到 20 Hz 以下（= 关闭）时，把状态清掉：再打开时不该带着旧尾巴。
    public void Reset()
    {
        Array.Clear(mX1);
        Array.Clear(mX2);
        Array.Clear(mY1);
        Array.Clear(mY2);
        mCutoff = -1;
    }

    void Recompute(double rate, double cutoff)
    {
        mRate = rate;
        mCutoff = cutoff;

        // 截止频率夹到奈奎斯特之下：接近 fs/2 时系数会退化。
        double f = Math.Clamp(cutoff, 1.0, rate * 0.49);
        double w0 = 2 * Math.PI * f / rate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5));   // Q = 1/√2

        double a0 = 1 + alpha;
        mB0 = (1 + cos) / 2 / a0;
        mB1 = -(1 + cos) / a0;
        mB2 = (1 + cos) / 2 / a0;
        mA1 = -2 * cos / a0;
        mA2 = (1 - alpha) / a0;
    }
}
