using System.Collections.Generic;

namespace TuneLab.Audio;

// 多设备输出：音频线程侧的副设备出口。
//
// 主设备（设置里选的那个）仍然走原来的 SDL 回调 + 生产者线程，是**唯一的时钟主**：
// 播放头、预读头、限幅器都跟着它。副设备不参与时钟 —— 它们从主设备的每一块里
// 取走属于自己的那条设备流（见 Mixer.CopyDeviceStream），由播放层负责排队与漂移补偿。
//
// 这个接口的调用全在音频线程上；实现必须做到：
//   · DeviceIds 返回快照（音频线程只读，不分配）；
//   · GetBuffer / Push 的锁只覆盖一次字典查找与排队，临界区极短；
//   · Push 里不分配（SDL_QueueAudio 是拷贝进 SDL 内部队列）。
internal interface ISecondaryAudioSink
{
    // 这一块要渲染的副设备 id（"device:<name>"）快照。空 = 没有副设备。
    IReadOnlyList<string> DeviceIds { get; }

    // 该设备的可写缓冲（交织立体声，长度 ≥ samples*2）。
    // 设备已被移除 / 还没准备好时返回 null（这一块跳过它，不影响主输出）。
    float[]? GetBuffer(string deviceId);

    // 把刚填好的 samples 帧交给设备。实现负责队列深度与丢补样。
    void Push(string deviceId, int samples);
}
