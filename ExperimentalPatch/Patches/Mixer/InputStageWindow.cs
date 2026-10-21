using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.Patches.Mixer.Controls;
using TuneLab.ExperimentalPatch.UI;
using TuneLab.Foundation;
using TuneLab.GUI.Components;
using TuneLab.SDK;
using TuneLabSlider = TuneLab.GUI.Components.Slider;

namespace TuneLab.ExperimentalPatch.Patches.Mixer;

// 输入级浮窗（通道条上的 [IN] 键，DESIGN §5.7.22）。
//
// 三项：输入增益（Trim）、相位反转（Ø）、高通（HPF）。位置在"音源混进通道缓冲之后、
// 推子与插入之前"，所以它影响的是**进插件之前**的信号 —— 与 DAW 的通道输入级一致。
//
// 控件用 TuneLab.GUI 的 Slider / DraggableNumberBox：和宿主参数面板同一套手感
// （拖动、双击回默认、数字框拖动/双击输入），TuneLab.GUI 对补丁层开了 InternalsVisibleTo。
// 通道条本身（Fader / PanKnob / 通道头）仍是补丁自绘的 —— 那是混音台自己的视觉语言。
//
// 高通用**对数刻度**：0..1 映射到 20 Hz..2000 Hz。20 Hz 是滑杆最左端 = 关（与引擎的
// "≤ 20 Hz 视为关闭"一致），此时右侧单位标签显示「关」而不是「Hz」。
public sealed class InputStageWindow : Window
{
    readonly IMixerChannel mChannel;
    readonly IHostServices mHost;

    readonly TuneLabSlider mTrim = new();
    readonly DraggableNumberBox mTrimBox = new();
    readonly StripButton mPhase = new();
    readonly TuneLabSlider mHpf = new();
    readonly DraggableNumberBox mHpfBox = new();
    readonly TextBlock mHpfUnit = new();

    const double MinTrimDb = -24;
    const double MaxTrimDb = 24;

    // 高通的滑杆端点。引擎把 ≤ 20 Hz 当作关心（off），界面上最左端即"关"。
    const double MinHpfHz = 20;
    const double MaxHpfHz = 2000;
    const double HpfRatio = MaxHpfHz / MinHpfHz;

    public InputStageWindow(IMixerChannel channel, IHostServices host)
    {
        mChannel = channel;
        mHost = host;

        Title = Header;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        // 和宿主参数面板同一底色（TuneLab.GUI 控件自带配套的颜色）。
        Background = new SolidColorBrush(TuneLab.GUI.Style.INTERFACE);

        Content = BuildContent();
        Sync();
    }

    string Header => L("输入级 — ", "Input stage — ") + mChannel.Name;

    Control BuildContent()
    {
        var close = new ChromeButton { Text = "✕", Width = 30, Height = 20, HoverColor = Palette.CloseHover };
        close.Click += Hide;

        var title = new TextBlock
        {
            Text = Header,
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(TuneLab.GUI.Style.LIGHT_WHITE),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(close, 1);
        headerRow.Children.Add(title);
        headerRow.Children.Add(close);

        var header = new Border
        {
            Height = MixerMetrics.PopupHeaderHeight,
            Background = new SolidColorBrush(TuneLab.GUI.Style.BACK),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 64)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerRow,
        };

        var body = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(MixerMetrics.PopupPadding, 10),
        };

        body.Children.Add(BuildTrimRow());
        body.Children.Add(BuildPhaseRow());
        body.Children.Add(BuildHpfRow());

        var reset = new ChromeButton
        {
            Text = L("全部复位", "Reset all"),
            Width = 84,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        reset.Click += () =>
        {
            mChannel.TrimDb = 0;
            mChannel.PhaseInvert = false;
            mChannel.HpfHz = 0;
            Sync();
        };

        body.Children.Add(reset);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        grid.Children.Add(header);
        grid.Children.Add(body);

        return new Border
        {
            BorderBrush = new SolidColorBrush(TuneLab.GUI.Style.LINE),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    Control BuildTrimRow()
    {
        mTrim.Direction = SliderDirection.LeftToRight;
        mTrim.Height = 24;
        mTrim.VerticalAlignment = VerticalAlignment.Center;
        mTrim.SetRange(MinTrimDb, MaxTrimDb);
        // 双击回到 0 dB —— TuneLab 滑杆的默认手势。
        mTrim.DefaultValue = 0;
        mTrim.Display(mChannel.TrimDb);
        mTrim.ValueChanged.Subscribe(() =>
        {
            double db = Math.Round(mTrim.Value, 1);
            mChannel.TrimDb = db;
            mTrimBox.Display(db);
        });

        ConfigureBox(mTrimBox, MinTrimDb, MaxTrimDb, 0, 0.1);
        // 增益是正负对称的，"+"号让人一眼看出是加了还是减了。
        mTrimBox.NumberFormat = SignedDb;
        mTrimBox.Display(mChannel.TrimDb);
        mTrimBox.ValueChanged.Subscribe(() =>
        {
            double db = Math.Round(mTrimBox.Value, 1);
            mChannel.TrimDb = db;
            mTrim.Display(db);
        });

        return Row(L("增益", "Trim"), mTrim, mTrimBox, "dB");
    }

    Control BuildPhaseRow()
    {
        mPhase.Text = L("反相 Ø", "Invert Ø");
        mPhase.Width = 96;
        mPhase.Height = 22;
        mPhase.HorizontalAlignment = HorizontalAlignment.Left;
        mPhase.IsActive = mChannel.PhaseInvert;
        mPhase.Click += () =>
        {
            mChannel.PhaseInvert = !mChannel.PhaseInvert;
            mPhase.IsActive = mChannel.PhaseInvert;
        };

        return Row(L("相位", "Phase"), mPhase, new Control(), string.Empty);
    }

    Control BuildHpfRow()
    {
        mHpf.Direction = SliderDirection.LeftToRight;
        mHpf.Height = 24;
        mHpf.VerticalAlignment = VerticalAlignment.Center;
        mHpf.Scale = NormalizedScale.Custom(
            t => MinHpfHz * Math.Pow(HpfRatio, Clamp01(t)),
            v => Math.Log(Math.Max(v, MinHpfHz) / MinHpfHz) / Math.Log(HpfRatio));
        // 双击回"关"（滑杆最左端）。
        mHpf.DefaultValue = MinHpfHz;
        mHpf.Display(ToSliderHz(mChannel.HpfHz));
        mHpf.ValueChanged.Subscribe(() =>
        {
            double hz = ToModelHz(mHpf.Value);
            mChannel.HpfHz = hz;
            mHpfBox.Display(hz);
            RefreshHpfUnit(hz);
        });

        // 0 = 关，允许直接输入 0 关掉高通。
        ConfigureBox(mHpfBox, 0, MaxHpfHz, 0, 1);
        mHpfBox.Display(mChannel.HpfHz);
        mHpfBox.ValueChanged.Subscribe(() =>
        {
            double hz = ToModelHz(mHpfBox.Value);
            mChannel.HpfHz = hz;
            mHpf.Display(ToSliderHz(hz));
            mHpfBox.Display(hz);
            RefreshHpfUnit(hz);
        });

        mHpfUnit.FontSize = 10;
        mHpfUnit.VerticalAlignment = VerticalAlignment.Center;
        mHpfUnit.HorizontalAlignment = HorizontalAlignment.Left;
        mHpfUnit.Foreground = new SolidColorBrush(TuneLab.GUI.Style.TEXT_LABEL);

        return Row(L("高通", "HPF"), mHpf, mHpfBox, mHpfUnit);
    }

    static void ConfigureBox(DraggableNumberBox box, double min, double max, double defaultValue, double step)
    {
        box.MinValue = min;
        box.MaxValue = max;
        box.DefaultValue = defaultValue;
        box.Step = step;
        box.Width = 68;
        box.Height = 24;
        box.VerticalAlignment = VerticalAlignment.Center;
        box.NumberFormat = NumberFormat.Decimals(step < 1 ? 1 : 0);
    }

    static Control Row(string label, Control control, Control value, string unit)
        => Row(label, control, value, new TextBlock
        {
            Text = unit,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = new SolidColorBrush(TuneLab.GUI.Style.TEXT_LABEL),
        });

    static Control Row(string label, Control control, Control value, TextBlock unit)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(TuneLab.GUI.Style.TEXT_LABEL),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*,76,26") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        Grid.SetColumn(value, 2);
        Grid.SetColumn(unit, 3);
        grid.Children.Add(text);
        grid.Children.Add(control);
        grid.Children.Add(value);
        grid.Children.Add(unit);
        return grid;
    }

    // 把数据里的值刷到控件上（Display 只改显示，不触发 ValueChanged）。
    void Sync()
    {
        double trim = Math.Clamp(mChannel.TrimDb, MinTrimDb, MaxTrimDb);
        mTrim.Display(trim);
        mTrimBox.Display(trim);

        mPhase.IsActive = mChannel.PhaseInvert;

        double hpf = ToModelHz(mChannel.HpfHz);
        mHpf.Display(ToSliderHz(hpf));
        mHpfBox.Display(hpf);
        RefreshHpfUnit(hpf);
    }

    void RefreshHpfUnit(double hz)
        => mHpfUnit.Text = hz <= MinHpfHz ? L("关", "off") : "Hz";

    // 模型值（0 = 关）→ 滑杆值（最左端 20 Hz）。中间值原样透传。
    static double ToSliderHz(double modelHz)
        => modelHz <= MinHpfHz ? MinHpfHz : Math.Clamp(modelHz, MinHpfHz, MaxHpfHz);

    // 滑杆/数字框的值 → 模型值：贴到最左端（或 ≤ 20）就是关。
    static double ToModelHz(double hz)
    {
        if (!double.IsFinite(hz) || hz <= MinHpfHz * 1.005)
            return 0;

        return Math.Round(Math.Clamp(hz, MinHpfHz, MaxHpfHz));
    }

    static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    // 增益框：正数带 "+"，负数自带 "-"，一位小数（与滑杆的 0.1 dB 步进一致）。
    static readonly INumberFormat SignedDb = NumberFormat.Custom(
        v => (v > 0 ? "+" : string.Empty) + v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
        t => double.TryParse(t, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null);

    string L(string zh, string en)
        => mHost.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;

}

