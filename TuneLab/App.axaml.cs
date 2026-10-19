using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using TuneLab.Animation;
using TuneLab.Audio;
using TuneLab.Audio.NAudio;
using TuneLab.Audio.SDL2;
using TuneLab.Foundation;
using TuneLab.Extensions;
using TuneLab.Extensions.Formats;
using TuneLab.SDK;
using TuneLab.GUI;
using TuneLab.Configs;
using TuneLab.UI;
using TuneLab.Utils;
using TuneLab.I18N;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using System.Threading;
using System.IO.Pipes;

using TuneLab.Extensions.Instruments;
using TuneLab.Extensions.Voices;
using TuneLab.Bridge;
// 启动闪屏的契约（补丁层提供实现）。契约里刻意没有 Avalonia 类型，宿主只按这四个方法调度时序。
using TuneLab.ExperimentalPatch.Abstractions;
namespace TuneLab;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                desktop.Startup += (s, e) =>
                {
                    AnimationManager.SharedManager.Init();
                };
                // 插件按钮（ButtonConfig）点击的宿主侧执行：来源归属、异常提示、退出时取消（见 PluginButtonInvoker）。
                TuneLab.GUI.Controllers.ButtonActions.Invoke = PluginButtonInvoker.Invoke;
                desktop.Exit += (s, e) =>
                {
                    ExperimentalPatchBootstrap.Stop();   // 锚点 app.shutdown：须早于引擎销毁，补丁才拿得到宿主服务
                    HostLifetime.Shutdown();   // 先取消插件侧长任务（按钮、进度弹窗），再拆引擎
                    ExtensionManager.LaunchPendingUninstalls();
                    ExtensionManager.Destroy();
                    AudioEngine.Destroy();
                };

                // init audio engine
                AudioUtils.Init(new NAudioCodec());
                AudioEngine.SampleRate.Value = Settings.SampleRate;
                AudioEngine.BufferSize.Value = Settings.BufferSize;
                if (!string.IsNullOrEmpty(Settings.AudioDriver)) AudioEngine.CurrentDriver.Value = Settings.AudioDriver;
                if (!string.IsNullOrEmpty(Settings.AudioDevice)) AudioEngine.CurrentDevice.Value = Settings.AudioDevice;
                AudioEngine.Init();
                AudioEngine.LoadKeySamples(Settings.PianoKeySamplesPath);
                AudioEngine.MasterGain = Settings.MasterGain;
                Settings.PianoKeySamplesPath.Modified.Subscribe(() => AudioEngine.LoadKeySamples(Settings.PianoKeySamplesPath));
                Settings.MasterGain.Modified.Subscribe(() => { AudioEngine.MasterGain = Settings.MasterGain; });
                Settings.BufferSize.Modified.Subscribe(() => { AudioEngine.BufferSize.Value = Settings.BufferSize; });
                Settings.SampleRate.Modified.Subscribe(() => { AudioEngine.SampleRate.Value = Settings.SampleRate; });
                Settings.AudioDriver.Modified.Subscribe(() => { AudioEngine.CurrentDriver.Value = Settings.AudioDriver; });
                Settings.AudioDevice.Modified.Subscribe(() => { AudioEngine.CurrentDevice.Value = Settings.AudioDevice; });

                LegacyCompatLoader.Wire();

                // ── 启动闪屏（补丁贡献；无补丁 = null）──
                // 本方法跑在消息循环之前，此刻 show 出来的窗口还不会绘制。所以有闪屏时，把余下的启动慢段
                // （扩展加载 → 音源引擎 → 建主窗口）交给消息循环去跑，闪屏才有机会先画出来再继续；
                // 没有闪屏时原样同步跑完——补丁层缺席时宿主行为逐行不变。
                //
                // 闪屏自己 show 自己的窗口，宿主不碰 desktop.MainWindow：桌面生命周期在 MainWindow 为
                // null、零窗口时并不会退出（实测见 ExperimentalPatch/.agents/startup-timeline.md），
                // 故补丁契约里可以不出现 Avalonia 类型。唯一的硬约束是【关闪屏必须在主窗口显示之后】。
                mStartupScreen = ExperimentalPatchBootstrap.CreateStartupScreen();
                mStartupScreen?.SetPhase(StartupPhase.LoadingExtensions);
                mStartupScreen?.Show();

                if (mStartupScreen == null)
                    ContinueStartup(desktop);   // 无补丁：原样同步，行为逐行不变
                else
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => ContinueStartup(desktop), Avalonia.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                mStartupScreen?.Close();
                mStartupScreen = null;
                ShowLaunchFailure(ex);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // 启动慢段：扩展加载 → 音源引擎 → 建主窗口 → 命令桥/启动参数/app.ready。
    //
    // 无闪屏时【全程同步】：本方法里唯一的 await 是 YieldToRenderIfSplashAsync，它在无闪屏时直接返回
    // （await 一个已完成的 ValueTask 不会让出），故不排队、不切线程，与上游逐行等价。
    // 有闪屏时逐相位让出：每个相位前让出一次消息循环，进度条才会动、文案才会变。
    async void ContinueStartup(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var started = Stopwatch.StartNew();
            bool splashing = mStartupScreen != null;

            // 相位 1：加载扩展
            mStartupScreen?.SetPhase(StartupPhase.LoadingExtensions);
            await YieldToRenderIfSplashAsync();
            ExtensionManager.LoadExtensions();

            // 相位 2：音源引擎
            mStartupScreen?.SetPhase(StartupPhase.InitializingEngines);
            await YieldToRenderIfSplashAsync();

            // 音源引擎（voice 与 instrument）须在 MainWindow 构建前初始化：MainWindow 构造时会新建默认
            // 工程，其 part 立即 Activate 并构建合成管线，此刻引擎若未 Init 则会回落到空会话且无回建
            // 路径（起动即无声）。（同时也让「设置音源」右键菜单更快弹出。）
            //
            // instrument 与 voice 在这件事上【同构】：两者都挂在 MidiPart 上作音源（XOR 二选一）、
            // 都要在菜单里列出自己的音源目录，所以两者都得急切 Init——只 Init voice 会让 instrument part
            // 起动无声、菜单也慢。effect 不在此列且不该在：它没有音源目录，按 part 用到才 Init 是对的。
            // 失败只报不停；哪些引擎要急切 Init、以及为何必须在挂工程之前，
            // 那条判据收在 SoundSourceEngines.InitAll（见那里），这里只管告诉眼前的用户。
            var engineFailures = SoundSourceEngines.InitAll();
            if (engineFailures.Count != 0)
            {
                if (mStartupScreen != null)
                {
                    // 有闪屏：失败就地显示、等用户确认再进主窗口（「全部加载成功再进窗口」的失败分支）。
                    await mStartupScreen.ShowFailuresAsync(engineFailures);
                }
                else
                {
                    // 无闪屏：保持上游的「逐个弹框、只报不停」。
                    foreach (var failure in engineFailures)
                    {
                        var dialog = new Dialog();
                        dialog.SetTitle("Error");
                        dialog.SetMessage(failure);
                        dialog.AddButton("OK", Dialog.ButtonType.Primary);
                        dialog.Show();
                    }
                }
            }

            // 闪屏最短显示时长：快机器上压掉「一闪而过」。
            if (splashing)
            {
                int remaining = MinimumSplashMilliseconds - (int)started.ElapsedMilliseconds;
                if (remaining > 0)
                    await Task.Delay(remaining);
            }

            // 相位 3：建主窗口
            mStartupScreen?.SetPhase(StartupPhase.OpeningEditor);
            await YieldToRenderIfSplashAsync();

            mMainWindow = new MainWindow();
            desktop.MainWindow = mMainWindow;
            // 闪屏路径下 MainWindow 是后设的（晚于生命周期 Start），不会自动显示，得自己 show；
            // 同步路径下它早于 Start 设好，由生命周期显示。
            if (splashing)
                mMainWindow.Show();

            // 关闪屏必须在主窗口显示【之后】：否则窗口数落到 0，进程当场退出（ShutdownMode.OnLastWindowClose）。
            mStartupScreen?.Close();
            mStartupScreen = null;

            // 命令桥（默认关，用户在设置里开）：本机的外部进程经命名管道驱动这个实例，跑的是与
            // AI Agent 面板同一份命令。放在主窗口之后——编辑器在构造时才把宿主执行环境装上。
            CommandBridge.Init();

            // 检测启动参数（args[0] 是可执行文件自身，不是参数）
            var args = Environment.GetCommandLineArgs();
            Log.Info("Command line args: " + string.Join(" ", args, 1, Math.Max(0, args.Length - 1)));
            HandleStartupArgs(args[1..]);

            // 锚点 app.ready：主窗口已建、启动参数已分流（工程已打开）。
            // 需要窗口/编辑器才能做的注入放这一档，别提前到 app.startup。
            ExperimentalPatchBootstrap.Ready();

            // 获取主线程SynchronizationContext
            var context = SynchronizationContext.Current ?? throw new InvalidOperationException("SynchronizationContext.Current is null");

            // 监听其他实例的启动参数
            Task.Run(() =>
            {
                while (true)
                {
                    using var pipeServer = new NamedPipeServerStream("TuneLab", PipeDirection.In);
                    pipeServer.WaitForConnection();

                    using var reader = new StreamReader(pipeServer);
                    // 一次连接 = 另一个实例的一次启动：把它那一批参数**收齐**再统一分流。逐行处理会让
                    // "一批里只认第一个工程"退化成"每一行都算第一个"，四个参数就又变回四个弹窗。
                    var received = new List<string>();
                    while (true)
                    {
                        var arg = reader.ReadLine();
                        if (arg == null)
                            break;   // 流到头了。原先写的是 continue，对方断开后这里会空转烧 CPU。

                        Log.Info($"Received from another instance: {arg}");
                        received.Add(arg);
                    }
                    if (received.Count != 0)
                        context.Post(_ => HandleStartupArgs(received), null);
                }
            });
        }
        catch (Exception ex)
        {
            mStartupScreen?.Close();
            mStartupScreen = null;
            ShowLaunchFailure(ex);
        }
    }

    // 让出一次消息循环。Render 的优先级高于 Background，故排在 Background 的续体一定晚于这一帧的绘制
    // ——闪屏刚设的文案先画出来，再继续下一相位。
    // 无闪屏时直接返回（不切线程、不排队），保证同步路径与上游逐行一致。
    async ValueTask YieldToRenderIfSplashAsync()
    {
        if (mStartupScreen == null)
            return;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(static () => { }, Avalonia.Threading.DispatcherPriority.Background);
    }

    void ShowLaunchFailure(Exception ex)
    {
        var dialog = new Dialog();
        dialog.SetTitle("Launch Failed");
        dialog.SetMessage(ex.ToString());
        dialog.AddButton("Quit", Dialog.ButtonType.Primary).Clicked += () => { Process.GetCurrentProcess().Kill(); };
        dialog.Show();
    }

    // 一批启动参数（命令行 / 双击关联文件 / 第二个实例转发过来的那一批）。规则与理由见 StartupArgs。
    public void HandleStartupArgs(IReadOnlyList<string> args)
    {
        if (mMainWindow == null)
            return;

        var plan = StartupArgs.Split(args, LooksLikeProjectFile);
        foreach (var ignored in plan.Ignored)
            Log.Info("Ignored startup argument (not a file we can open): " + ignored);

        if (plan.ExtensionPackages.Count != 0)
            mMainWindow.Editor.InstallExtensions(plan.ExtensionPackages);

        if (plan.ProjectPath != null)
            mMainWindow.Editor.OpenProjectByPath(plan.ProjectPath);
    }

    // 值不值得送去"打开工程"：文件在那儿，或者后缀是已注册的导入格式（那种情况下打不开要如实提示，
    // 用户多半是双击了一个已被删除/移走的工程）。都不是就不该弹框——那不是他要打开的东西。
    static bool LooksLikeProjectFile(string path)
    {
        if (File.Exists(path))
            return true;

        var suffix = Path.GetExtension(path).TrimStart('.');
        if (suffix.Length == 0)
            return false;

        foreach (var format in FormatsManager.GetAllImportFormats())
        {
            if (string.Equals(format, suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // 启动闪屏（补丁提供；无补丁 = null，此时 ContinueStartup 走原样同步路径）。
    IStartupScreen? mStartupScreen = null;

    // 闪屏最短显示时长：快机器上压掉「一闪而过」。只在有闪屏时生效。
    const int MinimumSplashMilliseconds = 600;

    MainWindow? mMainWindow = null;
}
