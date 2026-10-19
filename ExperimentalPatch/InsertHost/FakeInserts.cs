using System;
using System.Collections.Generic;
using TuneLab.ExperimentalPatch.MixerEngine;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 测试 / 冒烟用的假后端：不碰任何原生库，纯托管地"做点动静"。
//
// 【为什么它在这里而不是在测试工程里】宿主侧要验证"插入被接进图里、绕过链、
// PDC 用上了 latency"这些行为；给宿主测试喂一个真 VST3 是不可能的（测试机未必有插件）。
// 所以假后端必须是**产品代码的一部分**——它同时是单测的工具、也是"没有 native 也能跑通链路"的证明。
public sealed class GainInsert : IMixInsert
{
    // 乘上去的线性增益。1 = 直通。故意用 double 触发浮点路径，好验 bit 相同那条纪律。
    public double Gain { get; set; } = 1.0;

    public bool Bypassed { get; set; }

    public string Name { get; set; } = "Gain";

    public bool IsBypassed => Bypassed;

    public int LatencySamples { get; }

    // 记下被处理了多少块 —— 测试断言"旁通时一次都没进来"用。
    public int ProcessedBlocks { get; private set; }

    public int ProcessedSamples { get; private set; }

    public GainInsert(double gain = 1.0, int latencySamples = 0)
    {
        Gain = gain;
        LatencySamples = latencySamples;
    }

    public void Process(Span<float> buffer, int offset, int count)
    {
        if (count <= 0)
            return;

        if (IsBypassed || Gain == 1.0)
            return;

        float g = (float)Gain;
        int samples = count * 2;

        for (int i = 0; i < samples; i++)
            buffer[offset + i] *= g;

        ProcessedBlocks++;
        ProcessedSamples += count;
    }
}

// 一个只会失败的假后端：验证"创建失败 → 宿主把槽位标红旁通"这条降级路径。
public sealed class FailingInsert : IMixInsert
{
    public string Name => "AlwaysFails";

    public bool IsBypassed => true;

    public int LatencySamples => 0;

    public void Process(Span<float> buffer, int offset, int count)
    {
        throw new InvalidOperationException("this backend always fails");
    }
}
