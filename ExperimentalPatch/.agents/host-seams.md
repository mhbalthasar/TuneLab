# 宿主接缝（host seams）— 改哪里、怎么改

> 本文是**动手前必读**：补丁本身不需要碰宿主（见 §1）；需要碰宿主时，**只允许碰 §2 表里那六个位置 + 新增适配器文件**。
> 规矩与理由见 `AGENTS.md`。本文只讲"确切在哪、写成什么样"。

## 1. 先问：真的需要改宿主吗？

大多数补丁**一行宿主代码都不用改**——只要它用的锚点已存在，改的都只在 `ExperimentalPatch/` 里：

| 你想做的事 | 需要改宿主吗 |
|---|---|
| 进程启动时读自己的配置、装全局钩子 | 否，订阅 `app.startup` |
| 主窗口出来后加按钮/面板/菜单、拦命令 | 否，订阅 `app.ready` |
| 退出时清理 | 否，订阅 `app.shutdown` |
| 提供一个启动闪屏 | 否，`ProvideStartupScreen(...)` |
| 宿主在某个**新时机**通知补丁 | **是**，加一个通知型锚点（§3） |
| 宿主向补丁**索取**某种新对象 | **是**，加一个贡献型锚点（§4） |

判断标准：**你要的是"在这儿通知我"还是"宿主在这里的行为本身要变"？** 前者加锚点；后者才动宿主结构，
而且要按 `AGENTS.md` §0「两类接缝」写清楚为什么。

## 2. 宿主上允许改的位置（六个 + 新增适配器文件）

| # | 文件 | 位置（锚点/语句） | 现在写的是什么 | 说明 |
|---|---|---|---|---|
| 1 | `TuneLab/Program.cs` | `InitCoreServices()` 之后 | `ExperimentalPatchBootstrap.Start(args);` | 装配补丁 + `app.startup`。Avalonia 未起，**别在这儿碰 UI**。 |
| 2 | `TuneLab/App.axaml.cs` | `desktop.Exit += ...` 之后（`OnFrameworkInitializationCompleted` 开头） | `mStartupScreen = ExperimentalPatchBootstrap.CreateStartupScreen();` + `SetPhase` + `Show()` + 把余下启动整段交给 `ContinueStartup` | 闪屏索取点。**结构性接缝**，见 `startup-timeline.md`。 |
| 3 | `TuneLab/App.axaml.cs` | `HandleStartupArgs(args[1..]);` 之后 | `ExperimentalPatchBootstrap.Ready();` | `app.ready`。 |
| 4 | `TuneLab/App.axaml.cs` | `desktop.Exit` 首行 | `ExperimentalPatchBootstrap.Stop();` | `app.shutdown` + 卸载。须早于引擎销毁。 |
| 5 | `TuneLab/ExperimentalPatchBootstrap.cs` | 整个文件 | 四个转发方法 + `IHostServices` 实现 | 「缝」本身，**尽量只增不改**。 |
| 6 | `TuneLab/TuneLab.csproj`、`TuneLab.sln` | — | 一条 `ProjectReference` + 一条工程项 | 只加一次。 |
| 7 | `TuneLab/ExperimentalPatch<功能>.cs`（**新增文件**） | — | 适配器：把宿主 `internal` 类型（`Track` / `Project` / `AudioEngine` …）翻成契约 | **上游没有同名文件，冲突恒为零**。需要宿主内部类型时走这里，别塞进缝文件。 |

**除这六处 + 新增的适配器文件，宿主其余文件一律不改。** 需要"宿主在别处也通知一下"时，不是去改那个文件——而是把调用点
放到这六个位置里合适的那一处，或者按 §3/§4 新开一个锚点并**把它登记回本表**。

## 3. 加一个通知型锚点（宿主 → 补丁）

以"宿主打开工程后通知补丁"为例。改四处，其中**宿主只有一行**：

**① 契约**（`Abstractions/`）

```csharp
// Abstractions/AnchorIds.cs
public const string ProjectOpened = "project.opened";

// Abstractions/Anchors/ProjectAnchors.cs
// payload 只用契约类型；用 record 以便将来加字段不破坏既有补丁的编译。
public sealed record ProjectOpenedAnchor(IHostServices Host, string ProjectPath);

// Abstractions/IAnchorTable.cs
IDisposable OnProjectOpened(Action<ProjectOpenedAnchor> handler);
```

**② 耦合层**（`FunctionHooker/`）

```csharp
// AnchorHub：加清单 + 订阅方法 + Raise
readonly List<Action<ProjectOpenedAnchor>> mProjectOpened = [];
public IDisposable OnProjectOpened(Action<ProjectOpenedAnchor> handler) => Subscribe(mProjectOpened, handler);
internal void RaiseProjectOpened(ProjectOpenedAnchor anchor) => Raise(mProjectOpened, anchor, AnchorIds.ProjectOpened);

// PatchHost：暴露给宿主
public static void RaiseProjectOpened(ProjectOpenedAnchor anchor)
{
    var hub = mHub;
    if (hub == null)
        return;
    hub.RaiseProjectOpened(anchor);
}
```

**③ 宿主一行**（`TuneLab/ExperimentalPatchBootstrap.cs` 加转发，然后在真正的注入点加一行）

```csharp
// ExperimentalPatchBootstrap.cs（缝，只加方法）
public static void ProjectOpened(string projectPath)
{
    Guard(() => PatchHost.RaiseProjectOpened(new ProjectOpenedAnchor(hServices, projectPath)), "signal project opened");
}

// 注入点（宿主已有文件里唯一的改动，就这一行）
ExperimentalPatchBootstrap.ProjectOpened(path);
```

**④ 登记**：把新锚点写进 `AGENTS.md` §2 的表 + 本文件 §2 的表（如果新增了宿主文件/位置）。

> `Guard` 的纪律：宿主侧调用点**永不**让补丁异常冒出来。理由与 `LegacyCompatLoader` 相同——
> 补丁最坏只能"这次不生效"，不能连累启动/退出。

## 4. 加一个贡献型锚点（宿主 → 补丁索取）

以 `app.splash` 为现成范例（照抄即可）：

```csharp
// ① 契约
public interface IStartupScreen { void Show(); void SetPhase(StartupPhase phase); Task ShowFailuresAsync(IReadOnlyList<string> failures); void Close(); }
IDisposable ProvideStartupScreen(Func<IStartupScreen> factory);   // 注册工厂，不是实例

// ② 耦合层：AnchorHub
readonly List<Func<IStartupScreen>> mStartupScreens = [];
public IDisposable ProvideStartupScreen(Func<IStartupScreen> factory) { ... }
internal IStartupScreen? CreateStartupScreen()   // 第一个非 null 胜出；抛错只记日志、问下一个
{ ... }

// ③ 宿主：PatchHost.CreateStartupScreen() + ExperimentalPatchBootstrap.CreateStartupScreen()（带 Guard，返回 null）
```

**两条不变量**：

- **必须处理"没有"**：`CreateXxx()` 返回 `null` 时，宿主走**与上游逐行一致**的原样路径。这是本层存在
  的前提（`AGENTS.md` §5「无补丁 = 逐行等价」）。
- **注册的是工厂**：被贡献对象的构造时机可能晚于补丁装配时机（Avalonia 窗口就是），工厂把时机交给知道答案的一方。

## 5. 改完必须做的三件事

```powershell
# 1. 补丁层 + 宿主都编译过
dotnet build ExperimentalPatch/ExperimentalPatch.csproj -c Debug
dotnet build TuneLab.sln -c Debug

# 2. 无补丁路径没被改坏：确认 CreateXxx() 为 null 时代码仍是上游那份（对比 App.axaml.cs 的 ContinueStartup）

# 3. 文档登记：AGENTS.md §0/§2、本文件 §2、必要时 startup-timeline.md
```

## 6. 常见坑

| 现象 | 原因 | 处理 |
|---|---|---|
| 上游合并后补丁窗口不出现 | 宿主接缝那一行被上游改动盖掉 | 对照 §2 表逐处确认 |
| `MissingMethodException` 提到 SDK 成员 | 不是本层的问题——是插件层（`.tlx`）二进制陈旧，见根 `AGENTS.md` |
| 闪屏不绘制 / 界面卡住 | 在消息循环开始前做了重活 | 见 `startup-timeline.md` |
| 编译报 `Theme` / `Path` 冲突 | Avalonia 的 `StyledElement.Theme`、`System.IO.Path` 撞名 | 静态类改名（`SplashTheme`）、`using ShapePath = Avalonia.Controls.Shapes.Path` |
| 关掉闪屏后整个进程退出 | 最后一个窗口关闭（`ShutdownMode.OnLastWindowClose`） | 闪屏必须在主窗口显示**之后**才关 |
