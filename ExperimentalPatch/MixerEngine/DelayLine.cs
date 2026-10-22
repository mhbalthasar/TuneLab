namespace TuneLab.ExperimentalPatch.MixerEngine;

// 整数样本延迟线（交织立体声），只给 PDC 用。
//
// 为什么是整数样本：v1 不做分数延迟——插入报的延迟本来就是整数样本，
// 分数延迟要额外插值，收益在调音台这个尺度上看不出来。
public sealed class DelayLine
{
    readonly float[] mBuffer;   // 帧环形缓冲（每帧 2 float）
    readonly int mDelayFrames;
    int mWrite;

    public DelayLine(int delaySamples)
    {
        if (delaySamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(delaySamples));

        mDelayFrames = delaySamples;
        mBuffer = new float[delaySamples * 2];
    }

    public int DelaySamples => mDelayFrames;

    // dst[..] += (D 帧前的 src) * gain。左右同一个 gain。
    public void Process(float[] src, int srcOffset, float[] dst, int dstOffset, int count, float gain)
        => Process(src, srcOffset, dst, dstOffset, count, gain, gain);

    // dst[..] += (D 帧前的 src) * 左右各自的 gain（send 带自己声向时用）。
    public void Process(float[] src, int srcOffset, float[] dst, int dstOffset, int count,
        float leftGain, float rightGain)
    {
        if (leftGain == rightGain)
        {
            float gain = leftGain;

            for (int i = 0; i < count; i++)
            {
                int read = mWrite * 2;

                // 环形缓冲长度 = D 帧：即将被覆盖的这一格，装的正是 D 帧前写进来的样本。
                dst[dstOffset + i * 2] += mBuffer[read] * gain;
                dst[dstOffset + i * 2 + 1] += mBuffer[read + 1] * gain;

                mBuffer[read] = src[srcOffset + i * 2];
                mBuffer[read + 1] = src[srcOffset + i * 2 + 1];

                mWrite++;
                if (mWrite == mDelayFrames)
                    mWrite = 0;
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            int read = mWrite * 2;

            dst[dstOffset + i * 2] += mBuffer[read] * leftGain;
            dst[dstOffset + i * 2 + 1] += mBuffer[read + 1] * rightGain;

            mBuffer[read] = src[srcOffset + i * 2];
            mBuffer[read + 1] = src[srcOffset + i * 2 + 1];

            mWrite++;
            if (mWrite == mDelayFrames)
                mWrite = 0;
        }
    }

    public void Reset() => Array.Clear(mBuffer, 0, mBuffer.Length);
}
