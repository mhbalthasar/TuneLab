using Avalonia.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TuneLab.Foundation;

namespace TuneLab.Data;

// [EP-MIXER-AUTO] Pos / Value / IsSelected 的取值口径：呈现空间 = 存储空间（插入参数自动化按 part 存曲线后，
// 不再需要把存储锚点投影到别的空间——见 InsertAutomationCatalog）。
internal class AnchorPoint(double pos, double value) : IAnchorPoint
{
    public double Pos { get; } = pos;
    public double Value { get; } = value;

    public IActionEvent SelectionChanged => mSelectionChanged;
    public bool IsSelected { get => mIsSelected; set { if (mIsSelected == value) return; mIsSelected = value; mSelectionChanged.Invoke(); } }

    public static implicit operator AnchorPoint(Point point)
    {
        return new(point);
    }

    public AnchorPoint(Point point) : this(point.X, point.Y) { }

    readonly ActionEvent mSelectionChanged = new();

    bool mIsSelected = false;
}
