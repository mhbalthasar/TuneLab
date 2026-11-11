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

    // [EP-MIXER-AUTO] 插入参数自动化绑定（全图拍平，跨节点聚合）。
    // 空表 = 图里没有任何参数自动化：音频线程每块开头判一次空就完事。
    public IReadOnlyList<MixAutomationBinding> Automations { get; }

    // 每个节点属于哪条设备流（下标与 Nodes 对齐；非 sink = null）。
    // "device:default" = 主设备，其余见 MixDeviceIds。
    public IReadOnlyList<string?> SinkDeviceIds { get; }

    internal MixSnapshot(IReadOnlyList<MixNode> nodes, MixNode master, int maxLatencySamples,
        int sidechainBufferCount = 0, IReadOnlyList<MixAutomationBinding>? automations = null,
        IReadOnlyList<string?>? sinkDeviceIds = null)
    {
        Nodes = nodes;
        Master = master;
        MaxLatencySamples = maxLatencySamples;
        SidechainBufferCount = sidechainBufferCount;
        Automations = automations ?? [];
        SinkDeviceIds = sinkDeviceIds is { Count: > 0 } && sinkDeviceIds.Count == nodes.Count
            ? sinkDeviceIds
            : BuildSinkDeviceIds(nodes);
    }

    // 手工构造的快照（测试）没有传设备流：sink 全部回落到主设备，与 v1 行为一致。
    static string?[] BuildSinkDeviceIds(IReadOnlyList<MixNode> nodes)
    {
        var ids = new string?[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Kind is MixNodeKind.Master or MixNodeKind.Device)
                ids[i] = nodes[i].DeviceId ?? MixDeviceIds.Default;
        }

        return ids;
    }
}
