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

    // 贡献型锚点：登记一个闪屏工厂，宿主启动时取第一个能给出闪屏的。
    public IDisposable ProvideStartupScreen(Func<IStartupScreen> factory)
    {
        mStartupScreens.Add(factory);
        return new Subscription(() => mStartupScreens.Remove(factory));
    }

    // 命令贡献：「实验功能」菜单的条目。宿主构建菜单时取一次快照。
    public IDisposable AddCommand(ExperimentalCommand command)
    {
        mCommands.Add(command);
        return new Subscription(() => mCommands.Remove(command));
    }

    internal IReadOnlyList<ExperimentalCommand> GetCommands() => mCommands.ToArray();

    // 设置页贡献：宿主的设置窗每次打开时取一次快照。
    public IDisposable AddSettingsTab(SettingsTabContribution tab)
    {
        mSettingsTabs.Add(tab);
        return new Subscription(() => mSettingsTabs.Remove(tab));
    }

    internal IReadOnlyList<SettingsTabContribution> GetSettingsTabs() => mSettingsTabs.ToArray();

    // 取第一个能给出闪屏的提供者。提供者抛错只记日志、继续问下一个——一个坏补丁不该让所有人没闪屏。
    internal IStartupScreen? CreateStartupScreen()
    {
        foreach (var factory in mStartupScreens.ToArray())
        {
            try
            {
                if (factory() is { } screen)
                    return screen;
            }
            catch (Exception ex)
            {
                Host.LogError("startup screen provider threw", ex);
            }
        }

        return null;
    }

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
    readonly List<Func<IStartupScreen>> mStartupScreens = [];
    readonly List<ExperimentalCommand> mCommands = [];
    readonly List<SettingsTabContribution> mSettingsTabs = [];
}
