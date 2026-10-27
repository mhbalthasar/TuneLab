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

    // 改旁通态。只在 UI 线程调；音频线程下一次 Process 就能看到（实现方要保证可见性）。
    //
    // 为什么不是"换快照"：旁通不是图的结构变化，换快照会重建整个链（插件实例、延迟线…），
    // 代价大且会把插件参数丢掉。旁通是活值，和推子/静音同一条纪律。
    void SetBypassed(bool bypassed);

    // 该插件引入的延迟（样本）。0 = 无延迟。PDC 用它给别的路径补延迟线（§3.5）。
    int LatencySamples { get; }

    // [EP-MIXER-27] 插件声明的输入通道总数（VST2 的 numInputs / VST3 的主输入总线）。
    //
    // 为什么需要：侧链的既成惯例是"声明多于 2 个输入"，界面上的措辞必须如实
    // （"插件声明 4 进"），不能替用户断言"这个插件有侧链"——4 进也可能是 L/C/R。
    // 0 = 未知（测试替身 / 不是音频插件）。
    int InputChannels => 0;

    // [EP-MIXER-27] 我们能喂进去的侧链通道数：0 = 没有侧链输入。
    //
    // 上限是 2（共享环一块最多 4 声道 = 主 L/R + 键 L/R）；3 进的插件是单声道键。
    // 判定来自**已经建好的实例**，不进扫描缓存：缓存说 4 进、插件换了，就会喂错。
    int SidechainChannels => 0;

    // 就地处理一块交织立体声样本。
    //
    // buffer 的长度 >= count * 2；数据从 offset 开始。
    // 旁通时实现方应**原样返回**（拷都不用拷）——直接不进这里更快也更保险。
    // 返回 false = 这个插件这一块没处理成功（宿主据其严重程度决定是否旁通掉它）；
    // 引擎不会因为返回 false 就把缓冲区清掉，调用方自己看着办。
    void Process(Span<float> buffer, int offset, int count);

    // [EP-MIXER-27] 带侧链键的处理。sidechain 是**交织立体声**的键信号，
    // 长度 >= count * 2；空 span = 这个插入没有接侧链源。
    //
    // 默认实现 = 忽略键（七个不包装别人的实现因此一行都不用改）。
    // **包装器必须重写并转发**（WetDryInsert / TrimInsert）：漏掉的话只有
    // mix < 1 或 trim ≠ 0 的插入会丢键，默认值下一切正常，极难发现。
    void Process(Span<float> buffer, int offset, int count, ReadOnlySpan<float> sidechain)
        => Process(buffer, offset, count);
}
