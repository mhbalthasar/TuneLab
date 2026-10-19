using System;
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

    public void Attach(IAnchorTable anchors)
    {
        // 模型要等编辑器就绪才取得到，所以宿主句柄在 app.ready 才落袋；菜单命令是之后才点的。
        anchors.OnAppReady(anchor => mHost = anchor.Host);
        anchors.AddCommand(new ExperimentalCommand("mixer.open", Label(anchors.Host), Open));
    }

    public void Detach()
    {
        mWindow?.Detach();
        mWindow?.Close();
        mWindow = null;
        mModel = null;
        mHost = null;
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

            mWindow.Show();
            return;
        }

        // 模型换了（换工程 / 宿主重建适配器）：旧窗连同它的浮窗一起丢掉，避免读到旧数据。
        mWindow?.Detach();
        mWindow?.Close();

        mModel = model;
        mWindow = new MixerWindow(model, host);
        mWindow.Show();
    }
}
