using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.Patches.Mixer.Controls;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer;

// 插入效果的插件扫描设置页。
//
// 【为什么是宿主设置窗里的一页】它本来就是"设置"，跟界面语言、音频设备是一类东西；
// 早先散落在「实验功能」菜单里，结果是没人找得到（菜单里还因此多出一条同名项）。
// 宿主为此开了一个贡献锚点（EP-MIXER-16：IAnchorTable.AddSettingsTab），
// 页排在宿主自己的 tab 之后 —— 没有补丁时设置窗与上游逐项一致。
//
// 【这里只有三件事】：
//   ① 路径清单（VST2 / VST3 各一份）：增（文件夹选择器）、删。
//   ② 「立即扫描」：扫一遍 + 结果写进缓存（混音台"添加效果"菜单读的就是这份缓存）。
//   ③ 显示上次扫描时间与扫描到的数量 —— 让"缓存是不是新鲜"看得见。
public sealed class InsertPluginPathsPage : UserControl
{
    const string Vst3Format = "VST3";
    const string Vst2Format = "VST2";

    readonly IHostServices mHost;
    readonly ObservableCollection<PathEntry> mVst3 = [];
    readonly ObservableCollection<PathEntry> mVst2 = [];
    readonly TextBlock mStatus = new();
    readonly ScanProgressBar mProgress = new();
    bool mScanning;

    public InsertPluginPathsPage(IHostServices host)
    {
        mHost = host;

        Content = BuildContent();
        Reload();
    }

    Control BuildContent()
    {
        var intro = new TextBlock
        {
            Text = L(
                "混音台的插入效果从这里找插件。改完路径点「立即扫描」，扫描结果会缓存下来供「添加效果」菜单使用。",
                "The mixer finds insert plugins here. After editing paths, click Scan now — results are cached for the Add effect menu."),
            FontSize = 10.5,
            Foreground = Palette.Brush(Palette.TextLabel, 0.9),
            TextWrapping = TextWrapping.Wrap,
        };

        var vst3Section = BuildSection(
            L("VST3 路径", "VST3 paths"),
            L("进程内加载。", "Loaded in process."),
            Vst3Format,
            mVst3);

        var vst2Section = BuildSection(
            L("VST2 路径", "VST2 paths"),
            L("经桥进程加载（插件崩溃不会带走 TuneLab），32 位与 64 位插件都支持。",
              "Loaded through a bridge process (a plugin crash cannot take down TuneLab); both 32- and 64-bit plugins work."),
            Vst2Format,
            mVst2);

        var scan = new ChromeButton
        {
            // 与「+ 添加路径」同一个控件、同一档尺寸：同一页里两个动作按钮长得不一样，
            // 是在暗示它们不是一类东西（而它们正是同一类：一个本机设置的写操作）。
            Text = L("立即扫描", "Scan now"),
            Width = 84,
            Height = 20,
        };
        scan.Click += () => Scan(scan);

        mProgress.Margin = new Thickness(14, 8, 14, 0);
        mProgress.IsVisible = false;

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
            Background = Palette.Brush(Palette.Interface),
            BorderBrush = Palette.Brush(Palette.Line, 0.4),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Children =
                {
                    mProgress,
                    new Border { Padding = new Thickness(14, 8, 14, 8), Child = footerRow },
                },
            },
        };

        var body = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 18,
                Children = { intro, vst3Section, vst2Section },
            },
            Padding = new Thickness(14),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(body, 0);
        Grid.SetRow(footer, 1);
        grid.Children.Add(body);
        grid.Children.Add(footer);

        return grid;
    }

    // 一段格式的清单：标题 + 说明 + 路径行 + 「添加路径」。
    Control BuildSection(string title, string description, string format, ObservableCollection<PathEntry> paths)
    {
        var header = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.Brush(Palette.TextLight),
        };

        var hint = new TextBlock
        {
            Text = description,
            FontSize = 10,
            Foreground = Palette.Brush(Palette.TextLabel, 0.8),
            Margin = new Thickness(0, 1, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var add = new ChromeButton { Text = L("+ 添加路径", "+ Add path"), Width = 84, Height = 20 };
        add.Click += () => Browse(format, paths);

        // 手工往 StackPanel 里加行，不用 DataTemplate：我们的行要能"即时增删"，
        // 而 ItemsControl 的回收语义在这里只会添乱（删一行时它可能还在渲染上一个）。
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        foreach (var entry in paths.ToArray())
            list.Children.Add(BuildPathRow(entry, paths, list));

        // 之后增删跟着同步：这是"清单"而不是"缓存"，UI 就是它。
        paths.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (PathEntry added in e.NewItems)
                    list.Children.Add(BuildPathRow(added, paths, list));

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

        return body;
    }

    // 一行：路径 + ✕。路径不存在时用警示色标出来（用户打错字 / 卸载了插件是常态）。
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
        foreach (string path in settings.Paths.TryGetValue(Vst3Format, out var vst3) ? vst3 : [])
            mVst3.Add(new PathEntry(path));

        mVst2.Clear();
        foreach (string path in settings.Paths.TryGetValue(Vst2Format, out var vst2) ? vst2 : [])
            mVst2.Add(new PathEntry(path));

        mStatus.Text = settings.LastScanText == null
            ? L("尚未扫描", "Not scanned yet")
            : L($"上次扫描：{settings.LastScanText}", $"Last scan: {settings.LastScanText}");
    }

    void Save(string format, ObservableCollection<PathEntry> paths)
        => mHost.InsertPlugins?.SetPaths(format, paths.Select(p => p.Path).ToList());

    // 真·文件夹选择器；拿不到 TopLevel（理论上不会）或系统不支持时，退回手输。
    async void Browse(string format, ObservableCollection<PathEntry> paths)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;

        if (storage is { CanPickFolder: true })
        {
            try
            {
                var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = L("添加插件目录", "Add plugin folder"),
                    AllowMultiple = true,
                });

                foreach (var folder in folders)
                {
                    string? path = folder.TryGetLocalPath();
                    if (!string.IsNullOrWhiteSpace(path))
                        AddPath(format, paths, path);
                }

                return;
            }
            catch (Exception ex)
            {
                mHost.LogWarning("plugin folder picker failed, falling back to manual input: " + ex.Message);
            }
        }

        ShowManualInput(format, paths);
    }

    void ShowManualInput(string format, ObservableCollection<PathEntry> paths)
    {
        var input = new TextBox
        {
            Watermark = OperatingSystem.IsWindows()
                ? (format == Vst3Format ? @"C:\Program Files\Common Files\VST3" : @"C:\Program Files\VstPlugins")
                : (format == Vst3Format ? "/usr/lib/vst3" : "/usr/lib/vst"),
            FontSize = 10.5,
            Width = 380,
        };

        var ok = new ChromeButton { Text = L("添加", "Add"), Width = 60, Height = 20 };

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
            AddPath(format, paths, input.Text?.Trim() ?? string.Empty);
            dialog.Close();
        };

        if (TopLevel.GetTopLevel(this) is Window owner)
            dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    void AddPath(string format, ObservableCollection<PathEntry> paths, string path)
    {
        if (path.Length == 0 || paths.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;

        paths.Add(new PathEntry(path));
        Save(format, paths);
    }

    // 「立即扫描」：**扔到后台线程**，边扫边把进度画出来，不卡界面。
    //
    // 【为什么必须异步】扫几十个目录要几秒到几十秒；同步扫的话窗口直接冻住 ——
    // 连"关掉它"都点不动，用户只能杀进程。这里 UI 线程只做三件事：置灰按钮、
    // 收进度、写结果；真正走路的是后台线程。
    async void Scan(ChromeButton button)
    {
        if (mScanning)
            return;

        // 先把两个清单落盘：扫描读的是文件里那份，内存里的改动不写就不会生效。
        Save(Vst3Format, mVst3);
        Save(Vst2Format, mVst2);

        var settings = mHost.InsertPlugins;
        if (settings == null)
            return;

        mScanning = true;
        button.IsEnabled = false;
        mProgress.Value = 0;
        mProgress.IsVisible = true;
        mStatus.Text = L("正在扫描…", "Scanning…");

        // Progress<T> 在**构造它的线程**（这里是 UI 线程）上回调，所以下面直接碰控件是安全的。
        var progress = new Progress<InsertScanProgress>(p =>
        {
            mProgress.Value = p.TotalDirectories <= 0 ? 0 : (double)p.DoneDirectories / p.TotalDirectories;

            mStatus.Text = L(
                $"正在扫描 {p.DoneDirectories + 1}/{p.TotalDirectories}：{p.Directory}",
                $"Scanning {p.DoneDirectories + 1}/{p.TotalDirectories}: {p.Directory}");
        });

        try
        {
            var entries = await Task.Run(() => settings.Scan(progress));

            mProgress.Value = 1;

            // 重读一次清单：扫描期间可能被别的窗口改过（也可能就是我们刚写的）。
            Reload();

            string when = settings.LastScanText ?? string.Empty;
            mStatus.Text = L($"已扫描：{entries.Count} 个插件（{when}）",
                $"Scanned: {entries.Count} plugin(s) ({when})");
        }
        catch (Exception ex)
        {
            // 失败就别再 Reload 了 —— 那会把这句话盖掉，用户只看得到"上次扫描时间"。
            mStatus.Text = L("扫描失败，详见日志", "Scan failed - see the log");
            mHost.LogError("plugin scan failed", ex);
        }
        finally
        {
            mScanning = false;
            button.IsEnabled = true;
            mProgress.IsVisible = false;
        }
    }

    sealed class PathEntry(string path)
    {
        public string Path { get; } = path;
        public bool Exists => Directory.Exists(Path);
    }

    string L(string zh, string en)
        => mHost.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
}
