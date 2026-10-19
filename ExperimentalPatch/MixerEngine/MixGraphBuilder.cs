using System;
using System.Collections.Generic;
using System.Linq;

namespace TuneLab.ExperimentalPatch.MixerEngine;

// 把"谁连到谁"编译成一张拓扑序的快照。
//
// 三条纪律：
//   · 同一批就绪的节点**保持加入顺序** —— 退化图（全部轨 → Master）因此与旧路径的累加顺序一致，
//     这是 DESIGN.md §7.2「逐样本 bit 相同」成立的前提。
//   · 环路在这里就拒绝（Kahn 排不完 = 有环），不留给运行期。
//   · 节点 id 唯一；Master 恰好一个且没有出口。
public sealed class MixGraphBuilder
{
    public sealed class NodeDraft
    {
        public string Id { get; }
        public MixNodeKind Kind { get; }
        public IMixChannelState? State { get; }
        public IMixSource? Source { get; }

        internal int Index;
        internal readonly List<EdgeDraft> Edges = [];

        internal NodeDraft(string id, MixNodeKind kind, IMixChannelState? state, IMixSource? source)
        {
            Id = id;
            Kind = kind;
            State = state;
            Source = source;
        }
    }

    internal readonly record struct EdgeDraft(NodeDraft Target, float Gain, int DelaySamples);

    readonly List<NodeDraft> mNodes = [];

    public NodeDraft AddNode(string id, MixNodeKind kind, IMixChannelState? state = null, IMixSource? source = null)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("node id must not be empty", nameof(id));

        if (mNodes.Any(n => n.Id == id))
            throw new ArgumentException($"duplicate node id '{id}'", nameof(id));

        var draft = new NodeDraft(id, kind, state, source);
        draft.Index = mNodes.Count;
        mNodes.Add(draft);
        return draft;
    }

    // gain 是线性增益；delaySamples 是这条边要补的延迟（PDC，M6 起才会非零）。
    public void Connect(NodeDraft from, NodeDraft to, float gain = 1f, int delaySamples = 0)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (!mNodes.Contains(from) || !mNodes.Contains(to))
            throw new ArgumentException("both nodes must belong to this builder");

        if (delaySamples < 0)
            throw new ArgumentOutOfRangeException(nameof(delaySamples));

        from.Edges.Add(new EdgeDraft(to, gain, delaySamples));
    }

    public MixSnapshot Build()
    {
        if (mNodes.Count == 0)
            throw new InvalidOperationException("mixer graph has no nodes");

        var masters = mNodes.Where(n => n.Kind == MixNodeKind.Master).ToList();
        if (masters.Count != 1)
            throw new InvalidOperationException($"mixer graph must have exactly one Master node (found {masters.Count})");

        var master = masters[0];

        if (master.Edges.Count > 0)
            throw new InvalidOperationException("Master node must not have outputs");

        foreach (var node in mNodes)
        {
            if (node.Kind == MixNodeKind.Master)
                continue;

            if (node.Edges.Count == 0)
                throw new InvalidOperationException($"node '{node.Id}' has no output");

            if (node.Kind == MixNodeKind.Track && node.Source == null)
                throw new InvalidOperationException($"track node '{node.Id}' has no source");
        }

        // Kahn 拓扑排序（就绪队列按加入顺序出队 → 顺序稳定）。
        var indegree = new int[mNodes.Count];
        foreach (var node in mNodes)
            foreach (var edge in node.Edges)
                indegree[edge.Target.Index]++;

        var order = new List<NodeDraft>(mNodes.Count);
        var ready = new Queue<NodeDraft>();
        for (int i = 0; i < mNodes.Count; i++)
        {
            if (indegree[i] == 0)
                ready.Enqueue(mNodes[i]);
        }

        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            order.Add(node);

            foreach (var edge in node.Edges)
            {
                if (--indegree[edge.Target.Index] == 0)
                    ready.Enqueue(edge.Target);
            }
        }

        if (order.Count != mNodes.Count)
        {
            var stuck = mNodes.Where(n => indegree[n.Index] > 0).Select(n => n.Id);
            throw new InvalidOperationException("mixer graph has a cycle: " + string.Join(", ", stuck));
        }

        var topoIndex = new Dictionary<NodeDraft, int>(order.Count);
        for (int i = 0; i < order.Count; i++)
            topoIndex[order[i]] = i;

        var nodes = new List<MixNode>(order.Count);
        foreach (var draft in order)
        {
            var edges = new List<MixEdge>(draft.Edges.Count);
            foreach (var edge in draft.Edges)
                edges.Add(new MixEdge(topoIndex[edge.Target], edge.Gain, edge.DelaySamples));

            nodes.Add(new MixNode(draft.Id, draft.Kind, draft.State, draft.Source, edges));
        }

        return new MixSnapshot(nodes, nodes[topoIndex[master]], 0);
    }

    // 供 UI 用：在现有图上再加一条 from → to，会不会成环？
    // （调音台改 output / 加 send 之前先问一次，成环就拒绝并提示，别让非法状态进模型。）
    public static bool WouldCreateCycle(IEnumerable<(string From, string To)> edges, string from, string to)
    {
        if (from == to)
            return true;

        // 从 to 出发能不能走回 from？能 → 新边会成环。
        var adjacency = new Dictionary<string, List<string>>();
        foreach (var (a, b) in edges)
        {
            if (!adjacency.TryGetValue(a, out var list))
                adjacency[a] = list = [];

            list.Add(b);
        }

        var stack = new Stack<string>();
        var visited = new HashSet<string>();
        stack.Push(to);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == from)
                return true;

            if (!visited.Add(current))
                continue;

            if (adjacency.TryGetValue(current, out var next))
            {
                foreach (var item in next)
                    stack.Push(item);
            }
        }

        return false;
    }
}
