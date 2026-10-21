namespace TuneLab.ExperimentalPatch.Abstractions;

// 混音台的契约：补丁向宿主索取「当时的混音状态」，宿主负责把 internal 的 Track / Project 翻过来。
//
// 【为什么是接口而不是 record】这是**活对象**：界面要双向读写、要订阅变化，不是一个快照。
// 契约里只有 BCL 类型（string / double / bool / 事件），既没有宿主类型也没有 Avalonia 类型——
// 混音台长什么样完全归补丁，宿主只提供数字。
//
// 【单一真相源】实现方（宿主适配器）直接读写 Track / Aux 的数据属性，所以本契约天然与编辑器、
// 脚本、撤销栈同源。补丁侧**不得**缓存这些值，一切以每次读取为准。
// 通道在组内的移动方式（轨道头右键菜单）。
public enum MixerChannelMove
{
    Up,
    Down,
    Top,
    Bottom,
}

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

    // ── 能力标志 ──
    // 阶段一只有混音器（轨 → Master）：AUX / send / 插入还没接。界面据此**隐藏**对应的"添加"入口，
    // 而不是让用户点出一个异常，或点一个没反应的按钮（DESIGN.md §3.3 同一条纪律）。
    bool SupportsAux { get; }
    bool SupportsSends { get; }
    bool SupportsInserts { get; }

    // 轨 / AUX 增删、换序、换工程 → 整表重建。轨数在人类尺度上很小，重建比增量 diff 更不容易错。
    event Action? ChannelsChanged;

    // 图结构变了（output / send / 插入增删）→ 各条刷新自己的标签，但不用重建。
    event Action? RoutingChanged;

    // 播放中每帧一次。收到后遍历 Channels 读一次 Peak（而不是每通道各发一个事件）。
    event Action? MetersUpdated;

    // AUX 的增删（走宿主的 undo）。
    void AddAux();
    void RemoveAux(IMixerChannel aux);

    // ── 通道位置（轨道头右键菜单）──
    //
    // 只在**所属组内**移动：轨道组（Project.Tracks）与 AUX 组（Project.Auxes）各自独立，
    // 轨道不会因为"置底"跑到 AUX 后面去。顺序就是工程里的顺序，主界面的轨道头会跟着变。
    bool CanMoveChannel(IMixerChannel channel, MixerChannelMove move);
    void MoveChannel(IMixerChannel channel, MixerChannelMove move);

    // "移动到.."：把 channel 移到 target 现在的位置（同组内才生效）。
    void MoveChannelTo(IMixerChannel channel, IMixerChannel target);}

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

// 通道在调音台里的固定位置：固定后不随横向滚动，靠左或靠右堆叠（DESIGN.md §6.8）。
public enum MixerPin
{
    None,
    Left,
    Right,
}

public interface IMixerChannel
{
    MixerChannelKind Kind { get; }

    // 稳定 id（轨道的 id / AUX 的 id）。路由与"不能输出到自己"的判定靠它，不靠序号。
    string Id { get; }

    // 可写：调音台里双击名字就地改名（写回 Track.Name / AuxBus.Name，一步 undo）。
    string Name { get; set; }

    // 固定到窗口左/右（不随横向滚动）。Master 恒为 None。
    MixerPin Pin { get; set; }

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

    // 这条通道能不能把信号送到 target。
    // 假 = "输出到自己"或"会成环"——两者都会被引擎拒绝，菜单据此**不把它列出来**，
    // 免得用户点了个"点了没反应"的目标。Master 恒 false（它没有 send）。
    bool CanSendTo(IMixerOutput target);

    // 输入级（Master 没有：它没有音源可 Trim / 高通）。默认值 = 关闭。
    bool HasInputStage { get; }
    double TrimDb { get; set; }        // -24..+24 dB，0 = 不变
    bool PhaseInvert { get; set; }     // true = 整条通道反相
    double HpfHz { get; set; }         // ≤20 = 关；否则二阶 12 dB/oct 高通

    // 串联插入（按顺序处理）。Master 也可以有。
    IReadOnlyList<IMixerInsert> Inserts { get; }
    IMixerInsert AddInsert(IMixerInsertFormat format);
    void RemoveInsert(IMixerInsert insert);

    // 插入链的顺序调整（处理顺序 = 列表顺序）。delta = -1 上移 / +1 下移。
    // 返回 false 表示已经在头/尾（界面据此把 ▲▼ 置灰）。
    bool CanMoveInsert(IMixerInsert insert, int delta);
    void MoveInsert(IMixerInsert insert, int delta);
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

    // 插件暴露了可枚举的参数吗？（TuneLab 接管的参数面板据此决定是否可用）
    bool HasParameters { get; }

    // 参数面板：宿主自己把插件的参数列成滑杆/数值，不依赖插件 GUI。
    // 插件 GUI 画不出来（缺依赖、没有 GL、干脆没有界面）时，这是唯一的调节途径。
    // context = 窗口抬头用的「哪条轨道的哪个 FX」，由界面拼好传进来。
    void OpenParameters(string context);

    event Action? Changed;
}

// 一个"还能被告知自己是哪个插件"的插入（补丁侧从扫描缓存里点选时用）。
//
// 【为什么单独一个接口而不是塞进 IMixerInsert】IMixerInsert 是**已挂上**的插入：
// 它已经有名字、有旁通态、有界面。而"绑定到哪个插件"只发生在刚 `AddInsert(format)`
// 那一刻——宿主建槽位时还不知道用户选了什么（补丁先调 AddInsert 拿一个壳，再点菜单填内容）。
// 合成一个接口会让"每个插入都能被重新绑到别的插件"，那不是我们想要的语义
// （换插件 = 换一个槽位，见 IMixerInsertFormat 的注释）。
public interface IInsertPluginTarget
{
    // 把宿主扫描到的那个插件绑到这个槽位上。
    // pluginId = 稳定标识（VST3 classId / VST2 uniqueId），locator = 后端定位用的路径。
    void SetPlugin(string pluginId, string name, string locator);
}

public interface IMixerInsertFormat
{
    string Name { get; }

    // false = 菜单里禁用（例如 JSFX 还没实现、或 VST2 在非 Windows 上）。
    bool IsAvailable { get; }

    // 不可用时给一句原因（"仅 Windows" / "尚未实现"），直接显示在菜单里。
    string? UnavailableReason { get; }
}






