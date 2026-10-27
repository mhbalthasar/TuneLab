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

    // true = 这条边在**推子之前**取信号（pre-fader send）。
    //
    // 为什么需要：send 的"推子前/后"决定它受不受通道推子影响。post-fader（默认）
    // 拉低推子 send 也跟着小 —— 做混响尾音时一拉推子尾音就断了；pre-fader 则保持独立，
    // 推子只影响直达声。两种都要有：侧链总线、耳机混音都依赖 pre-fader。
    //
    // 代价：pre-fader 的取信号点在推子之前，所以宿主不能再把推子折进
    // Source.AddData 的左右增益里（那样 buffer 一诞生就带推子了），必须先按 unity 读、
    // tap 完再上推子。**没有 pre-fader 边时走原来的折叠路径**，与旧版逐位相同。
    public bool IsPreFader { get; }

    internal MixEdge(int targetIndex, float gain, int delaySamples, IMixEdgeGain? liveGain, bool isPreFader)
    {
        TargetIndex = targetIndex;
        Gain = gain;
        DelaySamples = delaySamples;
        LiveGain = liveGain;
        IsPreFader = isPreFader;
    }

    internal float CurrentGain => LiveGain?.Gain ?? Gain;

    // 左右声道各自的增益（LiveGain 支持声向时分开，否则都等于 CurrentGain）。
    internal float LeftGain => LiveGain?.LeftGain ?? Gain;

    internal float RightGain => LiveGain?.RightGain ?? Gain;

    // 这条边这一块是否完全不出声（send 被静音）。
    internal bool IsEdgeSilent => LiveGain is { IsSilent: true };
}

// 一条侧链输出（源侧视图）：把本节点推子后、插入后的信号写进哪块缓冲、补多少延迟。
//
// 【为什么延迟在源侧】延迟是"这条边"的属性，但写信号的是源节点、读信号的是目标节点。
// 建图时按 SIDECHAIN.md §4.5 的公式算好，音频线程只按它补延迟线。
public readonly record struct MixSidechainOutput(int BufferIndex, int DelaySamples);

public sealed class MixNode
{
    public string Id { get; }

    public MixNodeKind Kind { get; }

    // 只有 Device 没有（它没有推子）。
    public IMixChannelState? State { get; }

    // 只有 Track 有。
    public IMixSource? Source { get; }

    // 插入链（按顺序处理，就地）。推子之后、分发之前（DESIGN.md §3.2）。
    // 没有插入时是空数组——退化图因此与无插入的路径逐位相同。
    public IReadOnlyList<IMixInsert> Inserts { get; }

    public IReadOnlyList<MixEdge> Outputs { get; }

    // [EP-MIXER-27] 侧链：本节点要写出的键（源侧视图）。
    public IReadOnlyList<MixSidechainOutput> SidechainOutputs { get; }

    // [EP-MIXER-27] 每个插入读哪块键缓冲；-1 = 这个插入没有侧链。
    // 长度 = Inserts.Count（没有侧链时是空数组）。
    //
    // 为什么是"按插入下标"的数组而不是一张表：音频线程每块要按插入顺序查一次，
    // 数组下标是 O(1)，字典查询会在这条最热的循环里多一次哈希。
    public IReadOnlyList<int> SidechainBufferForInsert { get; }

    // 这条节点上有没有 pre-fader 的边（= 有 send 设在推子之前）。
    // 建图时算一次，音频线程只读 —— 它决定 Source.AddData 用 unity 还是直接用推子折叠。
    public bool HasPreFaderOutput { get; }

    internal MixNode(string id, MixNodeKind kind, IMixChannelState? state, IMixSource? source,
        IReadOnlyList<IMixInsert> inserts, IReadOnlyList<MixEdge> outputs,
        IReadOnlyList<MixSidechainOutput>? sidechainOutputs = null,
        IReadOnlyList<int>? sidechainBufferForInsert = null)
    {
        Id = id;
        Kind = kind;
        State = state;
        Source = source;
        Inserts = inserts;
        Outputs = outputs;
        SidechainOutputs = sidechainOutputs ?? [];
        SidechainBufferForInsert = sidechainBufferForInsert ?? [];

        for (int i = 0; i < outputs.Count; i++)
        {
            if (outputs[i].IsPreFader)
            {
                HasPreFaderOutput = true;
                break;
            }
        }
    }
}
