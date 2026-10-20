using System;
using System.Collections.Generic;
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

        // 视口高度必须是**行距的整数倍**：早先写死 260，而 8 行之后剩下 4px，
        // 第 9 行只露出薄薄一绺、下半截被裁掉 —— 看起来像"列表最后一行坏了"。
        // 现在按可见行数算：8 行 + 7 个间距 + 上下内边距，一行都不半露。
        const int visibleRows = 8;
        const double verticalPadding = 8;

        var body = new ScrollViewer
        {
            Content = new Panel { Children = { mRows, mEmpty } },
            MaxHeight = visibleRows * MixerMetrics.PopupRowHeight
                        + (visibleRows - 1) * mRows.Spacing
                        + verticalPadding * 2,
            Padding = new Thickness(MixerMetrics.PopupPadding, verticalPadding),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
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

    // 点"+ 添加效果"：按**格式 → 厂商 → 插件**三级列出缓存里的插件（不现扫，缓存见 InsertPluginSettings）。
    //
    // 为什么分三级：一个装了插件的机器动辄几百个，平铺一列没法找；而 VST2 / VST3 的厂商
    // （Voxengo / Waves / …）本来就是人记插件的方式。空 / 全部不可用时直接说明原因，
    // 而不是给一个点了没反应的按钮。
    //
    // 厂商与"是不是乐器"都来自**扫描时的探测**（VST2 过桥加载问一句，VST3 问 native 的
    // IsInstrument，见两个 Backend 的注释）。所以这里只读缓存，不加载任何插件 ——
    // 打开菜单必须是瞬间的。
    void ShowAddMenu(Control anchor)
    {
        var plugins = mHost.InsertPlugins?.CachedPlugins ?? [];

        var menu = new ContextMenu();

        foreach (var format in mFormats)
        {
            // 乐器不进插入清单：插入点是"处理已经存在的音频"，乐器在这里没有意义
            // （它要 MIDI 输入、要自己产声），插进来只会白占一条链、听不到任何声音。
            var candidates = plugins
                .Where(p => p.Format == format.Name && !p.IsInstrument)
                .ToList();

            // 格式自己就是第一级（子菜单），不可用 / 空的时候才退化成一个禁用的叶子。
            if (!format.IsAvailable)
            {
                menu.Items.Add(DisabledItem($"{format.Name} — {format.UnavailableReason ?? "unavailable"}"));
                continue;
            }

            if (candidates.Count == 0)
            {
                menu.Items.Add(DisabledItem($"{format.Name} — " + L("没有扫描到效果器，去「设置 → 插件扫描」扫一次",
                    "no effects scanned yet (Settings → Plugin scan)")));
                continue;
            }

            var formatItem = new MenuItem { Header = format.Name };

            foreach (var vendor in GroupByVendor(candidates))
            {
                var vendorItem = new MenuItem { Header = vendor.Key };

                foreach (var plugin in vendor.Value)
                {
                    var item = new MenuItem { Header = plugin.Name };
                    item.IsEnabled = plugin.IsAvailable;

                    if (!plugin.IsAvailable)
                        item.Header = plugin.Name + "   — " + (plugin.UnavailableReason ?? "");

                    var capturedFormat = format;
                    var capturedPlugin = plugin;
                    item.Click += (_, _) => AddInsert(capturedFormat, capturedPlugin);

                    vendorItem.Items.Add(item);
                }

                formatItem.Items.Add(vendorItem);
            }

            menu.Items.Add(formatItem);
        }

        menu.Open(anchor);
    }

    // 厂商分组：按厂商名排序（人不记顺序，只记名字），空厂商归到一个显式的占位名，
    // 而不是混在里面看不出来"为什么这些没有厂商"。
    IEnumerable<KeyValuePair<string, List<InsertPluginEntry>>> GroupByVendor(List<InsertPluginEntry> plugins)
    {
        string unknown = L("（未知厂商）", "(unknown vendor)");

        return plugins
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Vendor) ? unknown : p.Vendor)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new KeyValuePair<string, List<InsertPluginEntry>>(
                g.Key,
                g.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList()));
    }

    static MenuItem DisabledItem(string header) => new() { Header = header, IsEnabled = false };

    void AddInsert(IMixerInsertFormat format, InsertPluginEntry plugin)
    {
        var insert = mChannel.AddInsert(format);

        // 插件的定位信息（路径 / classId）写回数据槽位 —— 宿主据此建实例。
        if (insert is IInsertPluginTarget target)
            target.SetPlugin(plugin.PluginId, plugin.Name, plugin.Locator);

        // 立刻重建行。RoutingChanged 只在**成员**增删时响，而这里加槽位那次响完之后
        // 名字还是空的（SetPlugin 写的是内容，不是成员）—— 不重建的话用户刚加完只看到
        // 一个没有名字的格式标签，要把浮窗关掉再打开才正常。
        Rebuild();
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

        // 旁通要**当场**反映：只写数据槽位的话，音频图不会重建（旁通不是结构变化），
        // 用户按下去既听不出差别、按键也不亮，看起来就是"没反应"。
        // 适配器会把值写进正在跑的插件实例并立刻生效，这里再重建一遍行来更新配色。
        bypass.Click += () =>
        {
            insert.IsBypassed = !insert.IsBypassed;
            Rebuild();
        };

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
    }

    string L(string zh, string en)
        => mHost.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
}
