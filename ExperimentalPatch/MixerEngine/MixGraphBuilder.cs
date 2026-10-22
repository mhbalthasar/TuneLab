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

        // 插入链（就地、按顺序）。建图时把延迟报上来做 PDC。
        public IReadOnlyList<IMixInsert> Inserts { get; }

        internal int Index;
        internal readonly List<EdgeDraft> Edges = [];

        internal NodeDraft(string id, MixNodeKind kind, IMixChannelState? state, IMixSource? source,
            IReadOnlyList<IMixInsert> inserts)
        {
            Id = id;
            Kind = kind;
            State = state;
            Source = source;
            Inserts = inserts;
        }

        // 本节点的插入链延迟之和（音频线程每块现读一次太贵，建图时算一次就够：
        // 换插件 / 改 lookahead 会重建图，见 §3.7 与 §5.7）。
        internal int LatencySamples
        {
            get
            {
                int total = 0;
                foreach (var insert in Inserts)
                    total += insert.LatencySamples;

                return total;
            }
        }
    }

    internal readonly record struct EdgeDraft(NodeDraft Target, float Gain, int DelaySamples, IMixEdgeGain? LiveGain, bool IsPreFader);

    readonly List<NodeDraft> mNodes = [];

    public NodeDraft AddNode(string id, MixNodeKind kind, IMixChannelState? state = null,
        IMixSource? source = null, IReadOnlyList<IMixInsert>? inserts = null)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("node id must not be empty", nameof(id));

        if (mNodes.Any(n => n.Id == id))
            throw new ArgumentException($"duplicate node id '{id}'", nameof(id));

        var draft = new NodeDraft(id, kind, state, source, inserts ?? []);
        draft.Index = mNodes.Count;
        mNodes.Add(draft);
        return draft;
    }

    // gain 是线性增益；delaySamples 是这条边要补的延迟（PDC，M6 起才会非零）。
    // liveGain 非 null 时以它为准（send 的推子），从而拖推子不必重建图。
    // isPreFader = true 表示这条边在推子之前取信号（pre-fader send，见 MixEdge.IsPreFader）。
    public void Connect(NodeDraft from, NodeDraft to, float gain = 1f, int delaySamples = 0,
        IMixEdgeGain? liveGain = null, bool isPreFader = false)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (!mNodes.Contains(from) || !mNodes.Contains(to))
            throw new ArgumentException("both nodes must belong to this builder");

        if (delaySamples < 0)
            throw new ArgumentOutOfRangeException(nameof(delaySamples));

        from.Edges.Add(new EdgeDraft(to, gain, delaySamples, liveGain, isPreFader));
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

            // 设备 sink 没人路由过去是合法的（就是静音）；其余节点孤立 = 建图方的 bug。
            if (node.Edges.Count == 0 && node.Kind != MixNodeKind.Device)
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

        // ── 延迟补偿（PDC，DESIGN.md §3.5）──
        //
        // 语义先说清，不然后面的公式没有意义：
        //   一条路径经过延迟插件 ⇒ 它的信号**更晚**到达汇合点。
        //   要把所有路径对齐到"最晚的那条"，**快的那些路径要补延迟**，慢的不动。
        //
        // 记两个数（都按拓扑序下标）：
        //   own[i]  = 节点 i 自己的插入链延迟之和
        //   down[i] = 从节点 i 的**入口**走到某个 sink 的最长延迟
        //             （= max(入边延迟 + down[下游]) + own[i]）
        //
        // 一条边 i→j 该补多少：让整条 [i → j → sink] 的总长等于 maxLatency。
        //   总长 = own[i] + delay(边) + down[j]
        //   ⇒ delay(边) = maxLatency − own[i] − down[j]
        //
        // 两条纪律附在这公式上：
        //   · 边自己带的 DelaySamples 是调用方**显式**补的，算进 down[j] 就已经生效，
        //     公式里不再出现（否则等于补两次）。
        //   · down[j] 已经含 own[j]，所以公式里没有 own[j] —— 想让 j 的插入再拖后一点，
        //     那是 j 自己的事，由 j 的出边去补。
        var own = new int[order.Count];
        for (int i = 0; i < order.Count; i++)
            own[i] = order[i].LatencySamples;

        var down = new int[order.Count];   // 从本节点入口到 sink 的最长延迟
        var edges = new List<MixEdge>[order.Count];

        for (int i = 0; i < order.Count; i++)
            edges[i] = [];

        int maxLatency = 0;

        // 逆拓扑序：下游先算完，再算自己。
        for (int i = order.Count - 1; i >= 0; i--)
        {
            var draft = order[i];

            if (draft.Edges.Count == 0)
            {
                down[i] = own[i];
            }
            else
            {
                int best = int.MinValue;
                foreach (var edge in draft.Edges)
                    best = Math.Max(best, edge.DelaySamples + down[edge.Target.Index]);

                down[i] = best + own[i];
            }

            if (down[i] > maxLatency)
                maxLatency = down[i];
        }

        // 建边。A（无插入）与 B（4 样本插入）都到 Master：
        //   down[Master]=0、own[Master]=0、maxLatency=4
        //   B→Master: 4 − 4 − 0 = 0   （自己已经慢 4，不动）
        //   A→Master: 4 − 0 − 0 = 4   （快的往后拉齐）
        var nodes = new List<MixNode>(order.Count);
        for (int i = 0; i < order.Count; i++)
        {
            var draft = order[i];
            var list = edges[i];

            foreach (var edge in draft.Edges)
            {
                int target = topoIndex[edge.Target];
                int delay = maxLatency - own[i] - down[edge.Target.Index];

                list.Add(new MixEdge(target, edge.Gain, Math.Max(0, delay), edge.LiveGain, edge.IsPreFader));
            }

            nodes.Add(new MixNode(draft.Id, draft.Kind, draft.State, draft.Source, draft.Inserts, list));
        }

        return new MixSnapshot(nodes, nodes[topoIndex[master]], maxLatency);
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
