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
    const string ClapFormat = "CLAP";
    const string JsfxFormat = "JSFX";

    readonly IHostServices mHost;
    readonly ObservableCollection<PathEntry> mVst3 = [];
    readonly ObservableCollection<PathEntry> mVst2 = [];
    readonly ObservableCollection<PathEntry> mClap = [];
    readonly ObservableCollection<PathEntry> mJsfx = [];
    readonly TextBlock mStatus = new();
    readonly ScanProgressBar mProgress = new();
    ChromeButton mScan = new();
    ChromeButton mDeepScan = new();
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
                "混音台的插入效果从这里找插件。改完路径点「立即扫描」，扫描结果会缓存下来供「添加效果」菜单使用。"
                + "VST2 / VST3 / CLAP / JSFX 一律由独立的桥进程加载：插件崩溃或卡死只会让那个插入旁通，不会带走 TuneLab。",
                "The mixer finds insert plugins here. After editing paths, click Scan now — results are cached for the Add effect menu. "
                + "VST2, VST3, CLAP and JSFX always load in a separate bridge process: a plugin crash or hang only bypasses that insert, never takes down TuneLab."),
            FontSize = 10.5,
            Foreground = Palette.Brush(Palette.TextLabel, 0.9),
            TextWrapping = TextWrapping.Wrap,
        };

        // 缓冲时间放在最上面：它影响的是"插件能不能稳定跑"，比路径清单更常被调。
        var bufferSection = BuildBufferSection();

        var vst3Section = BuildSection(
            L("VST3 路径", "VST3 paths"),
            L("经桥进程加载，插件崩溃不会带走 TuneLab。",
              "Loaded through a bridge process; a plugin crash cannot take down TuneLab."),
            Vst3Format,
            mVst3);

        var isolationSection = BuildIsolationSection();

        var vst2Section = BuildSection(
            L("VST2 路径", "VST2 paths"),
            L("经桥进程加载（插件崩溃不会带走 TuneLab），32 位与 64 位插件都支持。",
              "Loaded through a bridge process (a plugin crash cannot take down TuneLab); both 32- and 64-bit plugins work."),
            Vst2Format,
            mVst2);

        var clapSection = BuildSection(
            L("CLAP 路径", "CLAP paths"),
            L("经桥进程加载。扫描要逐个加载 .clap 问描述（CLAP 没有可静态读取的元数据），比另外两种格式慢。"
              + "插件界面（编辑器）尚未开放，插上后先显示参数面板。",
              "Loaded through a bridge process. Scanning loads every .clap to ask for its description "
              + "(CLAP has no statically readable metadata), so it is slower than the other formats. "
              + "Plugin editors are not available yet: inserted CLAP plugins show the parameter panel."),
            ClapFormat,
            mClap);

        var jsfxSection = BuildSection(
            L("JSFX 路径", "JSFX paths"),
            L("扫描只读 .jsfx 文本头，不执行 EEL2；加载与运行由桥进程里的 ysfx 完成。"
              + "默认包含 TuneLab 的 Effects 目录与 REAPER 的 Effects 目录。",
              "Scanning only reads the .jsfx text header; EEL2 never runs in TuneLab. Loading and processing "
              + "happen in the bridge process through ysfx. Defaults include TuneLab's Effects folder and "
              + "REAPER's Effects folder."),
            JsfxFormat,
            mJsfx);

        var scan = new ChromeButton
        {
            // 与「+ 添加路径」同一个控件、同一档尺寸：同一页里两个动作按钮长得不一样，
            // 是在暗示它们不是一类东西（而它们正是同一类：一个本机设置的写操作）。
            Text = L("立即扫描", "Scan now"),
            Width = 84,
            Height = 20,
        };
        mScan = scan;
        scan.Click += () => Scan(scan);

        var deep = new ChromeButton
        {
            Text = L("完整扫描", "Deep scan"),
            Width = 84,
            Height = 20,
            Margin = new Thickness(6, 0, 0, 0),
        };
        mDeepScan = deep;

        // 悬停说明：这个按钮比"立即扫描"慢一个量级，得让用户知道为什么、以及什么时候该用它。
        ToolTip.SetTip(deep,
            L("把每个 VST3 的厂商和版本问一遍。只问描述，不实例化插件，安全但慢。\n" +
              "普通扫描读的是二进制里的类别标记，多半没有厂商。",
              "Asks every VST3 for its vendor and version. Describes only, never instantiates, safe but slow.\n" +
              "The quick scan reads binary markers and usually has no vendor."));

        mProgress.Margin = new Thickness(14, 8, 14, 0);
        mProgress.IsVisible = false;

        mStatus.FontSize = 10.5;
        mStatus.Foreground = Palette.Brush(Palette.TextLabel);
        mStatus.VerticalAlignment = VerticalAlignment.Center;
        mStatus.Margin = new Thickness(10, 0, 0, 0);
        mStatus.TextTrimming = TextTrimming.CharacterEllipsis;

        var footerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        Grid.SetColumn(scan, 0);
        Grid.SetColumn(deep, 1);
        Grid.SetColumn(mStatus, 2);
        footerRow.Children.Add(scan);
        footerRow.Children.Add(deep);
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
                Children = { bufferSection, intro, vst3Section, isolationSection, vst2Section, clapSection, jsfxSection },
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

    // 效果器插件缓冲时间（毫秒）。这是本页唯一的"音频参数"：路径决定能扫到什么，
    // 它决定扫到的东西能不能稳定地跑。宿主按当前采样率折算成设备缓冲尺寸，并同步
    // 桥进程单块的等待上限；默认 50 ms。
    Control BuildBufferSection()
    {
        var header = new TextBlock
        {
            Text = L("效果器插件缓冲时间", "Insert plugin buffer time"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.Brush(Palette.TextLight),
        };

        var hint = new TextBlock
        {
            Text = L(
                "以毫秒计；按当前采样率折算成设备缓冲尺寸，同时作为桥进程单块的等待上限。"
                + "加大可减少卡顿，代价是延迟增加。默认 50 ms。",
                "Measured in milliseconds; converted to the device buffer size at the current sample rate, "
                + "and used as the bridge's per-block wait limit. Larger values reduce dropouts at the cost of latency. "
                + "Default 50 ms."),
            FontSize = 10,
            Foreground = Palette.Brush(Palette.TextLabel, 0.8),
            Margin = new Thickness(0, 1, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var settings = mHost.InsertPlugins;
        int initial = settings?.BufferMs ?? 50;

        var value = new TextBlock
        {
            Text = $"{initial} ms",
            FontSize = 11,
            Foreground = Palette.Brush(Palette.TextNormal),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            MinWidth = 52,
        };

        var slider = new Slider
        {
            Minimum = 10,
            Maximum = 200,
            TickFrequency = 5,
            IsSnapToTickEnabled = true,
            Value = initial,
            IsEnabled = settings != null,
            Margin = new Thickness(0, 6, 0, 0),
        };

        slider.ValueChanged += (_, _) =>
        {
            var target = mHost.InsertPlugins;
            if (target == null)
                return;

            int ms = (int)Math.Round(slider.Value);
            target.BufferMs = ms;

            // 回读一次：宿主可能夹到范围内（例如采样率很高时），让界面显示实际生效值。
            int actual = target.BufferMs;
            value.Text = $"{actual} ms";

            if (Math.Abs(slider.Value - actual) > 0.5)
            {
                // 避免递归：只在确实需要纠正时回写。
                slider.Value = actual;
            }
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(slider, 0);
        Grid.SetColumn(value, 1);
        row.Children.Add(slider);
        row.Children.Add(value);

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(hint);
        body.Children.Add(row);
        return body;
    }

    // [VST3-BRIDGE] 隔离状态（只读）。
    //
    // 【为什么没有开关了】VST3 的进程内实现已经从 patch 本体搬到桥工程 —— 宿主进程里
    // 不再有能加载插件的代码，也就不存在"关掉隔离"这个选项。这一节只回答一个问题：
    // 这台机器上桥在不在、VST3 能不能用。
    Control BuildIsolationSection()
    {
        var header = new TextBlock
        {
            Text = L("崩溃隔离", "Crash isolation"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.Brush(Palette.TextLight),
        };

        var hint = new TextBlock
        {
            Text = L(
                "VST3 / VST2 / CLAP / JSFX 一样，一律由独立的 64 位桥进程加载 —— 插件崩溃或卡死只会拖垮桥，"
                + "TuneLab 与工程不受影响（该插入会旁通）。",
                "Like VST2, VST3, CLAP and JSFX always load in a separate 64-bit bridge process — a plugin crash "
                + "or hang only takes down the bridge, not TuneLab (the insert is bypassed instead)."),
            FontSize = 10,
            Foreground = Palette.Brush(Palette.TextLabel, 0.8),
            Margin = new Thickness(0, 1, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var status = new TextBlock
        {
            FontSize = 10,
            Foreground = Palette.Brush(Palette.TextLabel, 0.8),
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        void RefreshStatus()
        {
            var settings = mHost.InsertPlugins;

            if (settings == null)
            {
                status.Text = string.Empty;
                return;
            }

            if (settings.Vst3IsolationAvailable)
            {
                status.Text = L("当前：隔离可用。", "Current: isolation is available.");
                status.Foreground = Palette.Brush(Palette.TextLabel, 0.8);
                return;
            }

            string? reason = settings.Vst3IsolationUnavailableReason;

            status.Text = string.IsNullOrEmpty(reason)
                ? L("当前：VST3 不可用 —— 这台机器上跑不了隔离宿主。",
                    "Current: VST3 is unavailable — the isolated host cannot run on this machine.")
                : L("当前：VST3 不可用 —— " + reason + "。",
                    "Current: VST3 is unavailable — " + reason + ".");
            status.Foreground = Palette.Brush(Palette.AmpWarn);
        }

        // 页面每次打开都重新算一遍：这里问的是"文件在不在"，重装桥之后不用重启就该反映出来。
        status.AttachedToVisualTree += (_, _) => RefreshStatus();

        RefreshStatus();

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(hint);
        body.Children.Add(status);
        return body;
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

        // 行直接手工堆进 StackPanel，不用 ItemsControl/DataTemplate：这里是"清单"而不是"缓存"，
        // 几十条的量级，整体重建最省心。
        //
        // 【为什么是"重建"而不是增量增删】早先是增量同步：CollectionChanged 里按 Tag 找回那一行删掉。
        // 那个写法有个致命细节 —— 行是 Grid，而 Tag 打在里面的 TextBlock 上，于是**永远匹配不上**：
        // 删不掉旧行、✕ 也只从 Grid 里摘掉一个子控件（看着像删了，其实行还在）。
        // 于是每调一次 Reload（扫描完就会调）列表就多一份重复。
        // 整体重建没有这个类别的问题：内容与集合永远一致。
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

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

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(hint);
        body.Children.Add(list);
        body.Children.Add(add);

        return body;
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
            Foreground = Palette.Brush(entry.Exists ? Palette.TextNormal : Palette.CloseHover),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var remove = new ChromeButton { Text = "✕", Width = 18, Height = 18, HoverColor = Palette.CloseHover };
        remove.Click += onRemove;

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
    async void Scan(ChromeButton button, bool deep = false)
    {
        if (mScanning)
            return;

        // 先把两个清单落盘：扫描读的是文件里那份，内存里的改动不写就不会生效。
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
        mDeepScan.IsEnabled = false;
        mProgress.Value = 0;
        mProgress.IsVisible = true;
        mStatus.Text = deep ? L("完整扫描中…（要一会儿）", "Deep scanning… (this takes a while)") : L("正在扫描…", "Scanning…");

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
            button.IsEnabled = true;
            mScan.IsEnabled = true;
            mDeepScan.IsEnabled = true;
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



