namespace TuneLab.ExperimentalPatch.MixerEngine;

// 一条通道的**活**状态：每个块渲染时现读，不在快照里存值。
//
// 为什么这样拆：推子/声像/静音每帧都可能变，如果把它们塞进快照，推一下推子就要重建整张图；
// 而重建要分配缓冲、要换指针。拆开之后，**结构**（谁连到谁）才进快照，**数值**留在这里。
public interface IMixChannelState
{
    // 线性增益（1.0 = unity）。dB → 线性的换算归宿主，引擎只做乘法。
    double Volume { get; }

    // −1（全左）.. +1（全右）。
    double Pan { get; }

    // 门控：静音 / 未 solo / −∞ 档。**宿主算好**再给引擎——solo 的判据（"有任一轨 solo"）
    // 是宿主语义，引擎不该知道。返回 true 时该节点不出声（轨道连音源都不读，总线直接输出静音）。
    bool IsSilent { get; }
}
