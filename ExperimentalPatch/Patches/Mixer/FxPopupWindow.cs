using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.Patches.Mixer.Controls;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer;

// 插入效果浮窗：按顺序列出这条通道的插入，每行有「旁通」与「界面」两个按钮。
//
// 两个按钮的分工（按你的要求）：
//   - 旁通 = 跳过这个效果器（音频直通），槽位还在、顺序不变；行会变暗。
//   - 界面 = 打开插件自己的设置窗（JSFX 的 @gfx 也是走这里；没有界面的插件该按钮置灰）。
// 顺序 = 处理顺序（自上而下）。v1 不做拖拽排序，用 ▲▼。
public sealed class FxPopupWindow : Window
{
    readonly IMixerChannel mChannel;
    readonly IReadOnlyList<IMixerInsertFormat> mFormats;
    readonly IHostServices mHost;
    readonly bool mCanAdd;
    readonly StackPanel mRows = new() { Spacing = 4 };
    readonly TextBlock mEmpty = new();

    public FxPopupWindow(IMixerChannel channel, IReadOnlyList<IMixerInsertFormat> formats, IHostServices host, bool canAdd = true)
    {
        mChannel = channel;
        mFormats = formats;
        mHost = host;
        mCanAdd = canAdd;

        Title = L("插入效果", "Inserts");
        Width = 430;
        MinHeight = 120;
        MaxHeight = 460;
        SizeToContent = SizeToContent.Height;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Background = Palette.Brush(Palette.Back);

        Content = BuildContent();

        mChannel.RoutingChanged += Rebuild;
        foreach (var insert in mChannel.Inserts)
            insert.Changed += Rebuild;

        Rebuild();
    }

    Control BuildContent()
    {
        var close = new ChromeButton { Text = "✕", Width = 30, Height = 20, HoverColor = Palette.CloseHover };
        close.Click += Hide;

        var title = new TextBlock
        {
            Text = L("插入效果 — ", "Inserts — ") + mChannel.Name,
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
            MaxHeight = 260,
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
            Text = L("+ 添加效果", "+ Add insert"),
            Height = 22,
            Margin = new Thickness(MixerMetrics.PopupPadding, 2, MixerMetrics.PopupPadding, MixerMetrics.PopupPadding),
            IsVisible = mCanAdd,
        };
        add.Click += () => ShowAddMenu(add);
        var hint = new TextBlock
        {
            Text = L("插入效果尚未接入", "Inserts are not wired up yet"),
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
        foreach (var format in mFormats)
        {
            if (!format.IsAvailable)
                continue;

            mChannel.AddInsert(format);
            return;
        }
    }

    // 点"+ 添加效果"：按格式分组列出缓存里的插件（不现扫，缓存见 InsertPluginSettings）。
    // 空 / 全部不可用时直接说明原因，而不是给一个点了没反应的按钮。
    void ShowAddMenu(Control anchor)
    {
        var plugins = mHost.InsertPlugins?.CachedPlugins ?? [];

        var menu = new ContextMenu();

        foreach (var format in mFormats)
        {
            var candidates = plugins.Where(p => p.Format == format.Name).ToList();

            var header = new MenuItem { Header = format.Name, IsEnabled = false };
            menu.Items.Add(header);

            if (!format.IsAvailable)
            {
                menu.Items.Add(DisabledItem(format.UnavailableReason ?? "unavailable"));
                continue;
            }

            if (candidates.Count == 0)
            {
                menu.Items.Add(DisabledItem(L("（没有扫描到，去「插件扫描路径」扫一次）",
                    "(nothing scanned yet — check the plugin scan paths)")));
                continue;
            }

            foreach (var plugin in candidates)
            {
                var item = new MenuItem { Header = plugin.Name };
                item.IsEnabled = plugin.IsAvailable;

                if (!plugin.IsAvailable)
                    item.Header = plugin.Name + "   — " + (plugin.UnavailableReason ?? "");

                item.Click += (_, _) => AddInsert(format, plugin);
                menu.Items.Add(item);
            }
        }

        menu.Open(anchor);
    }

    static MenuItem DisabledItem(string header) => new() { Header = header, IsEnabled = false };

    void AddInsert(IMixerInsertFormat format, InsertPluginEntry plugin)
    {
        var insert = mChannel.AddInsert(format);

        // 插件的定位信息（路径 / classId）写回数据槽位 —— 宿主据此建实例。
        if (insert is IInsertPluginTarget target)
            target.SetPlugin(plugin.PluginId, plugin.Name, plugin.Locator);
    }

    void Rebuild()
    {
        mRows.Children.Clear();

        for (int index = 0; index < mChannel.Inserts.Count; index++)
            mRows.Children.Add(BuildRow(mChannel.Inserts[index], index));

        mEmpty.Text = L("还没有插入效果", "No inserts yet");
        mEmpty.IsVisible = mChannel.Inserts.Count == 0;
        mRows.IsVisible = mChannel.Inserts.Count > 0;
    }

    Control BuildRow(IMixerInsert insert, int index)
    {
        var order = new TextBlock
        {
            Text = (index + 1).ToString(),
            FontSize = 10,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = Palette.Brush(Palette.TextLabel),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var name = new TextBlock
        {
            Text = insert.Name,
            FontSize = 11,
            Foreground = Palette.Brush(insert.IsBypassed ? Palette.TextLabel : Palette.TextNormal),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var format = new TextBlock
        {
            Text = insert.FormatName,
            FontSize = 9,
            Foreground = Palette.Brush(Palette.TextLabel, 0.8),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameRow.Children.Add(name);
        nameRow.Children.Add(format);

        var bypass = new ChromeButton
        {
            Text = L("旁通", "Bypass"),
            Width = 46,
            Height = 18,
            IsChecked = insert.IsBypassed,
        };
        bypass.Click += () => insert.IsBypassed = !insert.IsBypassed;

        var editor = new ChromeButton
        {
            Text = L("界面", "UI"),
            Width = 40,
            Height = 18,
            IsEnabled = insert.HasEditor,
        };
        editor.Click += insert.OpenEditor;

        var remove = new ChromeButton { Text = "✕", Width = 18, Height = 18, HoverColor = Palette.CloseHover };
        remove.Click += () => mChannel.RemoveInsert(insert);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*,46,40,18"), Height = MixerMetrics.PopupRowHeight };
        Grid.SetColumn(order, 0);
        Grid.SetColumn(nameRow, 1);
        Grid.SetColumn(bypass, 2);
        Grid.SetColumn(editor, 3);
        Grid.SetColumn(remove, 4);
        row.Children.Add(order);
        row.Children.Add(nameRow);
        row.Children.Add(bypass);
        row.Children.Add(editor);
        row.Children.Add(remove);
        return row;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        mChannel.RoutingChanged -= Rebuild;
        foreach (var insert in mChannel.Inserts)
            insert.Changed -= Rebuild;
    }

    string L(string zh, string en)
        => mHost.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
}
