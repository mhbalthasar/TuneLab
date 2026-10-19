namespace TuneLab.ExperimentalPatch.Abstractions;

// ── 启动/退出生命周期锚点的 payload ──
//
// payload 一律用 record：将来要往锚点上挂新信息时，加一个 init 属性不会破坏既有补丁的编译
// （记录类型的 with / 位置参数演进可比接口方法加参温和得多）。payload 里【只放契约类型】，
// 不放宿主内部类型——那是补丁与宿主之间唯一的编译期界面。

// 宿主进程已初始化（配置/翻译/日志就绪），尚未创建 Avalonia 与主窗口。
// 补丁在这里做进程级准备（读自己的配置、注册全局钩子）。
public sealed record AppStartupAnchor(IHostServices Host, IReadOnlyList<string> CommandLineArgs);

// 主窗口已就绪（MainWindow 已建、启动参数已分流处理）。补丁在这里做需要窗口/编辑器的注入。
public sealed record AppReadyAnchor(IHostServices Host);

// 宿主即将退出（宿主引擎销毁【之前】触发，补丁可安全访问宿主服务）。
// 必须快、不得抛：异常由 AnchorHub 兜住并记日志，但别指望它。
public sealed record AppShutdownAnchor(IHostServices Host);
