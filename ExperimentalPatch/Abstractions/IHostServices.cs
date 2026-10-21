namespace TuneLab.ExperimentalPatch.Abstractions;

// 宿主开给补丁的基础能力面。这是补丁能看到宿主的【全部】入口——刻意收窄，
// 每多一个成员都等于多一处上游变更会波及补丁的耦合面，按需再加（见 .agents/AGENTS.md）。
//
// 实现方 = 宿主的 ExperimentalPatchBootstrap（TuneLab/ExperimentalPatchBootstrap.cs）。
public interface IHostServices
{
    // 宿主版本号（AppInfo.Version）。
    string AppVersion { get; }

    // 宿主数据目录（PathManager.TuneLabFolder）——补丁要落盘自己的文件时用它当基准，
    // 不要自己拼 %APPDATA%（该目录受 TUNELAB_DATA_DIR 重定向，沙盒/截图工具要靠它）。
    string DataFolder { get; }

    // 界面语言（如 "zh-CN" / "en-US"）。补丁要出面向用户的文案时按它选，别用 CurrentUICulture
    // ——宿主换语言走的是自己的设置，不动进程文化。
    string UiLanguage { get; }

    // 经宿主日志系统落盘（带补丁前缀归属），走的是与插件同一套 FileLogger。
    void Log(string message);
    void LogWarning(string message);

    // exception 可空：只有一句话、没有异常对象时用 LogError(message)。
    void LogError(string message, Exception? exception = null);

    // 混音台的模型（宿主实现，见 TuneLab/ExperimentalPatchMixerModel.cs）。
    // 没有打开的工程 / 编辑器尚未就绪时返回 null —— 调用方负责重试或显示空态。
    // 默认实现返回 null：让只做日志/闪屏的旧实现（harness）不必跟着改。
    IMixerModel? CreateMixerModel() => null;

    // 插入效果器的插件扫描设置（宿主持有的本机设置 + 扫描缓存）。
    // 同样有默认实现：不接插入效果的宿主不该被迫实现它。
    IInsertPluginSettings? InsertPlugins => null;

    // 播放/暂停（空格）。混音台是独立窗口，焦点在它上面时按键不会到主窗 ——
    // 补丁侧经这里把"空格"透传回宿主。默认空实现，无头 harness 不必关心。
    void TogglePlayback() { }
}

