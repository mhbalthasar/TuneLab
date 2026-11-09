using System;
using System.Collections.Generic;
using System.Linq;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace TuneLab.Data;

// 插入参数自动化的钢琴窗适配视图（[EP-MIXER-AUTO] 二期）。
//
// 【它解决什么】底部参数区整个工作在 **part 相对 tick** 空间，而插入自动化的数据存
// **工程绝对 tick**（Master / AUX 链不属于任何 part）；且 lane 的值轴要显示**实参**
// （钳位后的基值+偏移），数据存的是偏移量。本视图把两者翻译给渲染器与编辑操作：
//   · tick：呈现空间 = 绝对 − part.Pos（live 读，part 只是坐标原点的持有者）；写入时加回。
//   · 值：呈现 DefaultValue = 基值（DataStruct 本尊——渲染器的"锚点绝对值 = 锚点偏移 +
//     DefaultValue"口径正好落在加性语义上）；锚点以 PresentedAnchor **live 投影**（选中态
//     转发回存储锚点，因此跨手势的选中与既有轨完全一致）。
//   · GetValues(ticks) = clamp(基值 + Hermite(偏移)) —— **与音频线程的求值逐位同一公式**
//     （见 MixerAutomation.Binding），画出来的就是听到的。
//
// 【编辑】所有编辑原语翻译后委托给存储曲线（InsertSlotAutomation.Curve）——undo 命令、
// 合并通知全部落在真数据上，本视图不持任何命令状态、不订阅任何事件（可以随用随建，无生命周期）。
//
// 【IDataObject】借壳（IDataObject.Wrapper）：文档身份（Head/Commit/Modified/merge/undo）
// 全部转发给存储曲线；GetInfo/SetInfo 按存储口径直接往返（仅供通用的序列化通道）。
internal class InsertAutomationView : IAutomation
{
    public InsertAutomationView(IMidiPart part, InsertSlotAutomation entry)
    {
        mPart = part;
        mEntry = entry;
    }

    readonly IMidiPart mPart;
    readonly InsertSlotAutomation mEntry;

    // ── 呈现 ──

    public IMidiPart Part => mPart;

    // 呈现基线 = 基值本尊：渲染器的"绝对值 = 锚点偏移 + DefaultValue"正好等于实参。
    public IDataProperty<double> DefaultValue => mEntry.BaseValue;

    public IReadOnlyList<AnchorPoint> Points => new PresentedPoints(mPart, mEntry);

    // 区间失效原样转发（坐标是绝对 tick，消费方只拿它做重绘失效）。
    public IActionEvent<double, double> RangeModified => mEntry.Curve.RangeModified;

    public double[] GetValues(IReadOnlyList<double> ticks) => Evaluate(mPart, mEntry, ticks);

    // 无实例、无订阅的静态求值：渲染器每帧走这里（GetFinalAutomationValues 的 Insert 分支），
    // 不能为它建视图实例（订阅会随帧泄漏）。
    public static double[] Evaluate(IMidiPart part, InsertSlotAutomation entry, IReadOnlyList<double> ticks)
    {
        double partPos = part.Pos.Value;
        var absolute = new double[ticks.Count];
        for (int i = 0; i < ticks.Count; i++)
            absolute[i] = ticks[i] + partPos;

        var offsets = entry.Curve.GetValues(absolute);

        double baseValue = entry.BaseValue.Value;
        double min = entry.MinValue.Value;
        double max = entry.MaxValue.Value;

        for (int i = 0; i < offsets.Length; i++)
            offsets[i] = Math.Clamp(baseValue + offsets[i], min, max);

        return offsets;
    }

    // ── 编辑（呈现空间 → 存储空间的翻译）──

    public void AddLine(IReadOnlyList<AnchorPoint> points, double extend)
    {
        double partPos = mPart.Pos.Value;
        mEntry.Curve.AddLine(points.Select(p => new AnchorPoint(p.Pos + partPos, p.Value)).ToList(), extend);
    }

    public void Clear(double start, double end, double extend)
    {
        double partPos = mPart.Pos.Value;
        // 存储的 DefaultValue 恒 0（偏移语义）→ 清空 = 回到基值，与呈现基线一致。
        mEntry.Curve.Clear(start + partPos, end + partPos, extend);
    }

    public void InsertPoint(AnchorPoint point)
    {
        double partPos = mPart.Pos.Value;
        mEntry.Curve.InsertPoint(new AnchorPoint(point.Pos + partPos, point.Value));
    }

    public void DeletePoints(double start, double end)
    {
        double partPos = mPart.Pos.Value;
        mEntry.Curve.DeletePoints(start + partPos, end + partPos);
    }

    public void DeletePoints(IReadOnlyList<AnchorPoint> points)
    {
        // 删除按身份匹配（存储列表里的实例）；呈现点是投影，按位置找回去。
        double partPos = mPart.Pos.Value;
        var stored = mEntry.Curve.Points
            .Where(p => points.Any(presented => presented.Pos == p.Pos - partPos))
            .ToList();
        mEntry.Curve.DeletePoints(stored);
    }

    public void MoveSelectedPoints(double offsetPos, double offsetValue)
    {
        // 偏移量本身与空间无关（位移不变换）。
        mEntry.Curve.MoveSelectedPoints(offsetPos, offsetValue);
    }

    // ── IDataObject<AutomationInfo>：文档身份全部转发给存储曲线（借壳语义，手工转发 ——
    // IDataObject.Wrapper 在 Foundation 程序集内部，跨程序集不可见）──

    public IModifiedEvent Modified => mEntry.Curve.Modified;
    public IModifiedEvent WillModify => mEntry.Curve.WillModify;
    public Head Head => mEntry.Curve.Head;
    public void Attach(IDataObject parent) => mEntry.Curve.Attach(parent);
    public void Detach() => mEntry.Curve.Detach();
    public IDisposable MergeNotify() => mEntry.Curve.MergeNotify();
    public void BeginMergeNotify() => mEntry.Curve.BeginMergeNotify();
    public void EndMergeNotify() => mEntry.Curve.EndMergeNotify();
    public bool Pushable() => mEntry.Curve.Pushable();
    public bool Commit() => mEntry.Curve.Commit();
    public bool Discard() => mEntry.Curve.Discard();
    public bool DiscardTo(Head head) => mEntry.Curve.DiscardTo(head);
    public bool Undo() => mEntry.Curve.Undo();
    public bool Redo() => mEntry.Curve.Redo();

    public AutomationInfo GetInfo() => mEntry.Curve.GetInfo();

    public void SetInfo(AutomationInfo info) => mEntry.Curve.SetInfo(info);

    // ── 呈现锚点列表：live 投影 ──

    // 每次访问都生成投影锚点（廉价：只持引用）；选中态经 virtual 转发回存储锚点，
    // 所以"选中 → 移动"跨手势与既有轨行为一致。
    sealed class PresentedPoints(IMidiPart part, InsertSlotAutomation entry) : IReadOnlyList<AnchorPoint>
    {
        public int Count => entry.Curve.Points.Count;

        public AnchorPoint this[int index]
        {
            get
            {
                var stored = entry.Curve.Points[index];
                return new PresentedAnchor(stored, part, entry);
            }
        }

        public IEnumerator<AnchorPoint> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
                yield return this[i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // 存储锚点的呈现投影：tick 平移到 part 相对、值叠加基值并钳位到参数量纲（画在 lane 轴内），
    // 选中态原样转发。存储锚点被替换（编辑重建列表）后，旧投影仍指旧实例 —— 消费方每次读取
    // 都重新投影，不会持有过期列表。
    sealed class PresentedAnchor(AnchorPoint source, IMidiPart part, InsertSlotAutomation entry)
        : AnchorPoint(0, 0)
    {
        public override double Pos => source.Pos - part.Pos.Value;

        public override double Value
        {
            get
            {
                double effective = Math.Clamp(
                    entry.BaseValue.Value + source.Value, entry.MinValue.Value, entry.MaxValue.Value);
                return effective - entry.BaseValue.Value;
            }
        }

        public override bool IsSelected
        {
            get => source.IsSelected;
            set => source.IsSelected = value;
        }
    }
}
