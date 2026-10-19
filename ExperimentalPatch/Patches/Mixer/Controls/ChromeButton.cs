using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer.Controls;

// 自绘标题栏上的小按钮（置顶 / 关闭）。
//
// 为什么不用 Avalonia 的 Button：Fluent 主题的 Button 自带一套浅色悬浮/按下的视觉，
// 放进取色自宿主主题的标题栏里会像贴上去的补丁。这个小控件只画一个圆角底 + 一行字，
// 配色全从 Palette 取，与窗口其余部分同一来源。
public sealed class ChromeButton : Control
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<ChromeButton, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<ChromeButton, bool>(nameof(IsChecked));

    // 悬浮底色。置顶键用 ITEM，关闭键用 CloseHover（红）——那是最容易被认出来的约定。
    public static readonly StyledProperty<Color> HoverColorProperty =
        AvaloniaProperty.Register<ChromeButton, Color>(nameof(HoverColor), Palette.Item);

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    // 用于「置顶」这类开关：开启时常亮。
    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public Color HoverColor
    {
        get => GetValue(HoverColorProperty);
        set => SetValue(HoverColorProperty, value);
    }

    public event Action? Click;

    static readonly Typeface TextTypeface = Typeface.Default;
    bool mHover;

    public ChromeButton()
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

        if (change.Property == IsCheckedProperty || change.Property == TextProperty || change.Property == HoverColorProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        Color background = IsChecked
            ? Palette.Item
            : (mHover ? HoverColor : Palette.Transparent);

        context.DrawRectangle(Palette.Brush(background), null, new Rect(0, 0, width, height), 4, 4);

        if (string.IsNullOrEmpty(Text))
            return;

        var text = new FormattedText(Text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, TextTypeface, 11, Palette.Brush(Palette.LightWhite));
        context.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
    }
}
