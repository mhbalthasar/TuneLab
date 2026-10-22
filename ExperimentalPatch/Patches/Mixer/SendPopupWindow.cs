using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        // 不订阅每条 send 的 Changed：Sends 每次读都会新建适配器（引用不等，订阅根本留不住），
        // 而增删 send 已经由 RoutingChanged 覆盖；改增益更不该重建行——拖动中的滑块会被抽走。

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
        add.Click += () => ShowAddMenu(add);

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

    // 点「+ 添加发送」→ 弹出**可选目标**菜单，由人挑一条，而不是替他按 Master→AUX1→AUX2 的顺序硬塞。
    //
    // 菜单里只列"真能加"的：已加过的、不可用的（Cable）、输出到自己、会成环的，全都不出现。
    // 一条都不剩 → 显示禁用的「（空）」，说明"没有可加的了"，而不是弹一个空菜单让人以为坏了。
    void ShowAddMenu(Control anchor)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.Top };

        var candidates = Candidates();
        if (candidates.Count == 0)
        {
            flyout.Items.Add(new MenuItem
            {
                Header = L("（空）", "(Empty)"),
                IsEnabled = false,
            });
        }
        else
        {
            foreach (var output in candidates)
            {
                var target = output;
                var item = new MenuItem { Header = target.Name };
                item.Click += (_, _) => mChannel.AddSend(target);
                flyout.Items.Add(item);
            }
        }

        flyout.ShowAt(anchor);
    }

    // 可选目标 = 可用 + 没加过 + 引擎不会拒绝（见 IMixerChannel.CanSendTo）。
    List<IMixerOutput> Candidates()
    {
        var result = new List<IMixerOutput>();

        foreach (var output in mOutputs)
        {
            if (!output.IsAvailable || !mChannel.CanSendTo(output))
                continue;

            bool already = false;
            foreach (var send in mChannel.Sends)
            {
                if (send.Target.Id == output.Id)
                {
                    already = true;
                    break;
                }
            }

            if (!already)
                result.Add(output);
        }

        return result;
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

    // 一行 send：目标名 / 增益 / 读数，下面一小排 pre/post、声向、静音。
    //
    // 为什么分两行：一行放不下（名字 + 滑杆 + 读数 + 三个开关 + 删除键），
    // 而且 pre/post、pan、mute 是次级信息，跟增益分开更不容易误触。
    // 高度用 MixerMetrics.SendRowHeight（两条子行合计），别和 strip 上的行高混用。
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

        var remove = new ChromeButton { Text = "X", Width = 18, Height = 18, HoverColor = Palette.CloseHover, Margin = new Thickness(4, 0, 0, 0) };
        remove.Click += () => mChannel.RemoveSend(send);

        slider.ValueEdited += value =>
        {
            send.GainDb = value;
            readout.Text = Controls.Fader.FormatDb(value);
        };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*,44,20"), Height = MixerMetrics.PopupRowHeight };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(readout, 2);
        Grid.SetColumn(remove, 3);
        top.Children.Add(name);
        top.Children.Add(slider);
        top.Children.Add(readout);
        top.Children.Add(remove);

        // ---- 次级行：pre/post、声向、静音 ----
        var pre = new MixerToggle
        {
            Text = send.IsPreFader ? "PRE" : "POST",
            IsChecked = send.IsPreFader,
            CheckedColor = Palette.HighLight,
            Width = 46,
            Height = 18,
        };
        pre.Toggled += on =>
        {
            send.IsPreFader = on;
            pre.Text = on ? "PRE" : "POST";
        };

        var pan = new PanKnob
        {
            Value = send.Pan,
            Width = 30,
            Height = 30,
        };

        var panReadout = new TextBlock
        {
            Text = FormatPan(send.Pan),
            FontSize = 9,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = Palette.Brush(Palette.TextLabel),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 6, 0),
        };

        pan.PanEdited += value =>
        {
            send.Pan = value;
            panReadout.Text = FormatPan(value);
        };

        var mute = new MixerToggle
        {
            Text = "M",
            IsChecked = send.IsMute,
            CheckedColor = Palette.MuteChecked,
            Width = 24,
            Height = 18,
        };
        mute.Toggled += on => send.IsMute = on;

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("46,Auto,Auto,24,*") };
        Grid.SetColumn(pre, 0);
        Grid.SetColumn(pan, 1);
        Grid.SetColumn(panReadout, 2);
        Grid.SetColumn(mute, 3);
        bottom.Children.Add(pre);
        bottom.Children.Add(pan);
        bottom.Children.Add(panReadout);
        bottom.Children.Add(mute);

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(top);
        panel.Children.Add(bottom);
        return panel;
    }

    // 与通道条的声向读数同一口径：中央 C，两边 L20..R20。
    static string FormatPan(double pan)
        => Math.Abs(pan) < 0.02 ? "C" : (pan < 0 ? "L" : "R") + Math.Round(Math.Abs(pan) * 20);

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        mChannel.RoutingChanged -= Rebuild;
    }

    string L(string zh, string en)
        => mHost.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
}
