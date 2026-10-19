using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.Patches.Mixer.Controls;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer;

// 插入效果的插件路径设置窗。
//
// 【为什么它自成一体，不塞进宿主的设置窗】宿主的 tab 列表是硬编码的（SettingsWindow 构造里的
// tabPages 数组），往里加一页 = 改上游文件 = 每次都冲突。而这一页**只服务混音台的插入效果**，
// 属于补丁的私有设置。所以从这里开一个独立窗口，入口放在「实验功能」菜单里。
//
// 【这里只有三件事】：
//   ① 路径清单（VST2 / VST3 各一份）：增、删、浏览。
//   ② 「立即扫描」：扫一遍 + 结果写进缓存。
//   ③ 显示上次扫描时间与扫描到的数量 —— 让"缓存是不是新鲜"看得见。
public sealed class InsertPluginPathsWindow : Window
{
    readonly IHostServices mHost;
    readonly ObservableCollection<PathEntry> mVst3 = [];
    readonly TextBlock mStatus;

    public InsertPluginPathsWindow(IHostServices host)
    {
        mHost = host;

        Title = L("插件扫描路径", "Plugin scan paths");
        Width = 620;
        Height = 480;
        MinWidth = 460;
        MinHeight = 360;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Background = Palette.Brush(Palette.Back);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Content = BuildContent();

        Reload();
    }

    Control BuildContent()
    {
        var close = new ChromeButton { Text = "✕", Width = 30, Height = 20, HoverColor = Palette.CloseHover };
        close.Click += () => Close();

        var title = new TextBlock
        {
            Text = L("插件扫描路径", "Plugin scan paths"),
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.Brush(Palette.TextLight),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
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

        var vst3Section = BuildSection(
            L("VST3 路径", "VST3 paths"),
            L("进程内加载，延迟最低。", "Loaded in process — lowest latency."),
            mVst3,
            () => Browse(mVst3));

        var scan = new StripButton
        {
            Text = L("立即扫描", "Scan now"),
            Width = 96,
            Height = 24,
        };
        scan.Click += () => Scan();

        mStatus.FontSize = 10.5;
        mStatus.Foreground = Palette.Brush(Palette.TextLabel);
        mStatus.VerticalAlignment = VerticalAlignment.Center;
        mStatus.Margin = new Thickness(10, 0, 0, 0);
        mStatus.TextTrimming = TextTrimming.CharacterEllipsis;

        var footerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(scan, 0);
        Grid.SetColumn(mStatus, 1);
        footerRow.Children.Add(scan);
        footerRow.Children.Add(mStatus);

        var footer = new Border
        {
            Height = 40,
            Background = Palette.Brush(Palette.Interface),
            BorderBrush = Palette.Brush(Palette.Line, 0.4),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footerRow,
        };

        var body = new ScrollViewer
        {
            Content = new StackPanel { Spacing = 14, Children = { vst3Section } },
            Padding = new Thickness(14),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
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

    Control BuildSection(string title, string description, ObservableCollection<PathEntry> paths, Action browse)
    {
        var header = new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.Brush(Palette.TextLight),
        };

        var hint = new TextBlock
        {
            Text = description,
            FontSize = 10,
            Foreground = Palette.Brush(Palette.TextLabel, 0.8),
            Margin = new Thickness(0, 1, 0, 0),
        };

        var add = new ChromeButton { Text = L("+ 添加路径", "+ Add path"), Width = 84, Height = 20 };
        add.Click += () => browse();

        // 手工往 StackPanel 里加行，不用 DataTemplate：我们的行要能"即时增删"，
        // 而 ItemsControl 的回收语义在这里只会添乱（删一行时它可能还在渲染上一个）。
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        PathEntry[] snapshot = mVst3.ToArray();
        foreach (var entry in snapshot)
            list.Children.Add(BuildPathRow(entry, mVst3, list));

        // 之后增删跟着同步：这是"清单"而不是"缓存"，UI 就是它。
        mVst3.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (PathEntry added in e.NewItems)
                    list.Children.Add(BuildPathRow(added, mVst3, list));

            if (e.OldItems == null)
                return;

            // O(n) 找到那一行删掉。路径清单是十进制的规模，不值得上字典。
            foreach (PathEntry removed in e.OldItems)
            {
                for (int i = list.Children.Count - 1; i >= 0; i--)
                {
                    if (list.Children[i] is not Control { Tag: PathEntry tag } || !ReferenceEquals(tag, removed))
                        continue;

                    list.Children.RemoveAt(i);
                    break;
                }
            }
        };

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(hint);
        body.Children.Add(list);
        body.Children.Add(add);

        return new StackPanel { Children = { body } };
    }

    // 一行：路径 + ✕。路径不存在时用警示色标出来（用户打错字是常态）。
    static Control BuildPathRow(PathEntry entry, ObservableCollection<PathEntry> owner, Panel parent)
    {
        var text = new TextBlock
        {
            Text = entry.Path,
            FontSize = 10.5,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = Palette.Brush(entry.Exists ? Palette.TextNormal : Palette.CloseHover),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Tag = entry,
        };

        var remove = new ChromeButton { Text = "✕", Width = 18, Height = 18, HoverColor = Palette.CloseHover };
        remove.Click += () =>
        {
            owner.Remove(entry);
            parent.Children.Remove(text);
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,18"), Height = 22 };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(remove, 1);
        row.Children.Add(text);
        row.Children.Add(remove);

        return row;
    }

    void Reload()
    {
        var settings = mHost.InsertPlugins;
        if (settings == null)
        {
            mStatus.Text = L("宿主尚未接入插件扫描", "The host has no plugin scanner");
            return;
        }

        mVst3.Clear();
        foreach (string path in settings.Paths.TryGetValue("VST3", out var vst3) ? vst3 : [])
            mVst3.Add(new PathEntry(path));

        mStatus.Text = settings.LastScanText == null
            ? L("尚未扫描", "Not scanned yet")
            : L($"上次扫描：{settings.LastScanText}", $"Last scan: {settings.LastScanText}");
    }

    void Save()
    {
        mHost.InsertPlugins?.SetPaths("VST3", mVst3.Select(p => p.Path).ToList());
    }

    void Browse(ObservableCollection<PathEntry> paths)
    {
        // 补丁层没有宿主提供的文件夹选择服务（IHostServices 里没有这个能力）。
        // 所以这里用一个可编辑的输入框让用户直接粘贴路径 —— 比"给宿主新开一个锚点"轻。
        var input = new TextBox
        {
            Watermark = OperatingSystem.IsWindows() ? @"C:\Program Files\Common Files\VST3" : "/usr/lib/vst3",
            FontSize = 10.5,
            Background = Palette.Brush(Palette.Back),
            Foreground = Palette.Brush(Palette.TextLight),
            BorderBrush = Palette.Brush(Palette.Line),
            Width = 380,
        };

        var ok = new StripButton { Text = L("添加", "Add"), Width = 60, Height = 22 };

        var dialog = new Window
        {
            Title = L("添加路径", "Add path"),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            Background = Palette.Brush(Palette.Back),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                BorderBrush = Palette.Brush(Palette.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        input,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { ok },
                        },
                    },
                },
            },
        };

        ok.Click += () =>
        {
            string path = input.Text?.Trim() ?? string.Empty;

            if (path.Length > 0 && !paths.Any(p => p.Path == path))
            {
                paths.Add(new PathEntry(path));
                Save();
            }

            dialog.Close();
        };

        if (Owner != null)
            dialog.ShowDialog(this);
        else
            dialog.Show();
    }

    void Scan()
    {
        Save();

        var settings = mHost.InsertPlugins;
        if (settings == null)
            return;

        var entries = settings.Scan();

        mStatus.Text = L($"已扫描：{entries.Count} 个插件",
            $"Scanned: {entries.Count} plugin(s)");
    }

    sealed class PathEntry(string path)
    {
        public string Path { get; } = path;
        public bool Exists => Directory.Exists(Path);
    }

    string L(string zh, string en)
        => mHost.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
}


