using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.Patches.Mixer.Controls;
using TuneLab.ExperimentalPatch.UI;
using TuneLab.GUI.Components;
using TuneLab.SDK;

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
        Width = MixerMetrics.SendPopupWidth;
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
            // 内边距放在内容上，不放在 ScrollViewer 上：ScrollViewer 的 Padding 不参与
            // SizeToContent 的期望高度，最后一行会被裁掉（send 行的 dB 读数尤其明显）。
            Content = new Panel
            {
                Margin = new Thickness(MixerMetrics.PopupPadding, 8),
                Children = { mRows, mEmpty },
            },
            MaxHeight = 240,
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

    // 一行 send：目标名 / 增益旋钮 + 可键入的 dB 读数 / PRE-POST / 声向旋钮 / M / 删除。
    //
    // 为什么合成一行：send 的五个参数是一起调的（推混响量时顺手看 pre/post 和 pan），
    // 拆成上下两行会让视线在同一个 send 里来回跳；一行也把行高从 62 降到 48。
    // 增益旋钮下面留出完整的读数位（PanKnob 的读数同理），行高不再裁掉旋钮的数值。
    Control BuildRow(IMixerSend send)
    {
        // 名字列固定 82px：单行名字靠右贴旋钮，短名字（Delay）和长名字（Hall Reverb）
        // 的右缘都对齐；放不下的名字在列内换行（最多三行，仍放不下就省略号收尾），
        // 换行后的文本块在列内左对齐。行高 48 足够容下三行 10.5px 文本。
        var name = new TextBlock
        {
            Text = send.Target.Name,
            FontSize = 10.5,
            Foreground = Palette.Brush(Palette.TextNormal),
            VerticalAlignment = VerticalAlignment.Center,
            // 单行名字靠右贴旋钮；换行后的多行文本块在列内左对齐，读起来更顺。
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // 极长名字会被换行上限截断；悬停时仍能看到完整目标名。
        ToolTip.SetTip(name, send.Target.Name);

        var gainKnob = new SendGainKnob
        {
            Value = send.GainDb,
            Width = 48,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // dB 读数：双击原地键入，拖动也能改（与参数面板同一个 DraggableNumberBox）。
        // MinValue 故意留空：−∞ 是合法值，不能被当成"低于 −48"钳回 −48；
        // 有限值的边界在 SendDbFormat.Parse 和 Response 里钳。
        var gainBox = new DraggableNumberBox
        {
            Width = 48,
            Height = 16,
            MaxValue = SendGainKnob.MaximumDb,
            Step = 0.1,
            NumberFormat = SendDbFormat,
            Response = DragResponse.Custom((start, delta) =>
            {
                // −∞ 起拖时从最小可调值开始，否则会一直卡在 −∞。
                double from = double.IsNegativeInfinity(start) ? SendGainKnob.MinimumDb : start;
                return Math.Clamp(from + delta.X, SendGainKnob.MinimumDb, SendGainKnob.MaximumDb);
            }),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        gainBox.Display(send.GainDb);

        var gainModule = new StackPanel
        {
            Width = 48,
            VerticalAlignment = VerticalAlignment.Top,
        };
        gainModule.Children.Add(gainKnob);
        gainModule.Children.Add(gainBox);

        var pre = new MixerToggle
        {
            Text = send.IsPreFader ? "PRE" : "POST",
            IsChecked = send.IsPreFader,
            CheckedColor = Palette.HighLight,
            Width = 46,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pre.Toggled += on =>
        {
            send.IsPreFader = on;
            pre.Text = on ? "PRE" : "POST";
        };

        var pan = new PanKnob
        {
            Value = send.Pan,
            Width = 42,
            Height = MixerMetrics.SendRowHeight,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var mute = new MixerToggle
        {
            Text = "M",
            IsChecked = send.IsMute,
            CheckedColor = Palette.MuteChecked,
            Width = 24,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
        };
        mute.Toggled += on => send.IsMute = on;

        var remove = new ChromeButton
        {
            Text = "X",
            Width = 18,
            Height = 18,
            HoverColor = Palette.CloseHover,
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += () => mChannel.RemoveSend(send);

        gainKnob.ValueEdited += value =>
        {
            send.GainDb = value;
            gainBox.Display(value);
        };
        gainKnob.EditStarted += mChannel.BeginEdit;
        gainKnob.EditEnded += mChannel.EndEdit;

        gainBox.ValueChanged.Subscribe(() =>
        {
            send.GainDb = gainBox.Value;
            gainKnob.Value = gainBox.Value;
        });
        gainBox.ValueWillChange.Subscribe(mChannel.BeginEdit);
        gainBox.ValueCommitted.Subscribe(mChannel.EndEdit);

        pan.PanEdited += value => send.Pan = value;
        pan.EditStarted += mChannel.BeginEdit;
        pan.EditEnded += mChannel.EndEdit;

        var row = new Grid
        {
            Height = MixerMetrics.SendRowHeight,
            // 名字列用 *：没有滚动条时正好是 MixerMetrics.SendNameWidth（82px），
            // 发送多到出现竖向滚动条时自动让出滚动条的宽度，不会把右边的 X 挤出窗口。
            ColumnDefinitions = new ColumnDefinitions("*,48,46,42,24,18"),
            ColumnSpacing = 4,
        };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(gainModule, 1);
        Grid.SetColumn(pre, 2);
        Grid.SetColumn(pan, 3);
        Grid.SetColumn(mute, 4);
        Grid.SetColumn(remove, 5);
        row.Children.Add(name);
        row.Children.Add(gainModule);
        row.Children.Add(pre);
        row.Children.Add(pan);
        row.Children.Add(mute);
        row.Children.Add(remove);
        return row;
    }

    // dB 读数的双向格式：显示保留一位小数，−∞ 显示成 "−∞"；键入时接受 "−∞" / "-inf"。
    // 有限值在 Parse 里钳到 [−48, +12]，DraggableNumberBox 的 MinValue 留空（见上）。
    static readonly INumberFormat SendDbFormat = NumberFormat.Custom(
        Fader.FormatDb,
        text =>
        {
            text = text.Trim();
            if (text is "-∞" or "−∞" or "-inf" or "-Infinity")
                return double.NegativeInfinity;

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var db)
                ? Math.Clamp(db, SendGainKnob.MinimumDb, SendGainKnob.MaximumDb)
                : null;
        });

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        mChannel.RoutingChanged -= Rebuild;
    }

    string L(string zh, string en)
        => mHost.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
}
