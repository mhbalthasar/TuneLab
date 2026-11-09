using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.GUI;
using TuneLab.Utils;
using Button = TuneLab.GUI.Components.Button;
using Slider = TuneLab.GUI.Components.Slider;
using TuneLab.ExperimentalPatch.Patches.Mixer.Controls;

namespace TuneLab.ExperimentalPatch.Patches.Mixer;

// 插入效果器的插件扫描设置页。
//
// 【为什么是宿主设置窗里的一页】它本来就是"设置"，跟界面语言、音频设备是一类东西；
// 早先散落在「实验功能」菜单里，结果是没人找得到（菜单里还因此多出一条同名项）。
// 宿主为此开了一个贡献锚点（EP-MIXER-16：IAnchorTable.AddSettingsTab）。
//
// 【页面结构】尽量薄：
//   ① 「效果器属性」面板：一行插件缓冲（毫秒）——本页唯一的音频参数；
//   ② 每种格式（VST3 / VST2 / CLAP / JSFX）一个面板：路径清单 + 「+ 添加路径」；
//   ③ 底部一个「立即扫描」+ 进度 + 状态。
// 控件一律用宿主的 TuneLab.GUI（Button / Slider / Style），不另造一套皮肤。
public sealed class InsertPluginPathsPage : UserControl
{
    const string Vst3Format = "VST3";
    const string Vst2Format = "VST2";
    const string ClapFormat = "CLAP";
    const string JsfxFormat = "JSFX";

    readonly IHostServices mHost;
    readonly ObservableCollection<PathEntry> mVst3 = [];
    readonly ObservableCollection<PathEntry> mVst2 = [];
    readonly ObservableCollection<PathEntry> mClap = [];
    readonly ObservableCollection<PathEntry> mJsfx = [];
    readonly TextBlock mStatus = new();
    readonly ScanProgressBar mProgress = new();
    Button mScan = new();
    bool mScanning;

    public InsertPluginPathsPage(IHostServices host)
    {
        mHost = host;

        Content = BuildContent();
        Reload();
    }

    Control BuildContent()
    {
        var body = new StackPanel { Spacing = 18 };
        body.Children.Add(BuildPanel(L("效果器属性", "Effect properties"), BuildBufferRow()));
        body.Children.Add(BuildSection(L("VST3 路径", "VST3 paths"), Vst3Format, mVst3));
        body.Children.Add(BuildSection(L("VST2 路径", "VST2 paths"), Vst2Format, mVst2));
        body.Children.Add(BuildSection(L("CLAP 路径", "CLAP paths"), ClapFormat, mClap));
        body.Children.Add(BuildSection(L("JSFX 路径", "JSFX paths"), JsfxFormat, mJsfx));

        var scroll = new ScrollViewer
        {
            Content = body,
            Padding = new Thickness(14, 14, 20, 14),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        mProgress.Margin = new Thickness(14, 8, 14, 0);
        mProgress.IsVisible = false;

        mStatus.FontSize = 10.5;
        mStatus.Foreground = Style.TEXT_LABEL.ToBrush();
        mStatus.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        mStatus.Margin = new Thickness(10, 0, 0, 0);
        mStatus.TextTrimming = TextTrimming.CharacterEllipsis;

        mScan = BuildTextButton(L("立即扫描", "Scan now"), min_width: 84, height: 24);
        mScan.Clicked += () => Scan(mScan);

        var footerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(mScan, 0);
        Grid.SetColumn(mStatus, 1);
        footerRow.Children.Add(mScan);
        footerRow.Children.Add(mStatus);

        var footer = new Border
        {
            Background = Style.BACK.ToBrush(),
            BorderBrush = Style.LINE.ToBrush(),
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

        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(scroll, 0);
        Grid.SetRow(footer, 1);
        grid.Children.Add(scroll);
        grid.Children.Add(footer);

        return grid;
    }

    // 插件缓冲（毫秒）：路径决定能扫到什么，它决定扫到的东西能不能稳定地跑。
    // 一行完事：标签 + 滑块 + 当前值，不写说明（悬停细节交给 README / 日志）。
    Control BuildBufferRow()
    {
        var settings = mHost.InsertPlugins;
        int initial = settings?.BufferMs ?? 50;

        var slider = new Slider { Width = 180, Height = 20, IsInteger = true };
        slider.SetRange(10, 200);
        slider.DefaultValue = initial;
        slider.Value = initial;
        slider.IsEnabled = settings != null;

        var value = new TextBlock
        {
            Text = $"{initial} ms",
            FontSize = 11,
            Foreground = Style.TEXT_NORMAL.ToBrush(),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            MinWidth = 52,
        };

        slider.ValueChanged.Subscribe(() =>
        {
            var target = mHost.InsertPlugins;
            if (target == null)
                return;

            target.BufferMs = slider.IntergerValue;

            // 回读一次：宿主可能夹到范围内（例如采样率很高时），让界面显示实际生效值。
            int actual = target.BufferMs;
            value.Text = $"{actual} ms";

            if (Math.Abs(slider.Value - actual) > 0.5)
                slider.Value = actual;   // 避免递归：只在确实需要纠正时回写。
        });

        var right = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        right.Children.Add(slider);
        right.Children.Add(value);

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 0), LastChildFill = true };
        row.AddDock(right, Dock.Right);
        row.AddDock(new TextBlock
        {
            Text = L("插件缓冲:", "Plugin buffer:"),
            FontSize = 11,
            Foreground = Style.TEXT_NORMAL.ToBrush(),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });

        return row;
    }

    // 所有面板的统一画法：粗体标题 + 缩进一级的内容。缓冲行和四个格式路径区
    // 用同一套骨架，页面上每一块的层级才对得上（缓冲行以前裸着，看着像页首说明）。
    Control BuildPanel(string title, Control content)
    {
        var header = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Style.TEXT_LIGHT.ToBrush(),
        };

        var body = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        body.Children.Add(content);

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(body);

        return panel;
    }

    // 一个格式的面板：标题 + 路径行 + 「添加路径」。
    Control BuildSection(string title, string format, ObservableCollection<PathEntry> paths)
    {
        // 行直接手工堆进 StackPanel，不用 ItemsControl/DataTemplate：这里是"清单"而不是"缓存"，
        // 几十条的量级，整体重建最省心。（为什么"重建"而不是增量增删：见 git 历史——
        // 增量删行曾经靠 Tag 匹配失败而漏删，整体重建没有这个类别的问题。）
        var list = new StackPanel();

        void RebuildRows()
        {
            list.Children.Clear();

            foreach (var entry in paths.ToArray())
            {
                var captured = entry;
                list.Children.Add(BuildPathRow(captured, () =>
                {
                    // 删一条路径立刻落盘：否则界面说删了、扫描仍按旧的清单走
                    // （扫描读的是文件里那份，见 Scan 的注释）。
                    paths.Remove(captured);
                    Save(format, paths);
                }));
            }
        }

        RebuildRows();
        paths.CollectionChanged += (_, _) => RebuildRows();

        var add = BuildTextButton(L("+ 添加路径", "+ Add path"), min_width: 88, height: 24);
        add.Tag = "add-path";   // 供回归测试识别（渲染树测试按它数按钮）
        add.Clicked += () => Browse(format, paths);

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(list);
        body.Children.Add(add);

        return BuildPanel(title, body);
    }

    // 宿主样式的小按钮（AboutDialog / UpdateDialog 同款画法）。
    static Button BuildTextButton(string text, double min_width, double height)
    {
        var button = new Button { MinWidth = min_width, Height = height };
        button.AddContent(new()
        {
            Item = new BorderItem { CornerRadius = 4 },
            ColorSet = new() { Color = Style.BUTTON_NORMAL, HoveredColor = Style.BUTTON_NORMAL_HOVER },
        });
        button.AddContent(new() { Item = new TextItem { Text = text, FontSize = 11 }, ColorSet = new() { Color = Style.LIGHT_WHITE } });
        return button;
    }

    // 一行：路径 + ✕。路径不存在时用警示色标出来（用户打错字 / 卸载了插件是常态）。
    // ✕ 只从集合里去掉那条 —— 行本身由 RebuildRows 收拾（别再自己动视觉树，早先那样做只会漏行）。
    static Control BuildPathRow(PathEntry entry, Action onRemove)
    {
        var text = new TextBlock
        {
            Text = entry.Path,
            FontSize = 10.5,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = (entry.Exists ? Style.TEXT_NORMAL : Style.AMP_WARN).ToBrush(),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var remove = new Button { Width = 18, Height = 18 };
        remove.AddContent(new() { Item = new TextItem { Text = "✕", FontSize = 10 }, ColorSet = new() { Color = Style.LIGHT_WHITE, HoveredColor = Style.CLOSE_HOVER } });
        remove.Clicked += onRemove;

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

        mClap.Clear();
        foreach (string path in settings.Paths.TryGetValue(ClapFormat, out var clap) ? clap : [])
            mClap.Add(new PathEntry(path));

        mJsfx.Clear();
        foreach (string path in settings.Paths.TryGetValue(JsfxFormat, out var jsfx) ? jsfx : [])
            mJsfx.Add(new PathEntry(path));

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
                ? (format == Vst3Format ? @"C:\Program Files\Common Files\VST3"
                    : format == ClapFormat ? @"C:\Program Files\Common Files\CLAP"
                    : format == JsfxFormat ? @"%APPDATA%\REAPER\Effects"
                    : @"C:\Program Files\VstPlugins")
                : (format == Vst3Format ? "/usr/lib/vst3"
                    : format == ClapFormat ? "/usr/lib/clap"
                    : format == JsfxFormat ? "~/.config/REAPER/Effects"
                    : "/usr/lib/vst"),
            FontSize = 10.5,
            Width = 380,
        };

        var ok = BuildTextButton(L("添加", "Add"), min_width: 60, height: 24);

        var dialog = new Window
        {
            Title = L("添加路径", "Add path"),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            Background = Style.BACK.ToBrush(),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                BorderBrush = Style.LINE.ToBrush(),
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
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { ok },
                        },
                    },
                },
            },
        };

        ok.Clicked += () =>
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
    async void Scan(Button button)
    {
        if (mScanning)
            return;

        // 先把四个清单落盘：扫描读的是文件里那份，内存里的改动不写就不会生效。
        Save(Vst3Format, mVst3);
        Save(Vst2Format, mVst2);
        Save(ClapFormat, mClap);
        Save(JsfxFormat, mJsfx);

        var settings = mHost.InsertPlugins;
        if (settings == null)
            return;

        mScanning = true;
        button.IsEnabled = false;
        mScan.IsEnabled = false;
        mProgress.Value = 0;
        mProgress.IsVisible = true;
        mStatus.Text = L("正在扫描…", "Scanning…");

        // Progress<T> 在**构造它的线程**（这里是 UI 线程）上回调，所以下面直接碰控件是安全的。
        var progress = new Progress<InsertScanProgress>(p =>
        {
            mProgress.Value = p.TotalDirectories <= 0 ? 0 : (double)p.DoneDirectories / p.TotalDirectories;

            mStatus.Text = L(
                p.Detail == null
                    ? $"正在扫描 {p.DoneDirectories + 1}/{p.TotalDirectories}：{p.Directory}"
                    : $"正在扫描 {p.DoneDirectories + 1}/{p.TotalDirectories}：{p.Directory} — 检查 {p.Detail}",
                p.Detail == null
                    ? $"Scanning {p.DoneDirectories + 1}/{p.TotalDirectories}: {p.Directory}"
                    : $"Scanning {p.DoneDirectories + 1}/{p.TotalDirectories}: {p.Directory} — checking {p.Detail}");
        });

        try
        {
            // 完整扫描口径（把 VST3 的厂商/版本问一遍）；原来的"完整扫描"按钮调的也是它，
            // 两个按钮从诞生起就是同一件事，按用户反馈合并成一个。
            var entries = await Task.Run(() => settings.ScanDeep(progress));

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
            mScan.IsEnabled = true;
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
