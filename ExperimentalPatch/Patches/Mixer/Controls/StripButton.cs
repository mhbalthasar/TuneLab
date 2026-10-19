using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer.Controls;

// 通道条上的计数按钮（`Send 2` / `FX 1`）。
//
// 两种状态要一眼分得开（这是设计里明确要求的）：
//   - 没有内容：黑底 + 暗字 = 禁用感，但**仍然可点**（点了会打开空的浮窗去添加）；
//   - 有内容：ITEM 底 + 亮字 = 高亮，文字里带数量。
//
// 它不吃真禁用态（IsEnabled=false），因为"空链"和"不可用"是两件事：空链是可点进去添加的。
public sealed class StripButton : Control
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<StripButton, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<StripButton, bool>(nameof(IsActive));

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    // true = 这条通道挂了几条 send / 几个插入
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public event Action? Click;

    static readonly Typeface TextTypeface = Typeface.Default;
    bool mHover;

    public StripButton()
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

        if (change.Property == TextProperty || change.Property == IsActiveProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0 || string.IsNullOrEmpty(Text))
            return;

        var background = IsActive
            ? (mHover ? Palette.ButtonPrimaryHover : Palette.Item)
            : (mHover ? Palette.ButtonNormalHover : Palette.Back);

        var foreground = IsActive ? Palette.LightWhite : Palette.TextLabel;
        var brush = IsActive || mHover
            ? Palette.Brush(foreground)
            : Palette.Brush(foreground, 0.55);

        context.DrawRectangle(Palette.Brush(background), null, new Rect(0, 0, width, height), 3, 3);

        var text = new FormattedText(Text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, TextTypeface, 9.5, brush);
        context.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
    }
}
