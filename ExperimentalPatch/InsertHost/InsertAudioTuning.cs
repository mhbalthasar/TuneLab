namespace TuneLab.ExperimentalPatch.InsertHost;

// 插入效果器音频路的可调参数。
//
// 【为什么单独一个公开类型】VstBridgeClient 在 InsertHost 程序集里是 internal，
// 而"缓冲时间"这个设置由宿主（TuneLab）拥有。宿主只引用这个公开的小门面，
// 不必把 VstBridgeClient 的内部细节（共享内存、标签、超时分支）暴露出去。
//
// BlockWaitMs 的语义：音频线程发完一块后，最多等桥这么久；超时就把这一块直通，
// 并计入 AudioStallWatch。它应该 ≥ 一块音频的时长（否则桥稍微忙一点就必然超时）。
// 宿主在设置变化时把它同步成 max(20, BufferMs)（EP-MIXER-30，真源在 insert-plugins.json）。
public static class InsertAudioTuning
{
    public static int BlockWaitMs { get; set; } = 20;
}
