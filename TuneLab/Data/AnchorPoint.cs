using Avalonia.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TuneLab.Foundation;

namespace TuneLab.Data;

// Pos / Value / IsSelected 做 virtual：插入参数自动化的钢琴窗适配（InsertAutomationView）
// 用子类把存储锚点**live 投影**到呈现空间（part 相对 tick + 实参值），选中态转发回存储锚点
// —— 所有经 AnchorPoint 静态类型消费的既有代码（渲染器 / 编辑操作）零改动。
internal class AnchorPoint(double pos, double value) : IAnchorPoint
{
    public virtual double Pos { get; } = pos;
    public virtual double Value { get; } = value;

    public IActionEvent SelectionChanged => mSelectionChanged;
    public virtual bool IsSelected { get => mIsSelected; set { if (mIsSelected == value) return; mIsSelected = value; mSelectionChanged.Invoke(); } }

    public static implicit operator AnchorPoint(Point point)
    {
        return new(point);
    }

    public AnchorPoint(Point point) : this(point.X, point.Y) { }

    readonly ActionEvent mSelectionChanged = new();

    bool mIsSelected = false;
}
