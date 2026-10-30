using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer.Controls;

// 通用单值旋钮（FX 的增益 / 湿干用）。
//
// 与 PanKnob 同一套角度手势和视觉：拖动 = 指针角度，双击回默认值，滚轮微调。
// 区别只在量程与读数格式 —— 调用方给 Minimum / Maximum / DefaultValue / Formatter，
// 旋钮本身不知道"dB"或"百分比"是什么。这样 FX 行里的两个旋钮能共用一套手感，
// 也不必为了一个 -24..+24 的增益再复制一份 PanKnob。
public sealed class ValueKnob : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ValueKnob, double>(nameof(Value));

    // 当前值。范围由 Minimum / Maximum 决定；越界值在渲染时钳住。
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum { get; set; }
    public double Maximum { get; set; } = 1;
    public double DefaultValue { get; set; }

    // 滚轮一格的值增量（Shift = 1/10）。
    public double Step { get; set; } = 0.01;

    // 旋钮下方的读数。null = 用 "0.0"。
    public Func<double, string>? Formatter { get; set; }

    // false = 只画圆盘，读数由调用方排版（FX 行把读数放在旋钮右侧）。
    public bool ShowValue { get; set; } = true;

    public event Action<double>? ValueEdited;
    public event Action? EditStarted;
    public event Action? EditEnded;

    const double KnobSize = 28;
    const double MaxAngle = 3 * Math.PI / 4;   // ±135°，缺口留在正下方

    static readonly Typeface LabelTypeface = Typeface.Default;

    static ValueKnob()
    {
        AffectsRender<ValueKnob>(ValueProperty);
    }

    bool mDragging;

    public ValueKnob()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount >= 2)
        {
            Value = DefaultValue;
            ValueEdited?.Invoke(Value);
            e.Handled = true;
            return;
        }

        mDragging = true;
        e.Pointer.Capture(this);
        EditStarted?.Invoke();
        SetValueFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!mDragging)
            return;

        SetValueFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!mDragging)
            return;

        mDragging = false;
        e.Pointer.Capture(null);
        EditEnded?.Invoke();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        double step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? Step / 10 : Step;
        Value = Math.Clamp(Value + e.Delta.Y * step, Minimum, Maximum);
        ValueEdited?.Invoke(Value);
        e.Handled = true;
    }

    void SetValueFromPoint(Point point)
    {
        var center = new Point(Bounds.Width / 2, KnobSize / 2);
        double dx = point.X - center.X;
        double dy = point.Y - center.Y;

        // 0 = 正上方；正 = 顺时针。
        double angle = Math.Atan2(dx, -dy);

        // 落进底部缺口就贴到最近的一端。
        if (Math.Abs(angle) > MaxAngle)
            angle = dx >= 0 ? MaxAngle : -MaxAngle;

        double normalized = (angle + MaxAngle) / (2 * MaxAngle);
        Value = Minimum + normalized * (Maximum - Minimum);
        ValueEdited?.Invoke(Value);
    }

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        if (width <= 0 || Bounds.Height <= 0)
            return;

        double size = Math.Min(KnobSize, width);
        var center = new Point(width / 2, KnobSize / 2);
        double radius = size / 2;

        double span = Maximum - Minimum;
        double normalized = span == 0 ? 0 : Math.Clamp((Value - Minimum) / span, 0, 1);
        bool atDefault = Math.Abs(Value - DefaultValue) < Math.Abs(span) * 0.001;

        // 外圈 + 内盘
        context.DrawEllipse(Palette.Brush(Palette.Interface), null, center, radius, radius);
        context.DrawEllipse(Palette.Brush(mDragging ? Palette.ButtonNormalHover : Palette.Item), null,
            center, radius - 1.5, radius - 1.5);

        // 指针：从圆心指向当前角度；默认值用强调色。
        double angle = (normalized * 2 - 1) * MaxAngle;
        var direction = new Point(Math.Sin(angle), -Math.Cos(angle));
        var indicator = Palette.Brush(atDefault ? Palette.HighLight : Palette.LightWhite);
        var pen = new Pen(indicator, 2, lineCap: PenLineCap.Round);
        context.DrawLine(pen,
            new Point(center.X + direction.X * radius * 0.25, center.Y + direction.Y * radius * 0.25),
            new Point(center.X + direction.X * (radius - 2), center.Y + direction.Y * (radius - 2)));

        // 圆心点
        context.DrawEllipse(indicator, null, center, 1.6, 1.6);

        if (!ShowValue)
            return;

        // 数值：与旋钮底部留 3px，避免读数和圆盘贴在一起。
        var text = new FormattedText(FormatValue(Value), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            LabelTypeface, 9.5, Palette.Brush(atDefault ? Palette.TextLabel : Palette.TextNormal));
        context.DrawText(text, new Point(width / 2 - text.Width / 2, KnobSize + 3));
    }

    public string FormatValue(double value)
        => Formatter?.Invoke(value) ?? value.ToString("0.0", CultureInfo.CurrentCulture);
}
