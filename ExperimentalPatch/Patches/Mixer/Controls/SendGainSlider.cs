using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer.Controls;

// send 的横向增益推子（−∞..+12 dB）。
//
// 为什么不复用 Fader：那个是竖向、而且刻度域是 −24..+6（与轨道头对齐）。send 需要更宽的量程
// （把一条轨大量推进混响时 +12 有用），两者混用会让"同一根推子在不同地方读数不同"。
public sealed class SendGainSlider : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SendGainSlider, double>(nameof(Value));

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public const double MinimumDb = -48;
    public const double MaximumDb = 12;

    // 最左边一小段是 −∞ 档（与轨道推子的约定一致：到底 = 静音）。
    const double InfDeadZone = 0.04;

    public event Action<double>? ValueEdited;

    bool mDragging;

    static SendGainSlider()
    {
        AffectsRender<SendGainSlider>(ValueProperty);
    }

    public SendGainSlider()
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
        SetFromX(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!mDragging)
            return;

        SetFromX(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!mDragging)
            return;

        mDragging = false;
        e.Pointer.Capture(null);
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

    void SetFromX(double x)
    {
        double width = Math.Max(1, Bounds.Width - 10);
        Value = PositionToDb((x - 5) / width);
        ValueEdited?.Invoke(Value);
    }

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        const double handleWidth = 9;
        const double handleHeight = 14;

        double trackHeight = 4;
        double trackTop = (height - trackHeight) / 2;
        double usable = Math.Max(1, width - handleWidth);

        context.DrawRectangle(Palette.Brush(Palette.Back), null,
            new Rect(handleWidth / 2, trackTop, usable, trackHeight), 2, 2);

        double position = DbToPosition(Value);
        double centerX = handleWidth / 2 + position * usable;

        // 已推起的一段（左→手柄）
        context.FillRectangle(Palette.Brush(Palette.Item),
            new Rect(handleWidth / 2, trackTop, Math.Max(0, centerX - handleWidth / 2), trackHeight));

        var handle = new Rect(centerX - handleWidth / 2, (height - handleHeight) / 2, handleWidth, handleHeight);
        context.DrawRectangle(Palette.Brush(mDragging ? Palette.White : Palette.LightWhite), null, handle, 2, 2);
        context.FillRectangle(Palette.Brush(Palette.Dark, 0.7),
            new Rect(handle.X + 2, handle.Center.Y - 0.5, handle.Width - 4, 1));
    }
}
