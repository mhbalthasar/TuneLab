using System.Collections.Generic;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace TuneLab.Data;

// 一个插入效果器参数的自动化条目：曲线 + 基值 + 参数量程，挂在 InsertSlot 上（随槽位 undo / 落盘）。
//
// 【加性语义】曲线存的是**偏移量**（相对基值的叠加量），不是参数的最终值：
//   实际生效值 = clamp(BaseValue + 曲线值(tick), MinValue, MaxValue)
// 曲线的"零点"就是"不干预"：Automation.DefaultValue 恒 0，锚点值直接是偏移量。
// 基值 = 用户在参数面板里设的那个值；面板在自动化接管期间改动 = 改基值（见
// InsertInstanceFactory 的自动化登记与参数面板的写入路径），叠加关系不变。
//
// 【时间空间】锚点 Pos 是**工程绝对 tick**（与 TempoManager 同一坐标系）：Master / AUX
// 链不属于任何 part，part 相对空间没有意义。渲染 / 求值都经 TempoManager 换算；钢琴窗
// 参数区（part 相对空间）经 InsertAutomationView 适配。
//
// 【量程】建条目时从活实例抄一份参数的 Min/Max：钢琴窗 lane 的值轴、锚点钳位都不能依赖
// "实例此刻活着"（重开工程、图还没编译时 lane 也得有正确的轴）。
//
// 【曲线数学】直接复用 Automation（part = null）：锚点插值、编辑原语、undo 命令全套继承。
// 曲线对象的父对象是本条目 —— 编辑产生的 undo 命令沿父链进工程撤销栈，与增删条目合成
// 各自独立的一步。
internal class InsertSlotAutomation : DataObject
{
    public InsertSlotAutomation(DataObject parent, string paramId) : base(parent)
    {
        ParamId = paramId;
        BaseValue = new(this);
        MinValue = new(this);
        MaxValue = new(this);
        Curve = new(null, new AutomationInfo(), this);
    }

    // 插件参数 id（字符串化的 long：映射键必须是字符串）。
    public string ParamId { get; }

    // 基值：叠加的起点。自动化激活期间参数面板的改动写这里（不直接写插件）。
    public DataStruct<double> BaseValue { get; }

    // 参数量程（建条目时抄自活实例的参数声明）。
    public DataStruct<double> MinValue { get; }
    public DataStruct<double> MaxValue { get; }

    // 偏移曲线。DefaultValue 恒 0 —— 偏移语义下"默认 = 不干预"。
    public Automation Curve { get; }

    // 偏移量取值（单个 tick，无分配；给音频线程的绑定快照用不上 —— 那边走冻结数组，
    // 这边给 UI / 停止态的即时应用用）。
    public double OffsetAt(double tick) => Curve.GetValues([tick])[0];

    public InsertSlotAutomationInfo GetInfo() => new()
    {
        ParamId = ParamId,
        BaseValue = BaseValue,
        MinValue = MinValue,
        MaxValue = MaxValue,
        Points = Curve.GetInfo().Points,
    };

    public void SetInfo(InsertSlotAutomationInfo info)
    {
        BaseValue.SetInfo(info.BaseValue);
        MinValue.SetInfo(info.MinValue);
        MaxValue.SetInfo(info.MaxValue);
        // 曲线的 DefaultValue 恒 0（偏移语义），不落盘不回读。
        Curve.SetInfo(new AutomationInfo { DefaultValue = 0, Points = info.Points });
    }
}

// 落盘载荷（InsertDto.Automations 的元素）。Points = 工程绝对 tick + 偏移量。
internal class InsertSlotAutomationInfo
{
    public string ParamId { get; set; } = string.Empty;
    public double BaseValue { get; set; }
    public double MinValue { get; set; }
    public double MaxValue { get; set; }
    public List<Point> Points { get; set; } = [];
}
