using TuneLab.ExperimentalPatch.Abstractions;

namespace TuneLab.ExperimentalPatch.Patches.Sample;

// 示例补丁：只往日志里打点，用来验证接线与三个生命周期锚点的触发顺序。
// 用不着了就删掉整个 Patches/Sample 目录——不影响其余补丁（发现靠反射，没有清单要维护）。
[Patch(Order = 100)]
public sealed class SamplePatch : IPatch
{
    public string Id => "sample.hello";

    public void Attach(IAnchorTable anchors)
    {
        anchors.OnAppStartup(anchor =>
            anchor.Host.Log($"[{Id}] app.startup: {anchor.CommandLineArgs.Count} command line arg(s)"));

        anchors.OnAppReady(anchor =>
            anchor.Host.Log($"[{Id}] app.ready: main window is up"));

        anchors.OnAppShutdown(anchor =>
            anchor.Host.Log($"[{Id}] app.shutdown"));
    }

    public void Detach()
    {
        // 订阅型副作用由 FunctionHooker 统一回收，这里无需解绑。
    }
}
