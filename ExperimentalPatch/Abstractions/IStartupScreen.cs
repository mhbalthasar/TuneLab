namespace TuneLab.ExperimentalPatch.Abstractions;

// 启动闪屏的契约。宿主只管【时序】——什么时候显示、什么时候报告失败、什么时候关；
// 窗口长什么样完全归补丁。
//
// 【为什么这里没有 Avalonia 类型】宿主把闪屏塞进 desktop.MainWindow 固然方便，但那要求契约
// 暴露 Window，等于把宿主的启动代码和补丁的窗口类绑在一起。实测（见 .agents/startup-timeline.md）
// 桌面生命周期在 MainWindow 为 null、零窗口时不会退出，故补丁自己 show 自己的窗口即可，
// 宿主只需要认识下面这四个方法。
public interface IStartupScreen
{
    // 显示闪屏。宿主在 Avalonia 就绪、消息循环起来之前调用它，并在随后让出一次消息循环。
    void Show();

    // 当前阶段。文案由闪屏自己决定（本层不做 i18n，见 .agents/host-seams.md 的待办）。
    void SetPhase(StartupPhase phase);

    // 报告初始化失败的项。返回的 Task 在用户确认后完成——宿主据此决定何时继续启动。
    // 只在真的有失败时调用。
    Task ShowFailuresAsync(IReadOnlyList<string> failures);

    // 关闭闪屏。宿主保证此刻主窗口已经显示（否则最后一个窗口关闭会让整个进程退出）。
    void Close();
}

public enum StartupPhase
{
    // 刚显示，还没进具体阶段（兜底）。
    Preparing,

    // 正在初始化音频设备（AudioUtils / AudioEngine）。
    PreparingAudio,

    // 正在加载扩展包。
    LoadingExtensions,

    // 正在初始化音源引擎（voice / instrument）。
    InitializingEngines,

    // 正在打开编辑器（新建主窗口）。
    OpeningEditor,
}
