using System;
using System.Collections.Generic;

namespace TuneLab.ExperimentalPatch.MixerEngine;

// 混音图的渲染器：持有一张快照 + 它需要的缓冲，按块把音频累加进输出。
//
// 【实时安全】Render 里不分配、不加锁、不碰 UI。缓冲在 SetSnapshot 时按最大块长分配；
// 一次请求比最大块长长就切块（切块不改变逐样本算术，故不影响 bit 相同）。
//
// 【换图】SetSnapshot 换的是一个 Runtime 对象（快照 + 缓冲 + 延迟线），音频线程只读一次引用。
// 旧 Runtime 由 GC 回收——没有"释放了但音频线程还在用"的窗口。
public sealed class Mixer
{
    public const int DefaultMaxBlockSize = 4096;

    readonly int mMaxBlockSize;
    volatile Runtime? mRuntime;

    public Mixer(int maxBlockSize = DefaultMaxBlockSize)
    {
        if (maxBlockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBlockSize));

        mMaxBlockSize = maxBlockSize;
    }

    public bool HasSnapshot => mRuntime != null;

    public MixSnapshot? Snapshot => mRuntime?.Snapshot;

    // 输出级限幅（DESIGN.md §3.10）。默认**关**：引擎单测要验证"纯混音"的 bit 相同，
    // 限幅是另一层语义。宿主在 AudioGraph 里把它打开。
    public PeakLimiter Limiter { get; } = new();

    public bool LimiterEnabled { get; set; }

    // 读取位置的补偿量（样本）：图里所有链的最长延迟 MaxLatencySamples。
    // 挂在 Runtime 上而不是 MixSnapshot 上：它随快照一起换，但**读**它的频率是每块一次，
    // 放在 Runtime 里音频线程少一次间接跳转。
    public int SeekOffset => mRuntime?.SeekOffset ?? 0;

    public void SetSnapshot(MixSnapshot? snapshot)
    {
        mRuntime = snapshot == null ? null : new Runtime(snapshot, mMaxBlockSize);
    }

    // 把 [position, position + count) 渲染进 buffer（交织立体声，写起点 offset）。
    // masterInput 是"直接混进 Master 输入"的额外音源（宿主的预览音），在 Master 增益之前加入。
    public void Render(int position, int count, float[] buffer, int offset, IMixSource? masterInput = null)
    {
        var runtime = mRuntime;
        if (runtime == null || count <= 0)
            return;

        while (count > 0)
        {
            int n = Math.Min(count, mMaxBlockSize);
            RenderChunk(runtime, position, n, buffer, offset, masterInput);

            // 限幅在**整块渲染完之后**做：各节点的电平抽头（表）已经在这一块里取过了，
            // 所以表显示的是限幅前的峰值（DESIGN.md §3.10）。
            if (LimiterEnabled)
                Limiter.Process(buffer, offset, n);

            position += n;
            offset += n * 2;
            count -= n;
        }
    }

    // 取某个节点的峰值电平（dB）。调音台的表用它（Track / AUX / Master 同一口径）。
    // 停止播放时引擎不渲染，这里读到的会是最后一帧 —— 调用方（宿主）负责判定"现在没在播"。
    public bool TryGetPeakDb(string nodeId, out double leftDb, out double rightDb)
    {
        leftDb = double.NegativeInfinity;
        rightDb = double.NegativeInfinity;

        var runtime = mRuntime;
        if (runtime == null || !runtime.Index.TryGetValue(nodeId, out int index))
            return false;

        leftDb = ToDb(runtime.PeakLeft[index]);
        rightDb = ToDb(runtime.PeakRight[index]);
        return true;
    }

    static double ToDb(float amplitude) => amplitude <= 0 ? double.NegativeInfinity : 20 * Math.Log10(amplitude);

    void RenderChunk(Runtime runtime, int position, int count, float[] output, int outputOffset, IMixSource? masterInput)
    {
        var nodes = runtime.Snapshot.Nodes;

        // 1) 清空所有节点的缓冲（含 sink：sink 也用自己的缓冲，最后统一混进输出）。
        for (int i = 0; i < nodes.Count; i++)
            Array.Clear(runtime.Buffers[i], 0, count * 2);

        // 2) 按拓扑序逐节点：出声 → 推子 → 分发。
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var target = runtime.Buffers[i];
            const int targetOffset = 0;

            bool hasSignal = true;

            if (node.Kind == MixNodeKind.Track)
            {
                var state = node.State;
                if (state == null || state.IsSilent || node.Source == null)
                {
                    // 静音 / 未 solo：连音源都不读（与旧路径"跳过这条轨"一致）。
                    hasSignal = false;
                }
                else
                {
                    // 与宿主旧路径同一公式、同一顺序：lv = v(1−p)、rv = v(1+p)。
                    float left = (float)(state.Volume * (1 - state.Pan));
                    float right = (float)(state.Volume * (1 + state.Pan));

                    // 提前播放的补偿（DESIGN.md §3.11）：延迟插件使 out[t] = source[t−X]，
                    // 于是播放头走到 t 时听到的是 t−X 的内容。读 t+X 就把它拉回 t。
                    // 偏移为 0（没有任何插件延迟）时与旧路径逐位相同 —— 退化的 bit 相同正是靠这个。
                    node.Source.AddData(position + runtime.SeekOffset, count, target, targetOffset, left, right);
                }
            }
            else if (node.Kind == MixNodeKind.Master)
            {
                // 预览音在 Master 增益之前混进来 —— 与旧路径（MixData → AddData(预览) → MasterGain）同序。
                masterInput?.AddData(position, count, target, targetOffset, 1f, 1f);
                ApplyGain(node.State, target, targetOffset, count);
            }
            else if (node.Kind == MixNodeKind.Device)
            {
                // 直接进设备：**不过 Master 推子**（DESIGN.md §4.4）。这里什么都不做。
            }
            else
            {
                var state = node.State;
                if (state == null || state.IsSilent)
                    hasSignal = false;
                else
                    ApplyGain(state, target, targetOffset, count);
            }

            // 电平抽头：Track / AUX / Master 同一口径 —— 推子与声像**之后**、插入链之前。
            // Track 也要测：通道条的表必须能反映声像（左右两根柱子各走各的）。
            if (node.Kind is MixNodeKind.Track or MixNodeKind.Aux or MixNodeKind.Master)
                MeasurePeak(target, targetOffset, count, runtime, i);

            if (!hasSignal)
                continue;

            // 插入链：在推子之后、分发之前就地处理（DESIGN.md §3.2）。
            // 跳过旁通的插入：**旁通是活值**（写入后下一块就生效），引擎这里每块现读一次
            // IsBypassed，所以按状态直接 continue。实现方仍然应该自己判直通路径 ——
            // 这里只保证"旁通的插入一次都不会被调用"，测试替身据此断言。
            var inserts = node.Inserts;
            for (int k = 0; k < inserts.Count; k++)
            {
                if (inserts[k].IsBypassed)
                    continue;

                inserts[k].Process(target, targetOffset, count);
            }

            var edges = node.Outputs;
            for (int e = 0; e < edges.Count; e++)
            {
                var edge = edges[e];
                var destination = runtime.Buffers[edge.TargetIndex];
                const int destinationOffset = 0;
                float gain = edge.CurrentGain;

                if (edge.DelaySamples > 0)
                {
                    runtime.DelayLines[i][e]!.Process(target, targetOffset, destination, destinationOffset, count, gain);
                }
                else
                {
                    Accumulate(target, targetOffset, destination, destinationOffset, count, gain);
                }
            }
        }

        // 3) 所有 sink（Master / Device）混进输出缓冲。
        //    每个 sink 用自己的缓冲 → Master 的增益只作用于 Master 自己的输入，
        //    "直接去设备"的通道因此不受 Master 推子影响（这是混音台的语义，不是优化）。
        for (int i = 0; i < nodes.Count; i++)
        {
            if (IsSink(nodes[i]))
                Accumulate(runtime.Buffers[i], 0, output, outputOffset, count, 1f);
        }
    }

    static bool IsSink(MixNode node) => node.Kind is MixNodeKind.Master or MixNodeKind.Device;

    // 电平抽头：AUX / Master 的推子之后取峰值（DESIGN.md §3.4）。
    // 只对这两类节点算 —— 轨的表仍走宿主的 InvokeRealtimeAmplitude（与轨道头同一个源）。
    static void MeasurePeak(float[] buffer, int offset, int count, Runtime runtime, int nodeIndex)
    {
        float left = 0;
        float right = 0;

        for (int i = 0; i < count; i++)
        {
            float l = Math.Abs(buffer[offset + i * 2]);
            float r = Math.Abs(buffer[offset + i * 2 + 1]);

            if (l > left)
                left = l;

            if (r > right)
                right = r;
        }

        runtime.PeakLeft[nodeIndex] = left;
        runtime.PeakRight[nodeIndex] = right;
    }

    static void ApplyGain(IMixChannelState? state, float[] buffer, int offset, int count)
    {
        if (state == null)
            return;

        float left = (float)(state.Volume * (1 - state.Pan));
        float right = (float)(state.Volume * (1 + state.Pan));

        // 乘 1 是逐位恒等的，直接跳过（MasterGain = 0 dB 是最常见的情形）。
        if (left == 1f && right == 1f)
            return;

        for (int i = 0; i < count; i++)
        {
            buffer[offset + i * 2] *= left;
            buffer[offset + i * 2 + 1] *= right;
        }
    }

    static void Accumulate(float[] source, int sourceOffset, float[] destination, int destinationOffset, int count, float gain)
    {
        int samples = count * 2;

        if (gain == 1f)
        {
            for (int i = 0; i < samples; i++)
                destination[destinationOffset + i] += source[sourceOffset + i];
        }
        else
        {
            for (int i = 0; i < samples; i++)
                destination[destinationOffset + i] += source[sourceOffset + i] * gain;
        }
    }

    sealed class Runtime
    {
        public readonly MixSnapshot Snapshot;
        public readonly float[][] Buffers;
        public readonly DelayLine?[][] DelayLines;
        public readonly float[] PeakLeft;
        public readonly float[] PeakRight;
        public readonly Dictionary<string, int> Index;

        // 读取位置的补偿量。与 Snapshot.MaxLatencySamples 同一来源，抄在这里是为了
        // 每块的读取是一次字段访问而不是一次接口跳转。
        public readonly int SeekOffset;

        public Runtime(MixSnapshot snapshot, int maxBlockSize)
        {
            Snapshot = snapshot;
            SeekOffset = snapshot.MaxLatencySamples;
            Buffers = new float[snapshot.Nodes.Count][];
            DelayLines = new DelayLine?[snapshot.Nodes.Count][];
            PeakLeft = new float[snapshot.Nodes.Count];
            PeakRight = new float[snapshot.Nodes.Count];
            Index = new Dictionary<string, int>(snapshot.Nodes.Count);

            for (int i = 0; i < snapshot.Nodes.Count; i++)
            {
                Buffers[i] = new float[maxBlockSize * 2];
                DelayLines[i] = new DelayLine?[snapshot.Nodes[i].Outputs.Count];
                Index[snapshot.Nodes[i].Id] = i;

                for (int e = 0; e < snapshot.Nodes[i].Outputs.Count; e++)
                {
                    int delay = snapshot.Nodes[i].Outputs[e].DelaySamples;
                    if (delay > 0)
                        DelayLines[i][e] = new DelayLine(delay);
                }
            }
        }
    }
}
