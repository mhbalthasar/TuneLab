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
    //
    // position 是**绝对**样本位置。混音台挂上有延迟的插件时，引擎传进来的 position
    // 已经预先往后拨了一段（= 图里所有链的最长延迟，DESIGN.md §3.11），所以实现方
    // **只管按 position 读**，不要自己再减：源侧读到哪就是哪，补偿只发生一次
    // （在 Mixer.RenderChunk 里，那一处）。
    void AddData(int position, int count, float[] buffer, int offset, float leftGain, float rightGain);
}
