using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using TuneLab.ExperimentalPatch.Abstractions;

namespace TuneLab.ExperimentalPatch.Patches.Mixer;

// 混音台的入口：往宿主的「实验功能」菜单里挂一条「混音台」。
//
// 为什么是补丁而不是宿主硬编码一项：宿主只知道"这里有个可点的条目"，条目要干什么全归补丁。
// 这样以后每个实验功能都复用同一个菜单接缝，不用各改一处宿主（DESIGN.md §5.4 / Q5）。
//
// 【窗口生命周期】三条硬规则：
//   ① **只能有一扇**：再点菜单只会把现有那扇带到前面，绝不新建；
//   ② 它是主窗口的**归属窗口**（owned）：主窗口在前台时它不会被盖住；
//   ③ 主窗口一关，它立刻跟着关 —— 否则"最后一个窗口"还在，进程不退出、音频继续跑。
[Patch(Order = 50)]
public sealed class MixerPatch : IPatch
{
    public string Id => "mixer.window";

    MixerWindow? mWindow;
    IMixerModel? mModel;
    IHostServices? mHost;
    Window? mOwner;
    Window? mInsertPathsWindow;

    public void Attach(IAnchorTable anchors)
    {
        // 模型要等编辑器就绪才取得到，所以宿主句柄在 app.ready 才落袋；菜单命令是之后才点的。
        anchors.OnAppReady(anchor => mHost = anchor.Host);
        anchors.AddCommand(new ExperimentalCommand("mixer.open", Label(anchors.Host), Open));

        // 插入效果的插件扫描路径。挂在同一个「实验功能」菜单下：它与混音台是一套功能的两半
        // （没有扫描路径就加不了效果器），但设成独立窗口而不是塞进宿主设置窗 —— 那个 tab 列表
        // 是宿主硬编码的，改它就是给上游合并添一处冲突（见 InsertPluginPathsWindow 顶部注释）。
        anchors.AddCommand(new ExperimentalCommand("mixer.insertPaths", Label(anchors.Host), OpenInsertPaths));
    }

    public void Detach()
    {
        UnsubscribeOwner();

        var window = mWindow;
        mWindow = null;
        mModel = null;
        mHost = null;

        if (mInsertPathsWindow != null)
        {
            mInsertPathsWindow.Closed -= OnInsertPathsClosed;
            mInsertPathsWindow.Close();
            mInsertPathsWindow = null;
        }

        if (window == null)
            return;

        window.Detach();
        window.Close();
    }

    static string Label(IHostServices host)
        => host.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "混音台" : "Mixer";

    static string InsertPathsLabel(IHostServices host)
        => host.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "插件扫描路径…"
            : "Plugin scan paths…";

    void OpenInsertPaths()
    {
        var host = mHost;
        if (host == null)
            return;

        // 单实例，与混音台同一套规矩（ MixerPatch 顶部注释 ①②③）。
        if (mInsertPathsWindow != null)
        {
            mInsertPathsWindow.Activate();
            return;
        }

        var window = new InsertPluginPathsWindow(host);
        window.Closed += OnInsertPathsClosed;

        mInsertPathsWindow = window;
        ShowWindow(window);
    }

    void OnInsertPathsClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
            window.Closed -= OnInsertPathsClosed;

        mInsertPathsWindow = null;
    }

    void Open()
    {
        var host = mHost;
        if (host == null)
            return;

        // ① 已经有一扇：只把它带到前面。模型是门面（MixerModelFacade），换工程时它自己会跟上，
        //    所以这里不需要因为"模型变了"而重建窗口。
        if (mWindow != null)
        {
            BringToFront(mWindow);
            return;
        }

        var model = host.CreateMixerModel();
        if (model == null)
        {
            host.LogWarning("mixer: no project is open");
            return;
        }

        var window = new MixerWindow(model, host);
        window.Closed += OnWindowClosed;   // 被真正关掉（不是隐藏）时清掉引用

        mModel = model;
        mWindow = window;
        ShowWindow(window);
    }

    void BringToFront(Window window)
    {
        // 隐藏过的（点了 ✕）重新显示；已经可见的只要激活。
        if (!window.IsVisible)
            window.Show();

        window.Activate();
    }

    // 让混音台成为主窗口的归属窗口（owned window）：
    //   · 主窗口在前台时它不会被盖住（调音台要一直看得见）；
    //   · 主窗口关闭时它跟着关 —— 进程因此能正常退出（ShutdownMode.OnLastWindowClose）。
    void ShowWindow(Window window)
    {
        var owner = MainWindow();

        if (owner == null || ReferenceEquals(owner, window))
        {
            window.Show();
            return;
        }

        if (!ReferenceEquals(mOwner, owner))
        {
            UnsubscribeOwner();
            mOwner = owner;
            mOwner.Closed += OnOwnerClosed;
        }

        window.Show(owner);
        PlaceBeside(window, owner);
    }

    static Window? MainWindow()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    // 贴在主窗口右侧；放不下（主窗口最大化时必然放不下）就贴右边缘，并被夹进工作区。
    // 有归属关系在，即使重叠也始终浮在主窗口之上。
    static void PlaceBeside(Window window, Window owner)
    {
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
        if (screen == null)
            return;

        double scale = owner.RenderScaling > 0 ? owner.RenderScaling : 1;

        var topLeft = owner.PointToScreen(new Point(0, 0));
        var topRight = owner.PointToScreen(new Point(owner.Bounds.Width, 0));

        int width = (int)Math.Round(window.Width * scale);
        int height = (int)Math.Round(window.Height * scale);

        var area = screen.WorkingArea;

        int x = Math.Clamp(topRight.X + 8, area.X, Math.Max(area.X, area.Right - width));
        int y = Math.Clamp(topLeft.Y, area.Y, Math.Max(area.Y, area.Bottom - height));

        window.Position = new PixelPoint(x, y);
    }

    void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
            window.Closed -= OnWindowClosed;

        mWindow = null;
        mModel = null;
    }

    void OnOwnerClosed(object? sender, EventArgs e)
    {
        UnsubscribeOwner();

        var window = mWindow;
        mWindow = null;
        mModel = null;

        if (window == null)
            return;

        try
        {
            window.Detach();
            window.Close();
        }
        catch (Exception ex)
        {
            // 退出路径上不许抛：最坏情况是这扇窗没关掉，也比带走进程好。
            mHost?.LogError("mixer: failed to close the mixer window with the main window", ex);
        }
    }

    void UnsubscribeOwner()
    {
        if (mOwner == null)
            return;

        mOwner.Closed -= OnOwnerClosed;
        mOwner = null;
    }
}
