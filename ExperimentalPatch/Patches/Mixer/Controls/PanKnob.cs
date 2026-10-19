using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer.Controls;

// 声像旋钮（−1 全左 .. +1 全右，0 = 居中）。
//
// 为什么用旋钮而不是横向滑杆：通道条是**窄而高**的（84px 宽），横向滑杆在这里行程只有几十像素；
// 旋钮在小面积里手感更好，且"居中 / 偏了多少"一眼可读——与 Audition 的通道条一致。
//
// 拖动是**角度式**：指针相对圆心的角度 = 值。底部有一段死区（旋钮的"缺口"），
// 指到缺口里就贴到最近的一端——这是旋钮的通用手感。
public sealed class PanKnob : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<PanKnob, double>(nameof(Value));

    // −1..+1。
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    const double KnobSize = 28;
    const double LabelHeight = 13;
    const double MaxAngle = 3 * Math.PI / 4;   // ±135°，缺口留在正下方
    const double CenterEpsilon = 0.02;

    // 用户改动了值（不是程序设值）。写入端靠它回写数据源。
    public event Action<double>? PanEdited;

    // 一次拖动 = 一步 undo（同 Fader）。
    public event Action? EditStarted;
    public event Action? EditEnded;

    static PanKnob()
    {
        // Render() 直接读 Value，属性变化要触发重绘。
        AffectsRender<PanKnob>(ValueProperty);
    }

    static readonly Typeface LabelTypeface = Typeface.Default;

    bool mDragging;

    public PanKnob()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public static string FormatPan(double pan)
    {
        if (Math.Abs(pan) < CenterEpsilon)
            return "C";

        int percent = (int)Math.Round(Math.Abs(pan) * 100);
        return (pan < 0 ? "L" : "R") + percent.ToString(CultureInfo.InvariantCulture);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount >= 2)
        {
            Value = 0;
            PanEdited?.Invoke(Value);
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
        Value = Math.Clamp(Value + e.Delta.Y * 0.05, -1, 1);
        PanEdited?.Invoke(Value);
        e.Handled = true;
    }

    void SetValueFromPoint(Point point)
    {
        var center = new Point(Bounds.Width / 2, KnobSize / 2);        double dx = point.X - center.X;
        double dy = point.Y - center.Y;

        // 0 = 正上方；正 = 顺时针。
        double angle = Math.Atan2(dx, -dy);

        // 落进底部缺口就贴到最近的一端（按横向偏向决定贴哪边）。
        if (Math.Abs(angle) > MaxAngle)
            angle = dx >= 0 ? MaxAngle : -MaxAngle;

        Value = Math.Clamp(angle / MaxAngle, -1, 1);
        PanEdited?.Invoke(Value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        if (width <= 0 || Bounds.Height <= 0)
            return;

        double size = Math.Min(KnobSize, width);
        var center = new Point(width / 2, KnobSize / 2);
        double radius = size / 2;
        bool centered = Math.Abs(Value) < CenterEpsilon;

        // 外圈 + 内盘
        context.DrawEllipse(Palette.Brush(Palette.Interface), null, center, radius, radius);
        context.DrawEllipse(Palette.Brush(mDragging ? Palette.ButtonNormalHover : Palette.Item), null, center,
            radius - 1.5, radius - 1.5);

        // 指针：从圆心指向当前角度；居中时用强调色（一眼看出"回正了"）
        double angle = Value * MaxAngle;
        var direction = new Point(Math.Sin(angle), -Math.Cos(angle));
        var indicator = Palette.Brush(centered ? Palette.HighLight : Palette.LightWhite);
        var pen = new Pen(indicator, 2, lineCap: PenLineCap.Round);
        context.DrawLine(pen,
            new Point(center.X + direction.X * radius * 0.25, center.Y + direction.Y * radius * 0.25),
            new Point(center.X + direction.X * (radius - 2), center.Y + direction.Y * (radius - 2)));

        // 圆心点
        context.DrawEllipse(indicator, null, center, 1.6, 1.6);

        // 数值
        var text = new FormattedText(FormatPan(Value), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelTypeface, 9.5, Palette.Brush(centered ? Palette.TextLabel : Palette.TextNormal));
        context.DrawText(text, new Point(width / 2 - text.Width / 2, KnobSize + 1));
    }
}
