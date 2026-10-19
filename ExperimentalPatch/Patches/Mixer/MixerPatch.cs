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
[Patch(Order = 50)]
public sealed class MixerPatch : IPatch
{
    public string Id => "mixer.window";

    MixerWindow? mWindow;
    IMixerModel? mModel;
    IHostServices? mHost;
    Window? mOwner;

    public void Attach(IAnchorTable anchors)
    {
        // 模型要等编辑器就绪才取得到，所以宿主句柄在 app.ready 才落袋；菜单命令是之后才点的。
        anchors.OnAppReady(anchor => mHost = anchor.Host);
        anchors.AddCommand(new ExperimentalCommand("mixer.open", Label(anchors.Host), Open));
    }

    public void Detach()
    {
        if (mOwner != null)
            mOwner.Closed -= OnOwnerClosed;

        mWindow?.Detach();
        mWindow?.Close();
        mWindow = null;
        mModel = null;
        mHost = null;
        mOwner = null;
    }

    static string Label(IHostServices host)
        => host.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "混音台" : "Mixer";

    void Open()
    {
        var host = mHost;
        if (host == null)
            return;

        var model = host.CreateMixerModel();
        if (model == null)
        {
            host.LogWarning("mixer: no project is open");
            return;
        }

        // 同一个模型 = 同一个工程：窗口只隐藏不销毁，再点就是恢复（保留几何与浮窗状态）。
        if (mWindow != null && ReferenceEquals(mModel, model))
        {
            if (mWindow.IsVisible)
            {
                mWindow.Activate();
                return;
            }

            ShowWindow(mWindow);
            return;
        }

        // 模型换了（换工程 / 宿主重建适配器）：旧窗连同它的浮窗一起丢掉，避免读到旧数据。
        mWindow?.Detach();
        mWindow?.Close();

        mModel = model;
        mWindow = new MixerWindow(model, host);
        ShowWindow(mWindow);
    }

    // 让混音台成为主窗口的**归属窗口**（owned window）：
    //   · 主窗口在前台时它不会被盖住（调音台要一直看得见，与主窗口并排）；
    //   · 主窗口关闭时它跟着关 —— 否则最后一个窗口还在，进程不会退出（播放会继续跑）。
    void ShowWindow(Window window)
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = desktop?.MainWindow;

        if (owner == null || ReferenceEquals(owner, window))
        {
            window.Show();
            return;
        }

        if (!ReferenceEquals(mOwner, owner))
        {
            if (mOwner != null)
                mOwner.Closed -= OnOwnerClosed;

            mOwner = owner;
            mOwner.Closed += OnOwnerClosed;
        }

        window.Show(owner);
        PlaceBeside(window, owner);
    }

    // 贴在主窗口右侧；放不下（主窗口最大化时必然放不下）就贴右边缘，并被夹进工作区。
    // 有归属关系在，即使重叠也始终浮在主窗口之上，所以"并排"与"最大化时浮在上面"都成立。
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

    void OnOwnerClosed(object? sender, EventArgs e)
    {
        // 主窗口关了就把混音台一起关掉：进程能正常退出，音频也不会继续跑。
        mWindow?.Detach();
        mWindow?.Close();
        mWindow = null;
    }
}
