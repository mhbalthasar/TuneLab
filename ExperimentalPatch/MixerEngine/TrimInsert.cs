using System;

namespace TuneLab.ExperimentalPatch.MixerEngine;

// 插入的增益微调包装：在插件输出上加一个 dB 增益。
//
// 【为什么需要】换一个 EQ 常常整条通道的电平就变了（不同插件的内部增益不同）。
// 专业流程里每个插入自带一个 trim，用来把插件的电平匹配回原位 ——
// 否则"A/B 对比两个 EQ"听到的差异里混着电平差，人耳会把更响的那个当成更好的。
//
// 【为什么不叫 GainInsert】`InsertHost.FakeInserts.GainInsert` 是测试替身，
// 用的是线性 gain 且不包装任何东西。这里包装一个真插入、用 dB，
// 叫 TrimInsert 才不会和它混。
//
// 【为什么不塞进湿/干包装】两件事的默认值不同：trim 默认 0 dB（= 透明），
// 湿/干默认 1（= 全湿）。合成一个包装意味着每次都要判两个条件，
// 分开则各自都能在默认值上整段跳过。链上多一环的代价是一次接口调用，
// 相对插件本身的处理可以忽略。
public sealed class TrimInsert : IMixInsert
{
    readonly IMixInsert mInner;

    // dB。活的，音频线程每块现读。
    volatile float mGainDb;

    public TrimInsert(IMixInsert inner, double gainDb = 0)
    {
        mInner = inner;
        mGainDb = (float)gainDb;
    }

    public IMixInsert Inner => mInner;

    public string Name => mInner.Name;

    public bool IsBypassed => mInner.IsBypassed;

    public int LatencySamples => mInner.LatencySamples;

    // [EP-MIXER-27] 侧链能力照抄内层。
    public int InputChannels => mInner.InputChannels;

    public int SidechainChannels => mInner.SidechainChannels;

    /// <summary>dB，0 = 不变。范围由界面约束（−24..+24）。</summary>
    public double GainDb
    {
        get => mGainDb;
        set => mGainDb = (float)(double.IsFinite(value) ? Math.Clamp(value, -24, 24) : 0);
    }

    public void SetBypassed(bool bypassed) => mInner.SetBypassed(bypassed);

    public void Process(Span<float> buffer, int offset, int count)
        => Process(buffer, offset, count, default);

    // [EP-MIXER-27] 键原样转给内层（理由见 WetDryInsert 同一处注释）。
    public void Process(Span<float> buffer, int offset, int count, ReadOnlySpan<float> sidechain)
    {
        mInner.Process(buffer, offset, count, sidechain);

        float db = mGainDb;

        // 0 dB = 逐位透明：连一次乘法都不做。这是默认值，
        // 绝大多数插入不会动它 —— 所以这条快速路径必须存在。
        if (db == 0f)
            return;

        float gain = (float)Math.Pow(10, db / 20);

        for (int i = 0; i < count * 2; i++)
            buffer[offset + i] *= gain;
    }
}
