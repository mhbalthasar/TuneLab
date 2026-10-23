using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer.Controls;

// 通道条上的方形开关（M / S）。
//
// 配色沿用轨道头的约定：未勾选 = BACK 底 + LIGHT_WHITE 字母；勾选 = 该功能自己的色 + 白字母
// （M 青、S 紫，数值取自 TrackHead.cs）。这样调音台上的 M/S 与轨道头上的 M/S 是同一个视觉语言。
public sealed class MixerToggle : Control
{
    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<MixerToggle, bool>(nameof(IsChecked));

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<MixerToggle, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<Color> CheckedColorProperty =
        AvaloniaProperty.Register<MixerToggle, Color>(nameof(CheckedColor));

    // [EP-MIXER-25] 角标：开关本身没亮，但"有个附加状态开着"。
    // 用于 solo-safe —— S 没按下去（没在 solo），但这条通道是安全的。
    // 不占用额外宽度（画在右上角），所以 M/S 两键的排布不用改。
    public static readonly StyledProperty<bool> IsMarkedProperty =
        AvaloniaProperty.Register<MixerToggle, bool>(nameof(IsMarked));

    public bool IsMarked
    {
        get => GetValue(IsMarkedProperty);
        set => SetValue(IsMarkedProperty, value);
    }

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Color CheckedColor
    {
        get => GetValue(CheckedColorProperty);
        set => SetValue(CheckedColorProperty, value);
    }

    static readonly Typeface LetterTypeface = Typeface.Default;

    // 用户点了开关（不是程序设值）。
    public event Action<bool>? Toggled;

    bool mHover;

    public MixerToggle()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        IsChecked = !IsChecked;
        Toggled?.Invoke(IsChecked);
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

        if (change.Property == IsCheckedProperty || change.Property == TextProperty ||
            change.Property == CheckedColorProperty || change.Property == IsMarkedProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        var background = IsChecked
            ? CheckedColor
            : (mHover ? Palette.ButtonNormalHover : Palette.Back);

        var letter = IsChecked ? Palette.White : Palette.LightWhite;

        context.DrawRectangle(Palette.Brush(background), null, new Rect(0, 0, width, height), 3, 3);

        // 角标：右上角一个小三角。用"形状"而不是"换个底色" —— 底色已经被
        // IsChecked 占用，而这两件事可以同时为真（solo 中的安全通道）。
        if (IsMarked)
        {
            var mark = new Avalonia.Media.StreamGeometry();
            using (var geo = mark.Open())
            {
                geo.BeginFigure(new Point(width - 1, 1), true);
                geo.LineTo(new Point(width - 1, 7));
                geo.LineTo(new Point(width - 7, 1));
                geo.EndFigure(true);
            }

            context.DrawGeometry(Palette.Brush(Palette.SoloChecked), null, mark);
        }

        if (string.IsNullOrEmpty(Text))
            return;

        var text = new FormattedText(Text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, LetterTypeface, 10.5, Palette.Brush(letter));
        context.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
    }
}
