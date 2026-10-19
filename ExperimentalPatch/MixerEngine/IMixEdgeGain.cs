namespace TuneLab.ExperimentalPatch.MixerEngine;

// 一条边的**活**增益。
//
// 为什么需要它：send 的推子每帧都可能动。如果增益烘进快照，拖一下 send 推子就要重建整张图
// （重新分配缓冲、换指针）——那是每秒几十次的分配，正好踩在实时安全的红线上。
// 把增益做成活值之后，只有"结构"变化（加删 send / 改 output）才重建。
public interface IMixEdgeGain
{
    // 线性增益（1 = unity）。
    float Gain { get; }
}
