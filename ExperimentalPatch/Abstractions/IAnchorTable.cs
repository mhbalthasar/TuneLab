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

    // ── 贡献型锚点 ──
    // 与上面三个「宿主通知补丁」不同，这是「宿主向补丁索取」：注册一个启动闪屏工厂，宿主启动时
    // 取第一个返回非 null 的（按补丁装配顺序）。返回的 IDisposable 可撤销注册。
    //
    // 【为什么是工厂而不是实例】闪屏是个 Avalonia 窗口，只能在 Avalonia 就绪之后构造；而
    // app.startup 早于它。故注册的是工厂，由宿主挑正确的时机调用。
    IDisposable ProvideStartupScreen(Func<IStartupScreen> factory);

    // ── 命令贡献 ──
    // 登记一条「实验功能」菜单命令。宿主在**构建菜单时**一次性取走（启动早期），
    // 之后再登记不会出现在已建好的菜单里。返回的 IDisposable 可撤销登记。
    IDisposable AddCommand(ExperimentalCommand command);
}
