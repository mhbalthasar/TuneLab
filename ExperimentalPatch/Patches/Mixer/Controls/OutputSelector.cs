using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer.Controls;

// 通道条底部的 output 行：当前目的地 + 下拉箭头。点击由窗口弹菜单。
//
// 为什么不直接用 ComboBox：宿主主题里的 ComboBox 是给"属性面板"那类场景的（高、带边框、带下拉列表控件），
// 放进 84px 宽的通道条会撑破布局；而且我们要的菜单是**四类分组 + 子菜单**，不是平铺列表。
public sealed class OutputSelector : Control
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<OutputSelector, string>(nameof(Text), string.Empty);

    // 直接进设备的通道（绕过 Master）要显眼一点——这是"听不到 Master 推子"的唯一提示。
    public static readonly StyledProperty<bool> IsDirectProperty =
        AvaloniaProperty.Register<OutputSelector, bool>(nameof(IsDirect));

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsDirect
    {
        get => GetValue(IsDirectProperty);
        set => SetValue(IsDirectProperty, value);
    }

    public event Action? Click;

    static readonly Typeface TextTypeface = Typeface.Default;
    bool mHover;

    public OutputSelector()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        Click?.Invoke();
        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        mHover = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        mHover = false;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty || change.Property == IsDirectProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        context.DrawRectangle(Palette.Brush(mHover ? Palette.ButtonNormalHover : Palette.Interface),
            null, new Rect(0, 0, width, height), 3, 3);

        // 直接进设备：左侧一道强调条 + 文字用强调色，提醒"这条不过 Master"。
        if (IsDirect)
            context.FillRectangle(Palette.Brush(Palette.HighLight), new Rect(0, 2, 2, height - 4));

        var foreground = IsDirect ? Palette.HighLight : Palette.TextNormal;
        var text = new FormattedText(Text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, TextTypeface, 9.5, Palette.Brush(foreground));

        // 短名（见 IMixerOutput.ShortName）保证不会溢出；这里不再做换行/裁剪，
        // 因为 FormattedText 的 MaxTextWidth 会**换行**而不是省略，反而把下面几行顶出去。
        context.DrawText(text, new Point(6, (height - text.Height) / 2));

        // ▾
        double cx = width - 8;
        double cy = height / 2;
        var arrow = StreamGeometry.Parse($"M{cx - 3},{cy - 2} L{cx},{cy + 2} L{cx + 3},{cy - 2}");
        context.DrawGeometry(null, new Pen(Palette.Brush(Palette.LightWhite, 0.8), 1.2), arrow);
    }
}
