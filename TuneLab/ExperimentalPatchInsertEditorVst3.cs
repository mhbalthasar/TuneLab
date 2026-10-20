using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using TuneLab.ExperimentalPatch.InsertHost;
using HostLog = TuneLab.Foundation.Log;

namespace TuneLab.Audio;

// VST3 插件编辑器的宿主窗口：真正把插件 GUI 嵌进 Avalonia 窗口的那一层。
//
// 【插件要什么】VST3 的 IPlugView 要点进一个宿主提供的原生窗口（Windows 上就是 HWND）。
// 于是我们建一个 Avalonia Window，把它的平台句柄交给插件，再由插件在里面创建自己的
// 子窗口。这跟 REAPER / Cubase 的做法一样 —— 插件 GUI 是原生控件，不由我们自己绘制。
//
// 【尺寸】插件自己报一个首选尺寸（IPlugView::getSize）。尊重它：硬要塞进我们定的尺寸
// 会让插件画出滚动条，那比窗口大一点糟得多。用户自己还能拖。
//
// 【关闭】关窗前先调 CloseEditor：插件要在窗口销毁之前把 view 摘掉（否则它关窗时会碰到
// 已经释放的组件，实测会崩）。顺序写死在 DropEditor 里，别调换。
//
// 【"打开是空的"/"卡死"】两件事都在这里收尾：
//   · 有些插件把 GUI 挂在自己的消息队列上，不泵消息就不画 —— 定时器每 20ms 泵一次；
//   · CreateEditor 是**插件自己的代码**，可能弹模态 / 跑自己的消息循环，那会把 UI 线程
//     连同整个调音台挂住。所以它丢到后台线程等，超时就把窗口留在空白态（有提示文字）。
public sealed class Vst3EditorWindow : Window, IInsertEditorWindow
{
    readonly Vst3Insert mInsert;
    IntPtr mParent;
    TextBlock? mHint;

    // 置顶跟随混音台主窗：编辑器是"调哪个钮"的工具窗，被主编辑器盖住就得来回切窗口。
    bool mTopmost;
    Button? mPin;

    event Action? mWindowClosed;

    Vst3EditorWindow(Vst3Insert insert, string pluginName)
    {
        mInsert = insert;

        Title = pluginName;

        // 一定要给个初始尺寸：Win32 的 CreateWindowEx 不接受 0 尺寸的窗口，
        // 而插件句柄是在窗口第一次呈现时才拿得到的。
        var size = insert.EditorSize;
        Width = size?.Width > 0 ? size.Value.Width : 640;
        Height = size?.Height > 0 ? size.Value.Height : 480;

        MinWidth = 200;
        MinHeight = 120;

        Background = new SolidColorBrush(Color.FromRgb(24, 24, 26));

        // 自绘标题栏（与混音台同一策略：系统标题栏会把窗口割裂成两种风格），
        // 上面放一个「置顶」小钮 —— 与混音台窗口的那一个同一套交互。
        // 这里用原生 Button 而不是补丁层的 ChromeButton：宿主持有的是自己的 UI 约定，
        // 而 ChromeButton 住在补丁程序集里，宿主**不能**反向引用它。
        var pin = new Button
        {
            Content = "置顶",
            FontSize = 10,
            Width = 38,
            Height = 18,
            Padding = new Thickness(4, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 206)),
            BorderThickness = default,
        };
        pin.Click += (_, _) =>
        {
            mTopmost = !mTopmost;
            Topmost = mTopmost;
            mPin!.Background = mTopmost
                ? new SolidColorBrush(Color.FromRgb(72, 110, 160))
                : Brushes.Transparent;
        };

        mPin = pin;

        var close = new Button
        {
            Content = "✕",
            FontSize = 10,
            Width = 26,
            Height = 18,
            Padding = new Thickness(4, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 206)),
            BorderThickness = default,
        };
        close.Click += (_, _) => Close();

        var name = new TextBlock
        {
            Text = pluginName,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(224, 224, 228)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        buttons.Children.Add(pin);
        buttons.Children.Add(close);

        var titleBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleBar.Children.Add(name);
        titleBar.Children.Add(buttons);
        Grid.SetColumn(buttons, 1);

        // 备用提示：插件建不出编辑器（或超时）时，窗口里得有句话，
        // 而不是一个黑方块 —— 那比"点了没反应"还难排查。
        mHint = new TextBlock
        {
            Text = "加载插件界面…",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 156)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var body = new Grid { Children = { mHint } };

        var layout = new Grid { RowDefinitions = new RowDefinitions("24,*") };
        layout.Children.Add(BuildTitleBarShell(titleBar));
        layout.Children.Add(body);
        Grid.SetRow(layout.Children[0], 0);
        Grid.SetRow(layout.Children[1], 1);

        Content = layout;

        Opened += OnOpened;
        Closing += OnClosing;
        Resized += OnResized;
    }

    // 标题栏的外壳：一条比内容深的底 + 下边线，与混音台浮窗同一视觉。
    static Border BuildTitleBarShell(Grid bar)
        => new()
        {
            Height = 24,
            Background = new SolidColorBrush(Color.FromRgb(32, 32, 36)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 64)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar,
        };

    public static Vst3EditorWindow Create(Vst3Insert insert, string pluginName)
        => new(insert, pluginName);

    public bool HasLiveEditor => mInsert.HasEditor;

    void OnOpened(object? sender, EventArgs e)
    {
        IPlatformHandle? handle = TryGetPlatformHandle();
        if (handle == null)
        {
            // 拿不到句柄（非 Windows / 没渲染器）就没什么可做的了。
            // 窗口里那段提示文字留着，至少不显得"点了没反应"。
            HostLog.Warning($"insert: cannot get a window handle for the editor of '{mInsert.Name}'");
            return;
        }

        mParent = handle.Handle;

        var size = mInsert.EditorSize;
        HostLog.Info($"insert: opening the editor of '{mInsert.Name}' (hasEditor={mInsert.HasEditor}, "
                   + $"size={(size == null ? "unknown" : size.Value.Width + "x" + size.Value.Height)})");

        // OpenEditor 里调的是**插件自己的代码**：有些插件在这里弹模态框 / 跑自己的消息循环，
        // 那会把我们的 UI 线程连同整个调音台一起挂住。所以放到后台线程等它，UI 线程继续跑。
        // 副作用：窗口首次呈现时插件还没挂上，用户会看到一瞬"加载插件界面…" —— 比整窗卡死划算。
        var open = Task.Run(() => mInsert.OpenEditor(mParent));

        if (!open.Wait(EditorOpenTimeout))
        {
            // 插件在自己的 CreateEditor 里自锁了。实测的典型：BC Chorus 4 找不到它的
            // Skins/default.xml，走"请选择 skin 文件"那条路弹 GetOpenFileName 模态框 ——
            // 那个框跑在**调用 CreateEditor 的这条线程**的消息队列上，而这条线程正卡在
            // CreateEditor 里等它。窗口过程全程只收到一条 WM_PAINT，一片死寂。
            // 这不是宿主能解的（REAPER 之类是在另一条线程上跑插件消息循环，我们没有这个余量），
            // 但至少要说清楚是插件卡在自己的界面上，而不是让我们看起来像是崩了。
            HostLog.Warning($"insert: '{mInsert.Name}' deadlocked inside its own CreateEditor "
                          + $"after {EditorOpenTimeout.TotalSeconds:0.#}s (it is waiting for a window or "
                          + "message that only its own code can service — commonly a missing asset, "
                          + "e.g. a skin file the install does not contain)");

            ReplaceHint($"插件卡在自己的界面上了：{mInsert.Name}"
                      + "（通常是插件自身资源缺失，重新安装该插件一般能解决）");
            return;
        }

        if (!open.Result)
        {
            // 插件有界面却建不出来（依赖缺失 / 版本不兼容）。提示文字换成明话。
            HostLog.Warning($"insert: '{mInsert.Name}' reported HasEditor=true but CreateEditor returned false");
            ReplaceHint($"这个插件没有打开界面：{mInsert.Name}");
            return;
        }

        // CreateEditor 返回了成功，但窗口里可能什么都没有（实测 Sound Delay：返回 true、
        // 0 个子窗口、泵消息 1 秒也没有）。native 库那侧没有报错，这里照实说。
        if (mInsert.ChildWindowCount(mParent) == 0)
        {
            HostLog.Warning($"insert: '{mInsert.Name}' returned success but created no GUI "
                          + "(its framework initialised nothing into our window)");
            ReplaceHint($"插件没有画出界面：{mInsert.Name}");
            return;
        }

        // 插件报告首选尺寸（打开之后才报得准的那些插件走这条）：
        // 按它调整一次，用户之后自己拖。
        if (mInsert.EditorSize is { } opened && opened.Width > 0 && opened.Height > 0)
        {
            Width = opened.Width;
            Height = opened.Height;
        }

        StartMessagePump();
    }

    // 每 ~20ms 泵一次插件自己的消息队列。VST3 里需要这么干的插件（GUI 挂在自己线程上的）
    // 不泵就永远不画 —— 用户报"打开的窗口是空的"，根源就在这里。
    void StartMessagePump()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };

        timer.Tick += (_, _) =>
        {
            if (!IsVisible)
                return;

            mInsert.PumpEditorMessages();
        };

        timer.Start();
    }

    void ReplaceHint(string text)
    {
        if (mHint == null)
            return;

        mHint.Text = text;
        // 插件卡死/没画出界面时要说两行，所以允许换行 + 居中铺开。
        mHint.TextWrapping = TextWrapping.Wrap;
        mHint.TextAlignment = TextAlignment.Center;
        mHint.Margin = new Thickness(24);
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 必须在窗口销毁之前摘掉 view（见类注释）。
        DropEditor();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        DropEditor();
        mWindowClosed?.Invoke();
    }

    void OnResized(object? sender, WindowResizedEventArgs e)
    {
        if (mParent == IntPtr.Zero)
            return;

        mInsert.ResizeEditor((int)e.ClientSize.Width, (int)e.ClientSize.Height);
    }

    // 先摘插件 view，再让窗口消失。调用方（InsertEditorHost）在关插件实例前也会调一次 ——
    // 重复调用是安全的，CloseEditor 自带"没开过就算了"。
    public void DropEditor() => mInsert.CloseEditor();

    void IInsertEditorWindow.Activate() => Activate();

    // 显式实现：基类的 Closed 是 event EventHandler，与本接口的 event Action 签名不同，
    // 写成显式就不会被误当成"自动实现"（编译错误 CS0738 就是这么来的）。
    event Action? IInsertEditorWindow.WindowClosed
    {
        add => mWindowClosed += value;
        remove => mWindowClosed -= value;
    }

    // 等插件建 UI 的上限。到了就把窗口留在空白态（有提示文字），绝不把 UI 线程无限期押在上面。
    static readonly TimeSpan EditorOpenTimeout = TimeSpan.FromSeconds(8);
}
