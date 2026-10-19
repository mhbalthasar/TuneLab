using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.FunctionHooker;
using TuneLab.Foundation;
using TuneLab.UI;
using HostLog = TuneLab.Foundation.Log;

namespace TuneLab;

// ExperimentalPatch 的【宿主侧接线】。宿主对本层的全部认知 = 下面三个方法 + 一份 IHostServices；
// 补丁的发现、装配、异常隔离、卸载一律在 ExperimentalPatch/FunctionHooker 里，此处刻意保持薄。
//
// 改动纪律（详见 ExperimentalPatch/.agents/AGENTS.md）：本文件属于「宿主与补丁的接缝」，
// 尽量只增不改；逻辑一律写到 ExperimentalPatch/ 里去。
internal static class ExperimentalPatchBootstrap
{
    // 进程级准备就绪、Avalonia 起来之前调用（Program.Main）。commandLineArgs 不含可执行文件自身。
    public static void Start(IReadOnlyList<string> commandLineArgs)
    {
        Guard(() => PatchHost.Boot(new HostServices(), commandLineArgs), "start");
    }

    // 主窗口就绪后调用（App.OnFrameworkInitializationCompleted）。
    public static void Ready()
    {
        Guard(PatchHost.RaiseReady, "signal ready");
    }

    // 启动时索取补丁提供的闪屏；无补丁/提供者失败 = null（宿主走无闪屏的原样路径）。
    public static TuneLab.ExperimentalPatch.Abstractions.IStartupScreen? CreateStartupScreen()
    {
        try
        {
            return PatchHost.CreateStartupScreen();
        }
        catch (Exception ex)
        {
            HostLog.Error("Experimental patch startup screen failed: " + ex);
            return null;
        }
    }

    // 「实验功能」菜单的命令（补丁贡献）。没有补丁 / 没有命令 = 空表，宿主不画那个菜单。
    public static IReadOnlyList<ExperimentalCommand> GetCommands()
    {
        try
        {
            return PatchHost.GetCommands();
        }
        catch (Exception ex)
        {
            HostLog.Error("Experimental patch commands failed: " + ex);
            return [];
        }
    }

    // 退出收尾调用（App 的 desktop.Exit），须早于宿主引擎销毁。
    public static void Stop()
    {
        mMixerModel?.Dispose();
        mMixerModel = null;
        mMixerModelDocument = null;

        Guard(PatchHost.Shutdown, "shutdown");
    }

    // 混音台的模型（补丁经 IHostServices 取）。返回的是**门面**：工程被替换时它自己换适配器
    // 并通知界面，所以补丁拿一次就够了（窗口不会指着旧工程）。
    public static IMixerModel? CreateMixerModel()
    {
        try
        {
            var document = CurrentEditor()?.Document;
            if (document?.Project == null)
                return null;

            if (mMixerModel == null || !ReferenceEquals(mMixerModelDocument, document))
            {
                mMixerModel?.Dispose();
                mMixerModel = new MixerModelFacade(document);
                mMixerModelDocument = document;
            }

            return mMixerModel;
        }
        catch (Exception ex)
        {
            HostLog.Error("Experimental patch mixer model failed: " + ex);
            return null;
        }
    }

    static Editor? CurrentEditor()
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        return (desktop?.MainWindow as MainWindow)?.Editor;
    }

    static MixerModelFacade? mMixerModel;
    static ProjectDocument? mMixerModelDocument;

    // 补丁层【永不允许】把异常抛回宿主：出问题就是「这次补丁不生效」，主程序照常跑。
    static void Guard(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            HostLog.Error(string.Format("Experimental patch layer failed to {0}: {1}", what, ex));
        }
    }

    // IHostServices 的宿主实现：把补丁的日志并进宿主日志流（带 [Patch] 前缀便于归因）。
    sealed class HostServices : IHostServices
    {
        public string AppVersion => AppInfo.VersionString;

        public string DataFolder => PathManager.TuneLabFolder;

        public string UiLanguage => I18N.TranslationManager.CurrentLanguage.Value;

        public void Log(string message) => HostLog.Info(Prefix + message);

        public void LogWarning(string message) => HostLog.Warning(Prefix + message);

        public void LogError(string message, Exception? exception = null)
            => HostLog.Error(Prefix + message + (exception == null ? string.Empty : Environment.NewLine + exception));

        public IMixerModel? CreateMixerModel() => ExperimentalPatchBootstrap.CreateMixerModel();

        // 插件扫描设置（EP-MIXER-12）。进程内一份就够：它是无状态的本机设置读写 + 缓存，
        // 与打开的工程无关，所以做成静态单例而不随工程重建。
        public IInsertPluginSettings InsertPlugins => sInsertPlugins;

        static readonly InsertPluginSettings sInsertPlugins = new();

        const string Prefix = "[ExperimentalPatch] ";
    }
}
