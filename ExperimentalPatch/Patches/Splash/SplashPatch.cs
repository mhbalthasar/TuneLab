using TuneLab.ExperimentalPatch.Abstractions;

namespace TuneLab.ExperimentalPatch.Patches.Splash;

// 启动闪屏补丁：把 SplashWindow 贡献给宿主。
//
// 宿主拿到的是一个工厂——闪屏是个 Avalonia 窗口，只能在 Avalonia 就绪之后构造，而 app.startup
// 早于它。何时调用由宿主决定（见 .agents/startup-timeline.md）。
[Patch(Order = 0)]
public sealed class SplashPatch : IPatch
{
    public string Id => "splash.startup";

    public void Attach(IAnchorTable anchors)
    {
        anchors.ProvideStartupScreen(() => new SplashWindow(anchors.Host));
    }

    public void Detach()
    {
        // 闪屏的生命周期随启动结束；无需解绑（订阅型副作用由 FunctionHooker 统一回收）。
    }
}
