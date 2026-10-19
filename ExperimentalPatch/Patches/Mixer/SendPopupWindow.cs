using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.Patches.Mixer.Controls;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer;

// send 浮窗：一条通道的所有 send（目的地 + 增益 + 移除），底部是"添加"。
//
// 为什么是独立浮窗而不是弹出式菜单：send 要**一边听一边调**（推着混响量找位置），
// 菜单一点就关会打断这个动作。Audition 的 Sends 面板也是常驻的。
public sealed class SendPopupWindow : Window
{
    readonly IMixerChannel mChannel;
    readonly IReadOnlyList<IMixerOutput> mOutputs;
    readonly IHostServices mHost;
    readonly bool mCanAdd;
    readonly StackPanel mRows = new() { Spacing = 4 };
    readonly TextBlock mEmpty = new();

    public SendPopupWindow(IMixerChannel channel, IReadOnlyList<IMixerOutput> outputs, IHostServices host, bool canAdd = true)
    {
        mChannel = channel;
        mOutputs = outputs;
        mHost = host;
        mCanAdd = canAdd;

        Title = L("发送", "Sends");
        Width = MixerMetrics.PopupWidth;
        MinHeight = 120;
        MaxHeight = 420;
        SizeToContent = SizeToContent.Height;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Background = Palette.Brush(Palette.Back);

        Content = BuildContent();

        mChannel.RoutingChanged += Rebuild;
        foreach (var send in mChannel.Sends)
            send.Changed += Rebuild;

        Rebuild();
    }

    Control BuildContent()
    {
        var close = new ChromeButton { Text = "✕", Width = 30, Height = 20, HoverColor = Palette.CloseHover };
        close.Click += Hide;

        var title = new TextBlock
        {
            Text = L("发送 — ", "Sends — ") + mChannel.Name,
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

        var body = new ScrollViewer
        {
            Content = new Panel { Children = { mRows, mEmpty } },
            MaxHeight = 240,
            Padding = new Thickness(MixerMetrics.PopupPadding, 8),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        mEmpty.FontSize = 10.5;
        mEmpty.Foreground = Palette.Brush(Palette.TextLabel);
        mEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        mEmpty.VerticalAlignment = VerticalAlignment.Center;
        mEmpty.IsVisible = false;

        var add = new StripButton
        {
            Text = L("+ 添加发送", "+ Add send"),
            Height = 22,
            Margin = new Thickness(MixerMetrics.PopupPadding, 2, MixerMetrics.PopupPadding, MixerMetrics.PopupPadding),
            IsVisible = mCanAdd,
        };
        add.Click += AddFirstAvailable;

        // 宿主还没接 send（阶段一）：不画"添加"按钮，直接说清为什么 —— 点了没反应的按钮比没有更糟。
        var hint = new TextBlock
        {
            Text = L("send 尚未接入", "Sends are not wired up yet"),
            FontSize = 10.5,
            Foreground = Palette.Brush(Palette.TextLabel, 0.7),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, MixerMetrics.PopupPadding),
            IsVisible = !mCanAdd,
        };

        var footer = new Panel { Children = { add, hint } };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(header);
        grid.Children.Add(body);
        grid.Children.Add(footer);

        return new Border
        {
            BorderBrush = Palette.Brush(Palette.Line),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    void AddFirstAvailable()
    {
        var used = mChannel.Sends;
        foreach (var output in mOutputs)
        {
            if (!output.IsAvailable)
                continue;

            bool already = false;
            foreach (var send in used)
            {
                if (send.Target.Id == output.Id)
                {
                    already = true;
                    break;
                }
            }

            if (already)
                continue;

            mChannel.AddSend(output);
            return;
        }
    }

    void Rebuild()
    {
        mRows.Children.Clear();

        foreach (var send in mChannel.Sends)
            mRows.Children.Add(BuildRow(send));

        mEmpty.Text = L("还没有 send", "No sends yet");
        mEmpty.IsVisible = mChannel.Sends.Count == 0;
        mRows.IsVisible = mChannel.Sends.Count > 0;
    }

    Control BuildRow(IMixerSend send)
    {
        var name = new TextBlock
        {
            Text = send.Target.Name,
            FontSize = 10.5,
            Foreground = Palette.Brush(Palette.TextNormal),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var slider = new SendGainSlider
        {
            Value = send.GainDb,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0),
        };

        var readout = new TextBlock
        {
            Text = Controls.Fader.FormatDb(send.GainDb),
            FontSize = 10,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = Palette.Brush(Palette.LightWhite),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var remove = new ChromeButton { Text = "✕", Width = 18, Height = 18, HoverColor = Palette.CloseHover, Margin = new Thickness(4, 0, 0, 0) };
        remove.Click += () => mChannel.RemoveSend(send);

        slider.ValueEdited += value =>
        {
            send.GainDb = value;
            readout.Text = Controls.Fader.FormatDb(value);
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*,44,20"), Height = MixerMetrics.PopupRowHeight };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(readout, 2);
        Grid.SetColumn(remove, 3);
        row.Children.Add(name);
        row.Children.Add(slider);
        row.Children.Add(readout);
        row.Children.Add(remove);
        return row;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        mChannel.RoutingChanged -= Rebuild;
        foreach (var send in mChannel.Sends)
            send.Changed -= Rebuild;
    }

    string L(string zh, string en)
        => mHost.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
}
