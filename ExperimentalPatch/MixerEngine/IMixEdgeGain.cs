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

    // 左右声道各自的增益（1 = unity）。默认返回 Gain（单值）= 原来的行为。
    //
    // 为什么要分开：send 可以有自己的声向（与通道条那个无关）。同一路送进两个 AUX 时，
    // 一个偏左一个偏右，不必拆两条轨道。没有声向需求的实现不用管这个属性。
    float LeftGain => Gain;

    float RightGain => Gain;

    // true = 这条边这一块不出声（send 被静音）。
    // 默认 false。与"增益 = 0"的区别：0 仍然会走一遍累加循环（写 0），
    // 而 true 直接跳过整条边 —— 静音的 send 不给目标贡献任何东西。
    bool IsSilent => false;
}
