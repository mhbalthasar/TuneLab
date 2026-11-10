using TuneLab.Foundation;
using TuneLab.SDK;

namespace TuneLab.Data;

// 一个插入效果器参数的自动化条目：基值 + 参数量程，挂在 InsertSlot 上（随槽位 undo / 落盘）。
//
// 【加性语义】每个 part 各有一条**偏移曲线**（住在 part.Automations 里，键 = "{slotId}:{paramId}"，
// 见 InsertAutomationCatalog），真正生效值 = 各 part 曲线求和后再叠加基值：
//   实际生效值 = clamp(BaseValue + Σ_part 曲线值(tick), MinValue, MaxValue)
// 曲线存的是**偏移量**（相对基值的叠加量），不是参数的最终值；"零点" = "这个 part 不干预"。
// 基值 = 用户在参数面板里设的那个值；面板在自动化接管期间改动 = 改基值（见
// InsertInstanceFactory 的自动化登记与参数面板的写入路径），叠加关系不变。
//
// 【为什么曲线按 part 存】AUX / Master 链不属于任何 part：多条轨的 part 都可能是它的
// 控制者，每个 part 各画各的 lane、求和生效（同一条轨的多个 part 亦然）。曲线住在 part 上，
// 天然随 part 移动（part 相对 tick）/ 删除 / undo，也让"同一参数被多个 part 控制"不需要
// 稳定的 part 身份落盘（part 没有持久 id）。
//
// 【量程】建条目时从活实例抄一份参数的 Min/Max：钢琴窗 lane 的值轴、锚点钳位都不能依赖
// "实例此刻活着"（重开工程、图还没编译时 lane 也得有正确的轴）。
internal class InsertSlotAutomation : DataObject
{
    public InsertSlotAutomation(DataObject parent, string paramId) : base(parent)
    {
        ParamId = paramId;
        BaseValue = new(this);
        MinValue = new(this);
        MaxValue = new(this);
    }

    // 插件参数 id（字符串化的 long：映射键必须是字符串）。
    public string ParamId { get; }

    // 基值：叠加的起点。自动化激活期间参数面板的改动写这里（不直接写插件）。
    public DataStruct<double> BaseValue { get; }

    // 参数量程（建条目时抄自活实例的参数声明）。
    public DataStruct<double> MinValue { get; }
    public DataStruct<double> MaxValue { get; }

    public InsertSlotAutomationInfo GetInfo() => new()
    {
        ParamId = ParamId,
        BaseValue = BaseValue,
        MinValue = MinValue,
        MaxValue = MaxValue,
    };

    public void SetInfo(InsertSlotAutomationInfo info)
    {
        BaseValue.SetInfo(info.BaseValue);
        MinValue.SetInfo(info.MinValue);
        MaxValue.SetInfo(info.MaxValue);
        // 条目只承载基值 / 量程；曲线按 part 住（见 InsertAutomationCatalog）。
    }
}

// 条目的信息载荷。二期首版的全局曲线（工程绝对 tick）是**落盘 DTO** 的遗留字段
// （InsertAutomationDto.Points），装载时由 ExperimentalPatchMixerPersistence.MigrateLegacyCurve
// 迁进 part 曲线；这里的条目信息里不再有曲线。
internal class InsertSlotAutomationInfo
{
    public string ParamId { get; set; } = string.Empty;
    public double BaseValue { get; set; }
    public double MinValue { get; set; }
    public double MaxValue { get; set; }
}
