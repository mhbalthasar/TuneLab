using TuneLab.ExperimentalPatch.Abstractions;

namespace TuneLab.ExperimentalPatch.FunctionHooker;

// 锚点表实现：宿主经 PatchHost 建一份，补丁经 IPatch.Attach 拿到它订阅锚点。
// 全部订阅都落在这里的 handler 清单里，卸载时统一 Dispose。
internal sealed class AnchorHub(IHostServices host) : IAnchorTable
{
    public IHostServices Host { get; } = host;

    public IDisposable OnAppStartup(Action<AppStartupAnchor> handler) => Subscribe(mAppStartup, handler);
    public IDisposable OnAppReady(Action<AppReadyAnchor> handler) => Subscribe(mAppReady, handler);
    public IDisposable OnAppShutdown(Action<AppShutdownAnchor> handler) => Subscribe(mAppShutdown, handler);

    internal void RaiseAppStartup(AppStartupAnchor anchor) => Raise(mAppStartup, anchor, AnchorIds.AppStartup);
    internal void RaiseAppReady(AppReadyAnchor anchor) => Raise(mAppReady, anchor, AnchorIds.AppReady);
    internal void RaiseAppShutdown(AppShutdownAnchor anchor) => Raise(mAppShutdown, anchor, AnchorIds.AppShutdown);

    // 逐个 handler 兜异常：一个补丁抛错不得影响其余补丁，更不得崩主程序。
    // 快照 ToArray 是必须的——handler 在自身回调里取消订阅会让 List 在枚举中变化。
    void Raise<T>(List<Action<T>> handlers, T anchor, string anchorId)
    {
        foreach (var handler in handlers.ToArray())
        {
            try
            {
                handler(anchor);
            }
            catch (Exception ex)
            {
                Host.LogError($"patch handler for anchor '{anchorId}' threw", ex);
            }
        }
    }

    static IDisposable Subscribe<T>(List<Action<T>> handlers, Action<T> handler)
    {
        handlers.Add(handler);
        return new Subscription(() => handlers.Remove(handler));
    }

    sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    readonly List<Action<AppStartupAnchor>> mAppStartup = [];
    readonly List<Action<AppReadyAnchor>> mAppReady = [];
    readonly List<Action<AppShutdownAnchor>> mAppShutdown = [];
}
