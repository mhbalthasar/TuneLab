namespace TuneLab.ExperimentalPatch.MixerEngine;

// 一张不可变的混音图：节点按**拓扑序**排好，边里带着目标下标。
//
// 不可变的含义是"结构不变"：谁连到谁、增益多少，在建图时定死。
// 推子 / 声像 / 静音是活值，走 IMixChannelState，每块现读（见 IMixChannelState 的注释）。
public sealed class MixSnapshot
{
    // 拓扑序：任何节点都排在它的所有下游之前。
    public IReadOnlyList<MixNode> Nodes { get; }

    public MixNode Master { get; }

    // 到 Master 的最长路径延迟（样本）。M6 起用于 PDC；无插入时恒为 0。
    public int MaxLatencySamples { get; }

    // [EP-MIXER-27] 侧链缓冲的数量（每条侧链边一块）。0 = 图里没有任何侧链。
    // Runtime 按它分配缓冲；退化图因此一块都不分配。
    public int SidechainBufferCount { get; }

    internal MixSnapshot(IReadOnlyList<MixNode> nodes, MixNode master, int maxLatencySamples,
        int sidechainBufferCount = 0)
    {
        Nodes = nodes;
        Master = master;
        MaxLatencySamples = maxLatencySamples;
        SidechainBufferCount = sidechainBufferCount;
    }
}
