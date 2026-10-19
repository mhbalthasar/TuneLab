namespace TuneLab.ExperimentalPatch.MixerEngine;

// 一个通道的音源（轨道节点才有）。
//
// 【为什么把左右增益传进来，而不是让引擎自己乘】
// 宿主今天是在**逐段**（每个 part / 每个 audio source）循环里做 `buffer += leftGain * sample`。
// 如果改成"先把各段裸样本求和、再整体乘增益"，浮点结果就不再与旧路径逐位相同
// （`lv*(L1+L2)` ≠ `lv*L1 + lv*L2`）。DESIGN.md §7.2 把"退化图与旧 MixData 逐样本 bit 相同"
// 定成了硬验收，所以增益必须留在逐段的循环里。
//
// 实现方（宿主）应当直接复用既有的逐段求和代码，而不是另写一份。
public interface IMixSource
{
    // 把 [position, position + count) 的立体声样本按给定左右增益**累加**进 buffer（交织、2 声道）。
    // buffer 的写起点是 offset（以 float 计）。
    void AddData(int position, int count, float[] buffer, int offset, float leftGain, float rightGain);
}
