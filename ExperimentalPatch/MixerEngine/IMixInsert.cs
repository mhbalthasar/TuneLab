namespace TuneLab.ExperimentalPatch.MixerEngine;

// 一个插入效果器（混音图里的一环）。
//
// 【实时安全】Process 在音频线程调用：不分配、不加锁、不碰 UI、不抛异常。
// 参数改动 / 加删插入都发生在 UI 线程，然后换快照（§3.7）；音频线程只读不可变的结构。
//
// 【为什么它在这里而不是在 InsertHost】IMixSource / IMixChannelState / IMixEdgeGain
// 都在 MixerEngine：引擎只认自己的接口，宿主与 InsertHost 都实现它们。
// 插入也一样——引擎要知道"这里有个东西要处理、它报多少延迟"，但不需要知道那是 VST3 还是
// 一个假的测试效果。后端可换是本层能独立单测的原因。
public interface IMixInsert
{
    // 显示名（`Pro-Q 4`），也用于日志。
    string Name { get; }

    // 旁通 = 直通（音频原样穿过）。槽位与顺序都不变，与"从链里删掉"是两件事。
    bool IsBypassed { get; }

    // 该插件引入的延迟（样本）。0 = 无延迟。PDC 用它给别的路径补延迟线（§3.5）。
    int LatencySamples { get; }

    // 就地处理一块交织立体声样本。
    //
    // buffer 的长度 >= count * 2；数据从 offset 开始。
    // 旁通时实现方应**原样返回**（拷都不用拷）——直接不进这里更快也更保险。
    // 返回 false = 这个插件这一块没处理成功（宿主据其严重程度决定是否旁通掉它）；
    // 引擎不会因为返回 false 就把缓冲区清掉，调用方自己看着办。
    void Process(Span<float> buffer, int offset, int count);
}
