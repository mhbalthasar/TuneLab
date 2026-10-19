using System;

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
            position += n;
            offset += n * 2;
            count -= n;
        }
    }

    void RenderChunk(Runtime runtime, int position, int count, float[] output, int outputOffset, IMixSource? masterInput)
    {
        var nodes = runtime.Snapshot.Nodes;

        // 1) 清空所有非 Master 节点的输入缓冲（Master 直接用调用方的输出缓冲）。
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Kind != MixNodeKind.Master)
                Array.Clear(runtime.Buffers[i], 0, count * 2);
        }

        // 2) 按拓扑序逐节点：出声 → 推子 → 分发。
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            bool isMaster = node.Kind == MixNodeKind.Master;
            var target = isMaster ? output : runtime.Buffers[i];
            int targetOffset = isMaster ? outputOffset : 0;

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
                    node.Source.AddData(position, count, target, targetOffset, left, right);
                }
            }
            else if (isMaster)
            {
                // 预览音在 Master 增益之前混进来 —— 与旧路径（MixData → AddData(预览) → MasterGain）同序。
                masterInput?.AddData(position, count, output, outputOffset, 1f, 1f);
                ApplyGain(node.State, target, targetOffset, count);
            }
            else
            {
                var state = node.State;
                if (state == null || state.IsSilent)
                    hasSignal = false;
                else
                    ApplyGain(state, target, targetOffset, count);
            }

            if (!hasSignal)
                continue;

            var edges = node.Outputs;
            for (int e = 0; e < edges.Count; e++)
            {
                var edge = edges[e];
                bool toMaster = nodes[edge.TargetIndex].Kind == MixNodeKind.Master;
                var destination = toMaster ? output : runtime.Buffers[edge.TargetIndex];
                int destinationOffset = toMaster ? outputOffset : 0;

                if (edge.DelaySamples > 0)
                {
                    runtime.DelayLines[i][e]!.Process(target, targetOffset, destination, destinationOffset, count, edge.Gain);
                }
                else
                {
                    Accumulate(target, targetOffset, destination, destinationOffset, count, edge.Gain);
                }
            }
        }
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

        public Runtime(MixSnapshot snapshot, int maxBlockSize)
        {
            Snapshot = snapshot;
            Buffers = new float[snapshot.Nodes.Count][];
            DelayLines = new DelayLine?[snapshot.Nodes.Count][];

            for (int i = 0; i < snapshot.Nodes.Count; i++)
            {
                Buffers[i] = new float[maxBlockSize * 2];
                DelayLines[i] = new DelayLine?[snapshot.Nodes[i].Outputs.Count];

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
