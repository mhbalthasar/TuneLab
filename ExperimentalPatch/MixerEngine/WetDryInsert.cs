using System;

namespace TuneLab.ExperimentalPatch.MixerEngine;

// 插入的湿/干混合包装：把"处理后的信号"与"原始信号"按比例混合。
//
// 【为什么在引擎层做，而不是在各后端】三个后端（VST2 / VST3 / JSFX）都要有这个能力，
// 而"存一份干信号、按比例混回去"的逻辑与插件格式无关。放在引擎层，后端零改动，
// 而且旁通 / 顺序调整 / 延迟补偿全都自动继承（包装器本身也是个 IMixInsert）。
//
// 【为什么干信号要另存一份】插件是**就地**处理的（Process 直接改 buffer）。
// 想要干信号就必须在调用它之前先把当前 buffer 拷走。这块拷贝是每块一次、
// 大小 = blockSize * 2 个 float，在音频线程上是必须省着用的分配 ——
// 所以缓冲在构造期一次分好，之后只做 Array.Copy。
//
// 【混合公式】out = dry * (1 − mix) + wet * mix。mix = 0 全干、1 全湿。
// mix = 0 时**跳过整个插件调用**（连拷贝都不做）—— 这与"旁通"不同：
// 旁通是插件不跑，mix = 0 是插件跑了但结果被完全丢掉。前者省 CPU，
// 后者保留插件的内部状态（混响尾巴继续衰减），听感上不一样。
public sealed class WetDryInsert : IMixInsert
{
    readonly IMixInsert mInner;
    readonly float[] mDry;

    // 0 = 全干，1 = 全湿。活的，音频线程每块现读。
    //
    // 为什么要活的：宿主把它建成编译期的一环，但用户会在界面上随时拖 mix。
    // 烘进快照意味着拖一下就要重建整张图（重新分配、换指针）——
    // 与 send 推子同一个理由（见 IMixEdgeGain 的注释）。
    volatile float mMix = 1f;

    public WetDryInsert(IMixInsert inner, int maxBlockSize)
    {
        mInner = inner;
        mDry = new float[Math.Max(1, maxBlockSize) * 2];
    }

    public IMixInsert Inner => mInner;

    public string Name => mInner.Name;

    public bool IsBypassed => mInner.IsBypassed;

    public int LatencySamples => mInner.LatencySamples;

    /// <summary>0 = 全干（只听原始信号），1 = 全湿（只听插件输出）。</summary>
    public float Mix
    {
        get => mMix;
        set => mMix = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;
    }

    public void SetBypassed(bool bypassed) => mInner.SetBypassed(bypassed);

    public void Process(Span<float> buffer, int offset, int count)
    {
        float mix = mMix;

        // 全湿 = 包装器完全透明（连拷贝都省了）。这是最常见的默认值。
        if (mix >= 1f)
        {
            mInner.Process(buffer, offset, count);
            return;
        }

        // 全干 = 插件照样跑（保持内部状态），但输出完全用干信号。
        // 注意顺序：先拷干信号 → 跑插件 → 按比例混。不能反过来，
        // 否则拷到的是已经被插件改过的 buffer。
        int samples = count * 2;
        var dry = mDry.AsSpan(0, samples);
        buffer.Slice(offset, samples).CopyTo(dry);

        mInner.Process(buffer, offset, count);

        if (mix <= 0f)
        {
            dry.CopyTo(buffer.Slice(offset, samples));
            return;
        }

        float dryGain = 1f - mix;
        for (int i = 0; i < samples; i++)
            buffer[offset + i] = dry[i] * dryGain + buffer[offset + i] * mix;
    }
}
