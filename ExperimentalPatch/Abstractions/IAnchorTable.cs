namespace TuneLab.ExperimentalPatch.Abstractions;

// 补丁接入宿主的【唯一】把手。宿主在启动时构造一份实现（FunctionHooker.AnchorHub）交给每个补丁，
// 补丁只能经它订阅锚点——补丁因此拿不到宿主内部类型，宿主重构也不会连带编译补丁。
//
// 【加锚点 = 加一个方法】本接口每多一个方法，宿主与全部补丁都会收到编译期提示，
// 这正是想要的：锚点是双方共享的契约，不能悄悄漂移。步骤见 .agents/AGENTS.md。
//
// 订阅一律返回 IDisposable：由 FunctionHooker 在卸载补丁时统一回收，
// 补丁作者不必自己记订阅（但也应持有它以便提前取消）。
public interface IAnchorTable
{
    // 基础能力面（日志、版本、数据目录）。所有补丁都能用。
    IHostServices Host { get; }

    // 宿主进程初始化完成、Avalonia 起来之前。
    IDisposable OnAppStartup(Action<AppStartupAnchor> handler);

    // 主窗口就绪。需要窗口/编辑器时用这个，不要在 OnAppStartup 里碰 UI。
    IDisposable OnAppReady(Action<AppReadyAnchor> handler);

    // 宿主退出（引擎销毁之前）。
    IDisposable OnAppShutdown(Action<AppShutdownAnchor> handler);
}
