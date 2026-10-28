using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer.Controls;

// send 增益旋钮（−48..+12 dB，−∞ 档在左下死区）。
//
// 与 PanKnob 同一套角度手势和视觉；数值不画在旋钮里——下面那个可双击键入的 dB 读数
// 才是权威显示，旋钮负责"推"的手感。量程沿用旧 SendGainSlider：0 dB 在 80.8% 处，
// −12..+6 这段常用区间集中在右半边，和横向滑杆的刻度一致。
public sealed class SendGainKnob : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SendGainKnob, double>(nameof(Value));

    // dB，0 = unity；double.NegativeInfinity = 静音档。
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public const double MinimumDb = -48;
    public const double MaximumDb = 12;

    // 最左端（−135°）起的一小段是 −∞ 档（与旧横向滑杆的 4% 死区一致）。
    const double InfDeadZone = 0.04;
    const double KnobSize = 28;
    const double MaxAngle = 3 * Math.PI / 4;   // ±135°，缺口留在正下方

    // 用户改动了值（不是程序设值）。写入端靠它回写数据源。
    public event Action<double>? ValueEdited;

    // 一次拖动 = 一步 undo（同 Fader / PanKnob）。
    public event Action? EditStarted;
    public event Action? EditEnded;

    static SendGainKnob()
    {
        AffectsRender<SendGainKnob>(ValueProperty);
    }

    bool mDragging;

    public SendGainKnob()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public static double DbToPosition(double db)
    {
        if (double.IsNegativeInfinity(db))
            return 0;

        var clamped = Math.Clamp(db, MinimumDb, MaximumDb);
        return InfDeadZone + (clamped - MinimumDb) / (MaximumDb - MinimumDb) * (1 - InfDeadZone);
    }

    public static double PositionToDb(double position)
    {
        position = Math.Clamp(position, 0, 1);

        if (position < InfDeadZone)
            return double.NegativeInfinity;

        return MinimumDb + (position - InfDeadZone) / (1 - InfDeadZone) * (MaximumDb - MinimumDb);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount >= 2)
        {
            Value = 0;
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

        double step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.1 : 1.0;
        double from = double.IsNegativeInfinity(Value) ? MinimumDb : Value;
        Value = Math.Clamp(from + e.Delta.Y * step, MinimumDb, MaximumDb);
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

        // 底部缺口：正中拉到底 = −∞（增益旋钮的"拉到底静音"），偏左/右则贴到对应端。
        if (Math.Abs(angle) > MaxAngle)
        {
            angle = Math.Abs(dx) < Bounds.Width * 0.15
                ? -MaxAngle
                : dx >= 0 ? MaxAngle : -MaxAngle;
        }

        Value = PositionToDb((angle + MaxAngle) / (2 * MaxAngle));
        ValueEdited?.Invoke(Value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        if (width <= 0 || Bounds.Height <= 0)
            return;

        double size = Math.Min(KnobSize, width);
        var center = new Point(width / 2, KnobSize / 2);
        double radius = size / 2;
        bool muted = double.IsNegativeInfinity(Value);
        bool unity = !muted && Math.Abs(Value) < 0.05;

        // 外圈 + 内盘
        context.DrawEllipse(Palette.Brush(Palette.Interface), null, center, radius, radius);
        context.DrawEllipse(Palette.Brush(mDragging ? Palette.ButtonNormalHover : Palette.Item), null, center,
            radius - 1.5, radius - 1.5);

        // 指针：从圆心指向当前角度；0 dB 用强调色，−∞ 用弱色。
        double angle = (DbToPosition(Value) * 2 - 1) * MaxAngle;
        var direction = new Point(Math.Sin(angle), -Math.Cos(angle));
        var indicator = Palette.Brush(unity ? Palette.HighLight : muted ? Palette.TextLabel : Palette.LightWhite);
        var pen = new Pen(indicator, 2, lineCap: PenLineCap.Round);
        context.DrawLine(pen,
            new Point(center.X + direction.X * radius * 0.25, center.Y + direction.Y * radius * 0.25),
            new Point(center.X + direction.X * (radius - 2), center.Y + direction.Y * (radius - 2)));

        // 圆心点
        context.DrawEllipse(indicator, null, center, 1.6, 1.6);
    }
}
