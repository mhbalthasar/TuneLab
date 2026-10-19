namespace TuneLab.ExperimentalPatch.Abstractions;

// 混音台的契约：补丁向宿主索取「当时的混音状态」，宿主负责把 internal 的 Track / Project 翻过来。
//
// 【为什么是接口而不是 record】这是**活对象**：界面要双向读写、要订阅变化，不是一个快照。
// 契约里只有 BCL 类型（string / double / bool / 事件），既没有宿主类型也没有 Avalonia 类型——
// 混音台长什么样完全归补丁，宿主只提供数字。
//
// 【单一真相源】实现方（宿主适配器）直接读写 Track / Aux 的数据属性，所以本契约天然与编辑器、
// 脚本、撤销栈同源。补丁侧**不得**缓存这些值，一切以每次读取为准。
public interface IMixerModel
{
    // 按显示顺序：先是所有轨，再是所有 AUX（不含 Master）。
    IReadOnlyList<IMixerChannel> Channels { get; }

    // 主输出。也是一条 IMixerChannel（Kind = Master），好让通道条只有一条渲染路径。
    IMixerChannel Master { get; }

    // 可以选的输出目的地（output 菜单的四类都从这里来）。
    IReadOnlyList<IMixerOutput> Outputs { get; }

    // 当前可用的插入格式（VST3 可用、VST2 仅 Windows、JSFX 未实现…）。
    IReadOnlyList<IMixerInsertFormat> InsertFormats { get; }

    // 轨 / AUX 增删、换序、换工程 → 整表重建。轨数在人类尺度上很小，重建比增量 diff 更不容易错。
    event Action? ChannelsChanged;

    // 图结构变了（output / send / 插入增删）→ 各条刷新自己的标签，但不用重建。
    event Action? RoutingChanged;

    // 播放中每帧一次。收到后遍历 Channels 读一次 Peak（而不是每通道各发一个事件）。
    event Action? MetersUpdated;

    // AUX 的增删（走宿主的 undo）。
    void AddAux();
    void RemoveAux(IMixerChannel aux);
}

public enum MixerChannelKind
{
    Track,
    Aux,
    Master,
}

// output 菜单的第一级分类（你说的 Aux / Master / Device / Cable）。
public enum MixerOutputKind
{
    Aux,
    Master,
    Device,
    Cable,
}

public interface IMixerOutput
{
    MixerOutputKind Kind { get; }

    // 稳定 id（AUX 的 id、设备的 id）。路由靠它，不靠序号。
    string Id { get; }

    // 显示名：AUX 1 / Master / 扬声器 (Realtek(R) Audio) / —
    string Name { get; }

    // 通道条那一行用的短名（84px 宽，塞不下完整设备名）：AUX 1 / Master / 设备 / Cable。
    // 菜单里仍用 Name。
    string ShortName { get; }

    // false = 菜单里画成禁用（例如 Cable 尚未实现）。
    bool IsAvailable { get; }
}

public interface IMixerChannel
{
    MixerChannelKind Kind { get; }

    // 稳定 id（轨道的 id / AUX 的 id）。路由与"不能输出到自己"的判定靠它，不靠序号。
    string Id { get; }

    string Name { get; }

    // 空串 = 用主题默认色（AUX / Master 用各自的固定色）。
    string ColorHex { get; }

    // 编辑器里的选中态（瞬态视图状态，不进 undo、不序列化）。
    bool IsSelected { get; }

    // dB，0 = unity；double.NegativeInfinity = 静音（轨道的 −∞ 档）。
    double GainDb { get; set; }

    // −1..+1。AUX 也有 pan（母线平衡），Master 没有。
    // 注意上游 pan 律是线性的：硬 pan 会让一侧变成 2×（+6 dB），界面如实呈现，不替上游"修正"。
    double Pan { get; set; }

    // Track 与 AUX 都有；Master 恒 false。
    bool IsMute { get; set; }

    // Track 的 solo 与 AUX 的 solo 是**两个独立域**：轨 solo 只压未 solo 的轨，
    // AUX solo 只压未 solo 的 AUX，互不影响，也都不影响 Master。
    bool IsSolo { get; set; }

    // 单选出入口。默认 Master；Master 自己的 output 只能是设备。
    IMixerOutput Output { get; set; }

    // 并联 send（post-fader）。同一目的地只允许一条。
    IReadOnlyList<IMixerSend> Sends { get; }
    IMixerSend AddSend(IMixerOutput target);
    void RemoveSend(IMixerSend send);

    // 串联插入（按顺序处理）。Master 也可以有。
    IReadOnlyList<IMixerInsert> Inserts { get; }
    IMixerInsert AddInsert(IMixerInsertFormat format);
    void RemoveInsert(IMixerInsert insert);

    // 峰值电平（dB）。null = 无信号 / 被静音 / 被 solo 压制 —— 即"此刻听不到"。
    (double LeftDb, double RightDb)? Peak { get; }

    // 交互式编辑会话：拖动开始时 BeginEdit()、结束时 EndEdit()。
    // 中间的多次 set 会被宿主合并成**一步 undo**（与轨道头滑杆同一套 merge 机制）。
    // 不调也安全：单次 set 就是一步 undo。
    void BeginEdit();
    void EndEdit();

    // 别处改了这条通道的推子 / pan / M / S / 选中 → 刷新该条。
    event Action? StateChanged;

    // 这条通道的 output / send / 插入变了 → 刷新标签与浮窗。
    event Action? RoutingChanged;
}

public interface IMixerSend
{
    IMixerOutput Target { get; }

    // −∞..+12 dB。增益与减益是同一个参数。
    double GainDb { get; set; }

    event Action? Changed;
}

public interface IMixerInsert
{
    // 插件名（`Pro-Q 4`），或 JSFX 的文件名。
    string Name { get; }

    // `VST3` / `VST2` / `JSFX`，显示在名字后面。
    string FormatName { get; }

    // 旁通 = 跳过这个效果器（音频直通）。与"从链里删掉"是两件事。
    bool IsBypassed { get; set; }

    // 该插件有没有自己的界面（JSFX 没有 @gfx 时也可能没有）。
    bool HasEditor { get; }

    // 打开插件自己的设置窗。没有界面时是空实现。
    void OpenEditor();

    event Action? Changed;
}

public interface IMixerInsertFormat
{
    string Name { get; }

    // false = 菜单里禁用（例如 JSFX 还没实现、或 VST2 在非 Windows 上）。
    bool IsAvailable { get; }

    // 不可用时给一句原因（"仅 Windows" / "尚未实现"），直接显示在菜单里。
    string? UnavailableReason { get; }
}
