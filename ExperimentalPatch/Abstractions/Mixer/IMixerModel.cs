namespace TuneLab.ExperimentalPatch.Abstractions;

// 混音台的契约：补丁向宿主索取「当前的混音状态」，宿主负责把 internal 的 Track / Project 翻过来。
//
// 【为什么是接口而不是 record】这是**活对象**：界面要双向读写、要订阅变化，不是一个快照。
// 契约里只有 BCL 类型（string / double / bool / 事件），既没有宿主类型也没有 Avalonia 类型——
// 混音台长什么样完全归补丁，宿主只提供数字。
//
// 【单一真相源】实现方（宿主适配器）直接读写 Track 的数据属性，所以本契约天然与编辑器、
// 脚本、撤销栈同源。补丁侧**不得**缓存这些值，一切以每次读取为准。
public interface IMixerModel
{
    // 按工程顺序（即显示顺序、也是 AudioGraph 的混音顺序）。
    IReadOnlyList<IMixerChannel> Channels { get; }

    // 主输出。也是一条 IMixerChannel（Name 固定、IsMute/IsSolo 恒 false），
    // 好让界面渲染通道条只有一条代码路径。
    IMixerChannel Master { get; }

    // 轨道增删 / 换序 / 换工程。收到后**整表重建**——轨道数在人类尺度上很小，
    // 重建比增量 diff 更不容易错。
    event Action? ChannelsChanged;

    // 播放中每帧一次。收到后遍历 Channels 读一次 Peak（而不是每通道各发一个事件）：
    // 派发次数少几十倍，且天然同帧一致。
    event Action? MetersUpdated;
}

public interface IMixerChannel
{
    string Name { get; }

    // 空串 = 用主题默认色。
    string ColorHex { get; }

    // 编辑器里的选中态（瞬态视图状态，不进 undo、不序列化）。
    bool IsSelected { get; }

    // dB，0 = unity；double.NegativeInfinity = 静音（轨道的 −∞ 档）。
    double GainDb { get; set; }

    // −1（全左）..+1（全右）。注意上游 pan 律是线性的：硬 pan 会让一侧变成 2×（+6 dB），
    // 而不是另一侧衰减——界面如实呈现，不替上游"修正"。
    double Pan { get; set; }

    bool IsMute { get; set; }
    bool IsSolo { get; set; }

    // 峰值电平（dB）。null = 无信号 / 被静音 / 有 solo 且本轨未 solo —— 即"此刻听不到"。
    (double LeftDb, double RightDb)? Peak { get; }

    // 别处（轨道头、脚本、撤销）改了这条轨 → 界面刷新该条。
    event Action? StateChanged;
}
