# 启动时序（startup timeline）— 硬约束与实测依据

> 碰启动流程（闪屏、初始化、建窗口）之前先读这篇。这里记的都是**不能违反的顺序**，以及为什么。
> 谁在下游、哪条约束是可调的，都写清楚，免得下次又靠猜。

## 1. 上游本来的启动顺序

```
Program.Main
 └─ InitCoreServices()                    ← 配置 / 翻译 / 日志就绪
     └─ ExperimentalPatchBootstrap.Start   ← 锚点 app.startup（Avalonia 还没起来）
         └─ Avalonia AppBuilder.StartWithClassicDesktopLifetime
             ├─ App.OnFrameworkInitializationCompleted()   ← 同步；【消息循环还没开始】
             │   ├─ desktop.Startup += AnimationManager.Init
             │   ├─ desktop.Exit   += 收尾（Stop / 拆引擎）
             │   ├─ 取闪屏（锚点 app.splash）→ SetPhase(Preparing) → Show()   ← 越早越好，就在这儿 show
             │   └─ 余下的启动工作整段交给消息循环：ContinueStartup(desktop)
             │       ├─ 相位 0  音频设备：AudioUtils.Init / AudioEngine.Init / 设置订阅 / LegacyCompatLoader.Wire
             │       ├─ 相位 1  扩展加载：ExtensionManager.LoadExtensions()             ← 慢
             │       ├─ 相位 2  音源引擎：SoundSourceEngines.InitAll()                  ← 慢
             │       ├─ 相位 3  建主窗口：new MainWindow(); desktop.MainWindow = ...; Show() → 关闪屏
             │       └─ 尾巴：CommandBridge.Init / HandleStartupArgs / Ready()          ← 锚点 app.ready
             └─ desktop.Start()  → RaiseStartup → 消息循环（Dispatcher）
                 └─ desktop.Exit（退出时）                 ← 锚点 app.shutdown
```

## 2. 五条硬约束

| # | 约束 | 为什么 |
|---|---|---|
| 1 | `SoundSourceEngines.InitAll()` 必须早于 `new MainWindow()` | `MainWindow` 构造时新建默认工程，其 part 立即 Activate 并建合成管线；引擎没 Init 会回落到空会话且无回建路径（**起动即无声**）。详见 `SoundSourceEngines.InitAll` 内的注释。 |
| 2 | 音频设备初始化搬进了**相位 0**（不再留在闪屏之前） | 冷启动时它要 ~3 s；留在闪屏之前 = 用户双击后先盯着空屏 ~3 s 才有反馈。搬下来后闪屏在 `OnFrameworkInitializationCompleted` 开头就出现。搬法是原文逐字下移、顺序不变（只插相位标记）。 |
| 3 | **关闪屏必须在主窗口显示之后** | `ShutdownMode.OnLastWindowClose`：窗口数落到 0 就退出进程。闪屏若先关、主窗口还没 show，程序会当场退出。 |
| 4 | `OnFrameworkInitializationCompleted` 里做的活**阻塞 UI**，且此时 show 出来的窗口**不会绘制** | 它运行在消息循环**之前**。所以"在闪屏里报告进度"这种需求，必须把重活让给循环去跑（见 §3）。 |
| 5 | `app.ready` 必须在 `HandleStartupArgs` 之后 | 锚点语义就是"主窗口已建、启动参数已分流（工程已打开）"，提前到 `app.startup` 会让需要编辑器的补丁拿到半成品。 |

## 3. 让闪屏真的能画出来：让出一次消息循环

在约束 4 下，"先 show 闪屏、再干重活"是不会出画面的。所以闪屏在 `OnFrameworkInitializationCompleted`
**开头**就 show，随后把**整段**启动工作切成相位、搬进 `ContinueStartup`，每个相位前**让出一次**：

```csharp
async ValueTask YieldToRenderIfSplashAsync()
{
    if (mStartupScreen == null)
        return;                       // 无闪屏：不切线程、不排队 —— 同步路径与上游逐行一致
    await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
}
```

依据是 Avalonia 的调度优先级 **Render 高于 Background**，所以排在 Background 的续体一定晚于这一帧的绘制
——刚设的阶段文案/进度先画出来，再继续下一相位。

`App.axaml.cs` 里慢段被搬进 `ContinueStartup`，按其上的相位标记对齐上游即可：

| 相位 | 标记 | 覆盖的上游原文 |
|---|---|---|
| `PreparingAudio` | `SetPhase` + yield | `AudioUtils.Init(...)` / `AudioEngine.*` / 设置订阅 / `LegacyCompatLoader.Wire();` |
| `LoadingExtensions` | `SetPhase` + yield | `ExtensionManager.LoadExtensions();` |
| `InitializingEngines` | `SetPhase` + yield | `SoundSourceEngines.InitAll();` |
| `OpeningEditor` | `SetPhase` + yield | `new MainWindow(); desktop.MainWindow = ...;` |

阶段 → 进度条的目标值映射在**补丁侧**（`SplashWindow.PhaseProgress`），宿主只说"到哪个阶段了"；
改阶段划分时两处（上面这张表 + 那个映射）要一起改，且进度必须单调不减。

**搬移时逐字保留上游原文**（只插相位标记），上游改这段时的对齐成本才最低。

## 4. 失败语义（用户确认后才进窗口）

`InitAll()` 失败只报不停。两条分支：

- **无闪屏**：保持上游的"逐个弹 `Dialog`"。
- **有闪屏**：`mStartupScreen.ShowFailuresAsync(failures)` 就地列出失败项 + 一个「继续」按钮，
  **返回的 `Task` 在用户点按钮后才完成**——宿主 `await` 它，所以用户不点就不会进主窗口。

## 5. 最短显示时长

快机器上闪屏会一闪而过，所以有下限（`MinimumSplashMilliseconds = 600`）：在**建主窗口之前**补足差额
（`await Task.Delay(remaining)`），而不是先显示主窗口再拖一会儿——否则那段"最少显示"会变成压在主窗口上的遮挡。
只在有闪屏时生效。

## 6. 实测依据：宿主不碰 `desktop.MainWindow`

**结论：`MainWindow` 为 `null`、且当时零窗口时，桌面生命周期并不会退出。** 故闪屏可以自己
`Show()` 自己的窗口，宿主完全不必拿到那个窗口——契约里因此可以不出现 Avalonia 类型
（`AGENTS.md` §2 的硬规矩）。

复现（Avalonia 11.3.15 headless，默认 `ShutdownMode.OnLastWindowClose`，与真实 App 相同）：

```csharp
builder.AfterSetup(app => Dispatcher.UIThread.Post(() =>
{
    // 此处 desktop.MainWindow 仍为 null，一个窗口也没有
    var splash = new Window { Width = 200, Height = 120 };
    splash.Show();                       // 补丁自己 show
    Dispatcher.UIThread.Post(() =>
    {
        var main = new Window();
        lt.MainWindow = main;            // 后设 MainWindow
        main.Show();                     // 得自己 show：Start() 已经过去了
    }, DispatcherPriority.Background);
}, DispatcherPriority.Background));

builder.StartWithClassicDesktopLifetime([]);   // → 不会提前退出，post 的任务照常跑
```

实测输出（三行齐全即通过）：

```
[job] posted job ran; MainWindow=null
[job] splash window shown; lifetime windows=1
[job] main window set+shown; windows=2
```

推论：`Start()` 只在 `MainWindow` 已经设好时替你 `Show()`；后设的必须自己 `Show()`（`ContinueStartup` 里
`if (splashing) mMainWindow.Show();` 就是这个原因）。

## 7. 怎么验证改动

1. **无补丁路径**：把 `SplashPatch` 的 `[Patch]` 摘掉（或 `Enabled = false`）跑一次真实程序——
   启动行为、失败弹框、启动参数分流都应与上游一致（`ContinueStartup` 里所有 `await` 都走早返回）。
2. **有补丁路径**：走真实锚点链路的小 harness（`PatchHost.Boot(你的 IHostServices, [])` →
   `CreateStartupScreen()` → headless 渲染），能覆盖契约、注册与工厂调用时机，且不需要真窗口：

   ```
   startup screen from anchors = ...Patches.Splash.SplashWindow
   failures completed before confirm? False   ← 用户不点就不放行
   continue button = 继续
   failures completed after confirm? True
   ```

3. **截图工具不受影响**：`tools/ScreenshotBot` 不调 `ExperimentalPatchBootstrap.Start`，故不装配补丁层、
   不会弹闪屏。若哪天它开始弹，说明启动接缝被挪错了位置。
