namespace TuneLab.ExperimentalPatch.MixerEngine;

public enum MixNodeKind
{
    // 音源：读 IMixSource，按 Volume/Pan 求和（增益在逐段循环里应用，见 IMixSource）。
    Track,

    // 总线：输入缓冲求和 → 推子 → 分发。AUX 与 Master 同构。
    Aux,

    // 终点：输入缓冲 → 推子（= MasterGain）→ 设备。整张图恰好一个。
    Master,

    // 直接进设备（绕过 Master）。v1 只有一个设备，所以它就是一个输出口。
    Device,
}

// 一条边（output 或 send）。增益与延迟在建图时定死，运行时只读。
public sealed class MixEdge
{
    public int TargetIndex { get; }

    // 线性增益（dB → 线性由宿主换算）。LiveGain 非 null 时以它为准（send 的推子）。
    public float Gain { get; }

    // 活增益（可选）。见 IMixEdgeGain 的注释。
    public IMixEdgeGain? LiveGain { get; }

    // 延迟补偿：这条边要补的样本数（M6 接入插入后才会 > 0）。
    public int DelaySamples { get; }

    internal MixEdge(int targetIndex, float gain, int delaySamples, IMixEdgeGain? liveGain)
    {
        TargetIndex = targetIndex;
        Gain = gain;
        DelaySamples = delaySamples;
        LiveGain = liveGain;
    }

    internal float CurrentGain => LiveGain?.Gain ?? Gain;
}

public sealed class MixNode
{
    public string Id { get; }

    public MixNodeKind Kind { get; }

    // 只有 Device 没有（它没有推子）。
    public IMixChannelState? State { get; }

    // 只有 Track 有。
    public IMixSource? Source { get; }

    public IReadOnlyList<MixEdge> Outputs { get; }

    internal MixNode(string id, MixNodeKind kind, IMixChannelState? state, IMixSource? source, IReadOnlyList<MixEdge> outputs)
    {
        Id = id;
        Kind = kind;
        State = state;
        Source = source;
        Outputs = outputs;
    }
}
