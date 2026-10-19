using System;

namespace TuneLab.ExperimentalPatch.MixerEngine;

// 输出级峰值限幅（"越界时向回压制"那一步）。
//
// 【放在哪】混音图 sink 之后、**电平表抽头之后**。所以：
//   · 表上仍然看得到越界（它显示的是限幅前的峰值）——正是要的效果；
//   · 送进设备 / 写进 WAV 的样本不超过 ceiling；
//   · 设备与导出走的是同一条路（`Mixer.Render`），所以"听到的"和"文件里的"一致。
//
// 【算法】无前视、逐样本：
//   · 瞬时起振：需要压到 ceiling/|peak| 时立刻压——同一帧内就压住，绝不会漏过一个尖峰；
//   · 线性释放：按 releaseSeconds 把增益放回 1；
//   · 立体声联动：同一帧的 L/R 取同一个增益，避免声像被压歪。
//
// 【为什么不是"整块乘一个增益"】块边界跳变会产生拉链噪声（zipper noise）；逐样本瞬时压 + 慢释放
// 没有这个问题。代价是起振瞬间有谐波失真（无前视限幅器的通病），但这比"样本回绕导致符号翻转"
// 好得多——后者就是用户听到的"刺啦刺啦"。
//
// 【为什么不做成用户可调】先做对，再做好：v1 固定 ceiling = 0.999、释放 150 ms。
// 将来若要暴露给界面，参数已经是构造参数，加一处宿主设置即可。
public sealed class PeakLimiter
{
    public const float DefaultCeiling = 0.999f;
    public const double DefaultReleaseSeconds = 0.15;

    readonly float mCeiling;
    readonly double mReleaseSeconds;

    int mSampleRate = 44100;
    float mGain = 1f;
    float mReleasePerSample;

    public PeakLimiter(float ceiling = DefaultCeiling, double releaseSeconds = DefaultReleaseSeconds)
    {
        if (ceiling <= 0 || ceiling > 1)
            throw new ArgumentOutOfRangeException(nameof(ceiling), "ceiling must be in (0, 1]");

        if (releaseSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(releaseSeconds));

        mCeiling = ceiling;
        mReleaseSeconds = releaseSeconds;
        UpdateRelease();
    }

    public float Ceiling => mCeiling;

    // 当前增益（1 = 没在压）。测试与调试用。
    public float Gain => mGain;

    public int SampleRate
    {
        get => mSampleRate;
        set
        {
            if (value <= 0 || value == mSampleRate)
                return;

            mSampleRate = value;
            UpdateRelease();
        }
    }

    public void Reset() => mGain = 1f;

    // 就地处理。channels = 2（立体声）或 1（单声道导出）。
    public void Process(float[] buffer, int offset, int frames, int channels = 2)
    {
        for (int i = 0; i < frames; i++)
        {
            int frame = offset + i * channels;

            float peak = 0;
            for (int c = 0; c < channels; c++)
                peak = Math.Max(peak, Math.Abs(buffer[frame + c]));

            if (peak > mCeiling)
            {
                float target = mCeiling / peak;
                if (target < mGain)
                    mGain = target;   // 瞬时起振
            }
            else if (mGain < 1f)
            {
                mGain = Math.Min(1f, mGain + mReleasePerSample);
            }

            for (int c = 0; c < channels; c++)
                buffer[frame + c] *= mGain;
        }
    }

    void UpdateRelease() => mReleasePerSample = (float)(1.0 / Math.Max(1.0, mReleaseSeconds * mSampleRate));
}
