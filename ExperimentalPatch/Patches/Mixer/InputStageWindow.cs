using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.Patches.Mixer.Controls;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer;

// 输入级浮窗（通道条上的 [IN] 键，DESIGN §5.7.22）。
//
// 三项：输入增益（Trim）、相位反转（Ø）、高通（HPF）。位置在"音源混进通道缓冲之后、
// 推子与插入之前"，所以它影响的是**进插件之前**的信号 —— 这与 DAW 的通道输入级一致。
//
// 控件用 Avalonia 的 Slider 而不是 TuneLab.GUI 的：混音台整套界面都是补丁层自绘的，
// 补丁工程不引用 TuneLab.GUI（那是宿主程序集）。滑杆高度给足 26，避免拇指被裁。
public sealed class InputStageWindow : Window
{
    readonly IMixerChannel mChannel;
    readonly IHostServices mHost;

    readonly Slider mTrim = new();
    readonly TextBlock mTrimText = new();
    readonly StripButton mPhase = new();
    readonly Slider mHpf = new();
    readonly TextBlock mHpfText = new();

    const double MinTrimDb = -24;
    const double MaxTrimDb = 24;

    // 高通用对数刻度：0..1 映射到 20 Hz..2000 Hz（20 Hz = 关）。
    const double MinHpfHz = 20;
    const double MaxHpfHz = 2000;

    public InputStageWindow(IMixerChannel channel, IHostServices host)
    {
        mChannel = channel;
        mHost = host;

        Title = L("输入级 — ", "Input stage — ") + channel.Name;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Background = Palette.Brush(Palette.Back);

        Content = BuildContent();
        Sync();
    }

    Control BuildContent()
    {
        var close = new ChromeButton { Text = "✕", Width = 30, Height = 20, HoverColor = Palette.CloseHover };
        close.Click += Hide;

        var title = new TextBlock
        {
            Text = L("输入级 — ", "Input stage — ") + mChannel.Name,
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.Brush(Palette.TextLight),
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
            Background = Palette.Brush(Palette.Interface),
            BorderBrush = Palette.Brush(Palette.Line, 0.4),
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
            BorderBrush = Palette.Brush(Palette.Line),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    Control BuildTrimRow()
    {
        mTrim.Minimum = MinTrimDb;
        mTrim.Maximum = MaxTrimDb;
        mTrim.Height = 26;
        mTrim.VerticalAlignment = VerticalAlignment.Center;
        mTrim.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty)
                return;

            mChannel.TrimDb = Math.Round(mTrim.Value, 1);
            mTrimText.Text = FormatDb(mChannel.TrimDb);
        };

        mTrimText.FontSize = 10.5;
        mTrimText.Width = 76;
        mTrimText.TextAlignment = TextAlignment.Right;
        mTrimText.VerticalAlignment = VerticalAlignment.Center;
        mTrimText.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
        mTrimText.Foreground = Palette.Brush(Palette.TextNormal);

        return Row(L("增益", "Trim"), mTrim, mTrimText);
    }

    Control BuildPhaseRow()
    {
        mPhase.Text = L("反相 Ø", "Invert Ø");
        mPhase.Width = 96;
        mPhase.Height = 22;
        mPhase.HorizontalAlignment = HorizontalAlignment.Left;
        mPhase.Click += () =>
        {
            mChannel.PhaseInvert = !mChannel.PhaseInvert;
            Sync();
        };

        return Row(L("相位", "Phase"), mPhase, new Control());
    }

    Control BuildHpfRow()
    {
        mHpf.Minimum = 0;
        mHpf.Maximum = 1;
        mHpf.Height = 26;
        mHpf.VerticalAlignment = VerticalAlignment.Center;
        mHpf.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty)
                return;

            double hz = ToHpfHz(mHpf.Value);

            // 贴到最左 = 关。
            mChannel.HpfHz = hz <= MinHpfHz * 1.01 ? 0 : Math.Round(hz);
            mHpfText.Text = FormatHpf(mChannel.HpfHz);
        };

        mHpfText.FontSize = 10.5;
        mHpfText.Width = 76;
        mHpfText.TextAlignment = TextAlignment.Right;
        mHpfText.VerticalAlignment = VerticalAlignment.Center;
        mHpfText.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
        mHpfText.Foreground = Palette.Brush(Palette.TextNormal);

        return Row(L("高通", "HPF"), mHpf, mHpfText);
    }

    static Control Row(string label, Control control, Control value)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            Width = 52,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Palette.Brush(Palette.TextLabel),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*,80") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        Grid.SetColumn(value, 2);
        grid.Children.Add(text);
        grid.Children.Add(control);
        grid.Children.Add(value);
        return grid;
    }

    // 把数据里的值刷到控件上（Display 语义：改控件不回调写回）。
    void Sync()
    {
        mTrim.Value = Math.Clamp(mChannel.TrimDb, MinTrimDb, MaxTrimDb);
        mTrimText.Text = FormatDb(mChannel.TrimDb);

        mPhase.IsActive = mChannel.PhaseInvert;

        mHpf.Value = ToSlider(mChannel.HpfHz);
        mHpfText.Text = FormatHpf(mChannel.HpfHz);
    }

    static double ToSlider(double hz)
    {
        if (hz <= MinHpfHz)
            return 0;

        double t = Math.Log(hz / MinHpfHz) / Math.Log(MaxHpfHz / MinHpfHz);
        return Math.Clamp(t, 0, 1);
    }

    static double ToHpfHz(double slider)
    {
        return MinHpfHz * Math.Pow(MaxHpfHz / MinHpfHz, Math.Clamp(slider, 0, 1));
    }

    static string FormatDb(double db)
        => (db > 0 ? "+" : string.Empty) + db.ToString("0.0") + " dB";

    string FormatHpf(double hz)
        => hz <= MinHpfHz ? L("关", "off") : hz.ToString("0") + " Hz";

    string L(string zh, string en)
        => mHost.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
}

