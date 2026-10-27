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

        // [EP-MIXER-27] 本节点**吃**的侧链：从哪个源节点、喂给第几个插入。
        // 记在目标侧而不是源侧，是因为延迟公式要用目标的 down[] 与 before(D,k)。
        internal readonly List<SidechainDraft> SidechainEdges = [];

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

    // [EP-MIXER-27] 一条侧链：Source 的出口 → 本节点第 InsertIndex 个插入的输入。
    internal readonly record struct SidechainDraft(NodeDraft Source, int InsertIndex);

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

    // [EP-MIXER-27] 把 from 的出口接到 to 的第 insertIndex 个插入的**侧链输入**。
    //
    // 三件事（SIDECHAIN.md §4.2）：
    //   1. 加进**排序依赖**（indegree），保证源节点在这一块里先渲染；
    //   2. 记进 to.SidechainEdges，**不**记进 to.Edges（不参与混音求和）；
    //   3. 建图时按 §4.5 算延迟。
    //
    // 侧链边**不进** down[] / maxLatency —— 加一条侧链不会改变主混音的延迟补偿，
    // 同一个工程"有侧链 / 没侧链"的可听结果只差插件本身的作用。
    public void ConnectSidechain(NodeDraft from, NodeDraft to, int insertIndex)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (!mNodes.Contains(from) || !mNodes.Contains(to))
            throw new ArgumentException("both nodes must belong to this builder");

        if (ReferenceEquals(from, to))
            throw new ArgumentException("a node cannot feed its own sidechain", nameof(from));

        if (insertIndex < 0 || insertIndex >= to.Inserts.Count)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));

        to.SidechainEdges.Add(new SidechainDraft(from, insertIndex));
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

        // [EP-MIXER-27] 侧链边也是排序依赖：源节点必须先渲染，目标才能读到这一块的键。
        // 反向表按源节点索引（侧链记在目标侧，Kahn 出队时要知道"谁在等我"）。
        var sidechainDependents = new List<NodeDraft>[mNodes.Count];
        for (int i = 0; i < mNodes.Count; i++)
            sidechainDependents[i] = [];

        foreach (var node in mNodes)
        {
            foreach (var sidechain in node.SidechainEdges)
            {
                indegree[node.Index]++;
                sidechainDependents[sidechain.Source.Index].Add(node);
            }
        }

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

            foreach (var dependent in sidechainDependents[node.Index])
            {
                if (--indegree[dependent.Index] == 0)
                    ready.Enqueue(dependent);
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
        // ⚠️ down[] 的下标是**拓扑序**，不是 `NodeDraft.Index`（创建序）。master 总是先创建、
        // 后出队，两者在常见图里就不同；拿创建序去取 down 会把某条轨的延迟当成 master 的，
        // 快的轨反而不补（`InsertLatency_IsCompensatedWhenTheDelayedTrackIsCompiledFirst`）。
        //
        // 一条边 i→j 该补多少：让整条 [i → j → sink] 的总长等于 maxLatency。
        //   output[i] = 从音源到 i 的**出口**的实际延迟（源 = own[i]，AUX = 入口 + own[i]）
        //   总长 = output[i] + delay(边) + down[j]
        //   ⇒ delay(边) = maxLatency − output[i] − down[j]
        //
        // 为什么不是 own[i]：own[i] 不含上游。轨道源的上游是 0，AUX 的上游是入边延迟之和；
        // 用 own[i] 会把整条 AUX 总线再往后推一次（`AuxBusWithUpstreamLatency...`）。
        //
        // 两条纪律附在这公式上：
        //   · 边自己带的 DelaySamples 是调用方**显式**补的，在入边之前；down[j] 不含它，
        //     所以公式里要减一次。
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
                    best = Math.Max(best, edge.DelaySamples + down[topoIndex[edge.Target]]);

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

        // ── 前向传播：每个节点**实际**的入口 / 出口延迟（按拓扑序）──
        //
        // 为什么不能直接用 own[i]：own[i] 只算"本节点插入链"，不含上游。
        // 轨道源的上游是 0（音源按 SeekOffset 提前读）；AUX 的上游是入边延迟之和。
        // 用 own[i] 去补 AUX 的出边，会把整条 AUX 总线再往后推一次
        // （`AuxBusWithUpstreamLatency_IsAlignedWithTheDirectPath` 钉住这个）。
        //
        // entrance[i] = 到 i 的入口为止的最长延迟（源 = 0）
        // output[i]   = entrance[i] + own[i]
        var entrance = new int[order.Count];
        var output = new int[order.Count];

        for (int i = 0; i < order.Count; i++)
        {
            var draft = order[i];
            output[i] = entrance[i] + own[i];

            foreach (var edge in draft.Edges)
            {
                int target = topoIndex[edge.Target];

                // 显式 DelaySamples 在入边**之前**，所以 down[target]（从 target 入口到 sink）
                // 不含它；公式里要单独减一次，否则会补两次。
                int delay = Math.Max(0, maxLatency - output[i] - edge.DelaySamples - down[target]);

                edges[i].Add(new MixEdge(target, edge.Gain, delay, edge.LiveGain, edge.IsPreFader));

                int arrival = output[i] + edge.DelaySamples + delay;
                if (arrival > entrance[target])
                    entrance[target] = arrival;
            }
        }

        // [EP-MIXER-27] 侧链边：每条一块键缓冲。键在 S 的**出口**取，送到 D 的第 k 个插入
        // 的**输入点**：
        //   delay = (D 的入口延迟 + D 前 k 个插入的延迟) − S 的出口延迟
        //
        // 与主信号共用 entrance[] / output[]，所以目标在不在最长路径上都对齐；
        // 不再出现"目标不在最长路径 → 键被多推 maxLatency − down[D]"的错位。
        //
        // 侧链边**不参与** down[] / maxLatency / entrance[] / output[] 的计算
        // （上面两段只看 draft.Edges）—— 加一条侧链不会改变主混音的延迟补偿。
        var sidechainOutputs = new List<MixSidechainOutput>[order.Count];
        var sidechainByInsert = new int[order.Count][];
        for (int i = 0; i < order.Count; i++)
        {
            sidechainOutputs[i] = [];
            sidechainByInsert[i] = [];
        }

        int sidechainBufferCount = 0;

        for (int i = 0; i < order.Count; i++)
        {
            var draft = order[i];

            if (draft.SidechainEdges.Count == 0)
                continue;

            var map = new int[draft.Inserts.Count];
            Array.Fill(map, -1);

            foreach (var sidechain in draft.SidechainEdges)
            {
                int bufferIndex = sidechainBufferCount++;

                int before = 0;
                for (int k = 0; k < sidechain.InsertIndex; k++)
                    before += draft.Inserts[k].LatencySamples;

                int source = topoIndex[sidechain.Source];
                int delay = Math.Max(0, entrance[i] + before - output[source]);

                sidechainOutputs[source].Add(new MixSidechainOutput(bufferIndex, delay));

                map[sidechain.InsertIndex] = bufferIndex;
            }

            sidechainByInsert[i] = map;
        }

        for (int i = 0; i < order.Count; i++)
        {
            var draft = order[i];
            nodes.Add(new MixNode(draft.Id, draft.Kind, draft.State, draft.Source, draft.Inserts, edges[i],
                sidechainOutputs[i], sidechainByInsert[i]));
        }

        return new MixSnapshot(nodes, nodes[topoIndex[master]], maxLatency, sidechainBufferCount);
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
