using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer.Controls;

// 竖推子 + dB 刻度。
//
// 为什么自绘而不是用宿主那套：`GainSlider` 是 TuneLab.GUI 的 internal（补丁碰不到），
// 而且它是 12px 高的横滑杆——调音台要的正是"竖着、行程长、能读刻度"这个反面。
//
// 刻度是**分段线性**的：0 dB 落在 66% 处、−12..+6 这一段（真正会去调的区间）占了 2/3 行程，
// 与真实调音台的刻度一个思路。最低处有一个 −∞ 档（与轨道头的约定一致：gain <= −24 即静音）。
public sealed class Fader : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<Fader, double>(nameof(Value));

    // dB，0 = unity；double.NegativeInfinity = 静音档。
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public const double MinimumDb = -24;
    public const double MaximumDb = 6;
    public const double UnityDb = 0;

    // 断点位置（0 = 最底，1 = 最顶）。
    const double InfPosition = 0.0;
    const double MinDbPosition = 0.08;
    const double Minus12Position = 0.34;
    const double UnityPosition = 0.67;
    const double MaxDbPosition = 1.0;
    const double InfDeadZone = 0.04;

    const double GrooveWidth = 7;
    const double CapWidth = 24;
    const double CapHeight = 13;
    const double VerticalPadding = 10;   // 上下留出推子帽的一半，帽到顶/底时不越界

    static readonly (string Label, double Db)[] Ticks =
    [
        ("+6", MaximumDb),
        ("0", UnityDb),
        ("-12", -12),
        ("-24", MinimumDb),
        ("-∞", double.NegativeInfinity),
    ];

    // 用户改动了值（不是程序设值）。写入端靠它回写数据源——比订阅属性变化再判断"是不是自己在改"干净。
    public event Action<double>? ValueEdited;

    // 一次拖动 = 一步 undo：按下开始会话、松手结束（宿主侧用 merge 作用域实现）。
    public event Action? EditStarted;
    public event Action? EditEnded;

    static Fader()
    {
        // Render() 直接读 Value，属性变化要触发重绘。
        AffectsRender<Fader>(ValueProperty);
    }

    static readonly Typeface LabelTypeface = Typeface.Default;

    bool mDragging;

    public Fader()
    {
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
    }

    // ── 刻度映射（公开，通道条要靠它对齐别的东西）──

    public static double DbToPosition(double db)
    {
        if (double.IsNegativeInfinity(db))
            return InfPosition;

        if (db >= MaximumDb)
            return MaxDbPosition;

        if (db >= UnityDb)
            return UnityPosition + (db - UnityDb) / (MaximumDb - UnityDb) * (MaxDbPosition - UnityPosition);

        if (db >= -12)
            return Minus12Position + (db + 12) / 12 * (UnityPosition - Minus12Position);

        if (db >= MinimumDb)
            return MinDbPosition + (db - MinimumDb) / 12 * (Minus12Position - MinDbPosition);

        return InfPosition;
    }

    public static double PositionToDb(double position)
    {
        position = Math.Clamp(position, 0, 1);

        if (position < InfDeadZone)
            return double.NegativeInfinity;

        if (position >= UnityPosition)
            return UnityDb + (position - UnityPosition) / (MaxDbPosition - UnityPosition) * (MaximumDb - UnityDb);

        if (position >= Minus12Position)
            return -12 + (position - Minus12Position) / (UnityPosition - Minus12Position) * 12;

        if (position <= MinDbPosition)
            return MinimumDb;

        return MinimumDb + (position - MinDbPosition) / (Minus12Position - MinDbPosition) * 12;
    }

    // 把界面文字转成 dB 读数（"+6.0" / "0.0" / "−∞"）。
    public static string FormatDb(double db)
        => double.IsNegativeInfinity(db) ? "-∞" : db.ToString("0.0", CultureInfo.InvariantCulture);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount >= 2)
        {
            Value = UnityDb;   // 双击回 unity（调音台通用手势）
            ValueEdited?.Invoke(Value);
            e.Handled = true;
            return;
        }

        mDragging = true;
        e.Pointer.Capture(this);
        EditStarted?.Invoke();
        SetValueFromY(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!mDragging)
            return;

        SetValueFromY(e.GetPosition(this).Y);
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

        // 滚轮 1 dB/格；Shift 精细到 0.1 dB。碰到 −∞ 档时以 −24 为起点往上走。
        double step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.1 : 1.0;
        double from = double.IsNegativeInfinity(Value) ? MinimumDb : Value;
        Value = Math.Clamp(from + e.Delta.Y * step, MinimumDb, MaximumDb);
        ValueEdited?.Invoke(Value);
        e.Handled = true;
    }

    void SetValueFromY(double y)
    {
        var travel = Math.Max(1, Bounds.Height - VerticalPadding * 2);
        var position = 1 - (y - VerticalPadding) / travel;
        Value = PositionToDb(position);
        ValueEdited?.Invoke(Value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        var travel = Math.Max(1, height - VerticalPadding * 2);

        double Y(double position) => VerticalPadding + (1 - position) * travel;

        // 刻度（含 −∞ 档与 unity 的加粗刻度）
        foreach (var (label, db) in Ticks)
        {
            var y = Y(DbToPosition(db));
            bool unity = db == UnityDb;

            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelTypeface, 9, Palette.Brush(unity ? Palette.TextLight : Palette.TextLabel));
            context.DrawText(text, new Point(MixerMetrics.ScaleWidth - 6 - text.Width, y - text.Height / 2));

            var tickBrush = Palette.Brush(unity ? Palette.TextLight : Palette.Line);
            context.FillRectangle(tickBrush, new Rect(MixerMetrics.ScaleWidth - 4, y - 0.5, unity ? 4 : 3, 1));
        }

        // 推子槽
        var grooveX = MixerMetrics.ScaleWidth + (width - MixerMetrics.ScaleWidth - GrooveWidth) / 2;
        var groove = new Rect(grooveX, VerticalPadding - 2, GrooveWidth, travel + 4);
        context.DrawRectangle(Palette.Brush(Palette.Item), null, groove, GrooveWidth / 2, GrooveWidth / 2);
        // 槽中央的一道暗线：让"槽"读起来像凹进去的
        context.FillRectangle(Palette.Brush(Palette.Dark), new Rect(groove.Center.X - 1, groove.Y, 2, groove.Height));

        var capY = Y(DbToPosition(Value));

        // 已抬起的那段（帽以下）加一点亮色，一眼看出"加了多少"
        var filled = new Rect(grooveX, capY, GrooveWidth, groove.Bottom - capY);
        if (filled.Height > 0)
            context.FillRectangle(Palette.Brush(Palette.HighLight, 0.32), filled);

        // 推子帽
        var capX = groove.Center.X - CapWidth / 2;
        var cap = new Rect(capX, capY - CapHeight / 2, CapWidth, CapHeight);
        var capColor = mDragging ? Palette.White : Palette.LightWhite;
        context.DrawRectangle(Palette.Brush(capColor), null, cap, 3, 3);
        context.FillRectangle(Palette.Brush(Palette.Dark, 0.75), new Rect(cap.X + 3, cap.Center.Y - 1, cap.Width - 6, 2));
    }
}
