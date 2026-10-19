using TuneLab.ExperimentalPatch.Abstractions;

namespace TuneLab.ExperimentalPatch.FunctionHooker;

// 宿主与本层之间的【唯一】接口。宿主只调这四个静态方法，其余全部封在这里：
//   Boot(host, args) ─ 发现补丁 → 装配 → 触发 app.startup
//   RaiseReady()     ─ 触发 app.ready
//   Shutdown()       ─ 触发 app.shutdown → 卸载全部补丁
//   (诊断属性见下)
//
// 关键纪律：本类及其调用点【不得抛异常到宿主】。装配失败逐个兜住；宿主侧调用点也只是顺序上的
// 一行，失败至多让补丁不生效，绝不连累主程序启动/退出（与 LegacyCompatLoader 同一条底线）。
public static class PatchHost
{
    public static bool IsBooted => mHub != null;

    // 已装配 / 装配失败的补丁 id，供诊断（宿主日志、将来的「补丁」设置页）。
    public static IReadOnlyList<string> LoadedPatches => mLoaded;
    public static IReadOnlyList<string> FailedPatches => mFailed;

    // 宿主：进程已初始化、Avalonia 起来之前调用。commandLineArgs 不含可执行文件自身。
    public static void Boot(IHostServices host, IReadOnlyList<string> commandLineArgs)
    {
        if (mHub != null)
        {
            host.LogWarning("PatchHost.Boot called more than once; ignoring.");
            return;
        }

        try
        {
            var hub = new AnchorHub(host);
            mHub = hub;

            foreach (var patch in PatchRegistry.Discover(host))
            {
                try
                {
                    patch.Attach(hub);
                    mAttached.Add(patch);
                    mLoaded.Add(patch.Id);
                    host.Log($"patch attached: {patch.Id}");
                }
                catch (Exception ex)
                {
                    // 单个补丁装配失败不影响其余补丁，更不影响宿主。
                    mFailed.Add(patch.Id);
                    host.LogError($"patch '{patch.Id}' failed to attach", ex);
                }
            }

            host.Log($"patch host booted: {mLoaded.Count} loaded, {mFailed.Count} failed.");
            hub.RaiseAppStartup(new AppStartupAnchor(host, commandLineArgs));
        }
        catch (Exception ex)
        {
            // 连装配框架自己都炸了：降级为「没有补丁」，主程序照常启动。
            host.LogError("patch host boot failed; experimental patches are disabled", ex);
            mHub = null;
        }
    }

    // 宿主：主窗口就绪后调用。
    public static void RaiseReady()
    {
        var hub = mHub;
        if (hub == null)
            return;

        // 本层不兜这里——只可能是本层自己的 bug，不该悄悄吞掉；呼救对象是宿主日志。
        hub.RaiseAppReady(new AppReadyAnchor(hub.Host));
    }

    // 宿主：启动时索取闪屏。没有补丁提供、或提供者全部失败 → null，宿主走无闪屏的原样路径。
    // 这也是「补丁层不存在时宿主行为逐行不变」的保证所在。
    public static IStartupScreen? CreateStartupScreen()
    {
        var hub = mHub;
        if (hub == null)
            return null;

        try
        {
            return hub.CreateStartupScreen();
        }
        catch (Exception ex)
        {
            hub.Host.LogError("failed to create startup screen", ex);
            return null;
        }
    }

    // 宿主：退出收尾调用（须早于宿主引擎销毁，补丁才能安全访问宿主服务）。
    public static void Shutdown()
    {
        var hub = mHub;
        if (hub == null)
            return;

        mHub = null;

        try
        {
            hub.RaiseAppShutdown(new AppShutdownAnchor(hub.Host));
        }
        catch (Exception ex)
        {
            hub.Host.LogError("patch host shutdown raised", ex);
        }

        // 反向卸载：先装配的后卸载。Detach 抛错逐个兜住，一个补丁不许拖垮退出流程。
        foreach (var patch in mAttached.AsEnumerable().Reverse())
        {
            try
            {
                patch.Detach();
            }
            catch (Exception ex)
            {
                hub.Host.LogError($"patch '{patch.Id}' failed to detach", ex);
            }
        }

        mAttached.Clear();
        mLoaded.Clear();
        mFailed.Clear();
    }

    static AnchorHub? mHub;
    static readonly List<IPatch> mAttached = [];
    static readonly List<string> mLoaded = [];
    static readonly List<string> mFailed = [];
}
