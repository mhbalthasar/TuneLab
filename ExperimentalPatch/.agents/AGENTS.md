# ExperimentalPatch — 补丁层约定

> 本目录是**实验性补丁层**：上游（`LiuYunPlayer/TuneLab`）功能之外的东西都写在这里，经**锚点**接进宿主。
> 目标只有一个：**上游更新时，冲突面小到可以手工合并**。所有设计取舍都服从这一条。

## 0. 一句话架构

```
宿主 TuneLab  ──ProjectReference──▶  ExperimentalPatch
      │                                    │
      │  只认四个调用点 + IHostServices      ├─ Abstractions/   契约（双方共享的唯一界面）
      └───────────────────────────────────▶├─ FunctionHooker/ 耦合层（宿主唯一的入口）
                                           └─ Patches/        补丁实现（加/删只动这里）
```

宿主对补丁层的认知被压到**四个静态方法 + 一份 `IHostServices`**：

| 宿主文件 | 位置 | 干什么 |
|---|---|---|
| `TuneLab/Program.cs` | `InitCoreServices()` 之后 | `ExperimentalPatchBootstrap.Start(args)` → 装配补丁 + 触发 `app.startup` |
| `TuneLab/App.axaml.cs` | `OnFrameworkInitializationCompleted` 开头 | `ExperimentalPatchBootstrap.CreateStartupScreen()` → 取闪屏并据此分派整段启动 |
| `TuneLab/App.axaml.cs` | `HandleStartupArgs(args[1..])` 之后 | `ExperimentalPatchBootstrap.Ready()` → 触发 `app.ready` |
| `TuneLab/App.axaml.cs` | `desktop.Exit` 首行 | `ExperimentalPatchBootstrap.Stop()` → 触发 `app.shutdown` + 卸载补丁 |
| `TuneLab/ExperimentalPatchBootstrap.cs` | — | `IHostServices` 实现 + 异常兜底 |
| `TuneLab/TuneLab.csproj` + `TuneLab.sln` | — | 一个 `ProjectReference` + 一条工程项 |

除以上六处、以及**允许新增的 `TuneLab/ExperimentalPatch*.cs` 适配器文件**（见 §5），宿主其余文件一律不改。

**适配器文件**：当一个补丁需要宿主 `internal` 类型（`Track` / `Project` / `AudioEngine` …）时，
翻译代码放一个**新增的** `TuneLab/ExperimentalPatch<功能>.cs` 里（一个功能一个文件），
由 `ExperimentalPatchBootstrap` 转出去。**上游不可能有同名文件，所以这条路的合并冲突恒为零**——
比把适配器硬塞进本该保持薄的缝文件要干净得多。

### 两类接缝，两种改法

| 类型 | 例子 | 宿主改动量 | 合并风险 |
|---|---|---|---|
| **单行锚点** | `app.startup` / `app.ready` / `app.shutdown` | 一行调用 | 极低——那一行周围上游几乎不动 |
| **结构性接缝** | `app.splash`（启动慢段被拆成相位） | 一次方法体搬移 | 中——上游若重写这段启动代码要手工对齐 |

**优先做成单行锚点。** 只有当补丁真的需要改变宿主**执行结构**（而不只是"在这里通知我一下"）时，才动结构；动了就必须在本文档与 `startup-timeline.md` 里写清楚为什么、以及搬移时保留了什么不变量。

## 1. 目录职责

| 路径 | 职责 | 谁改 |
|---|---|---|
| `Abstractions/` | 契约：`IPatch` / `IAnchorTable` / 锚点 payload / `IHostServices`。**补丁能看到宿主的全部**就是这里。 | 加锚点时改 |
| `FunctionHooker/` | 耦合层：`PatchHost`（宿主唯一入口）、`PatchRegistry`（反射发现）、`AnchorHub`（锚点分发 + 异常隔离 + 订阅回收）。 | 极少改 |
| `Patches/<功能>/` | 补丁实现。 | 日常改这里 |
| `.agents/` | 本约定。 | 约定变了才改 |

**依赖方向是单向的**：`Patches` → `Abstractions`；`FunctionHooker` → `Abstractions`。**没有任何东西依赖宿主程序集**——所以上游怎么重构，补丁都不会编译失败（只会在锚点语义变了时运行期失配，而那是可见的）。

## 2. 锚点（anchor）

**锚点 = 宿主在某个确定时机向补丁提供的接驳点。** 它由三件东西组成：

1. **id**（`AnchorIds` 里的常量，如 `app.startup`）：只用于日志与诊断，**改实现不改 id**。
2. **payload**（`Anchors/*.cs` 里的 `record`）：宿主递给补丁的数据。用 `record` 而非接口/参数列表，是为了将来加字段不破坏既有补丁的编译。
3. **`IAnchorTable` 上的一个方法**：补丁经它订阅/登记。

### 两种锚点

| 方向 | 形态 | 谁发起 | 例子 |
|---|---|---|---|
| **通知型**（宿主 → 补丁） | `IDisposable OnXxx(Action<XxxAnchor> handler)` | 宿主到点了挨个叫 | `app.startup` / `app.ready` / `app.shutdown` |
| **贡献型**（宿主 → 补丁索取） | `IDisposable ProvideXxx(Func<T> factory)` | 宿主需要了来取一件 | `app.splash` |

**贡献型为什么注册的是工厂而不是实例**：被贡献的对象（闪屏）可能只能在某个更晚的时机构造（Avalonia 窗口就得等 Avalonia 起来），而 `app.startup` 早于它。注册工厂 = 把构造时机交给知道答案的一方（宿主）。宿主取用时**第一个返回非 null 的胜出**，提供者抛错只记日志、继续问下一个。

### 两条硬规矩

- **payload 里只放契约类型**（BCL + `Abstractions` 里的类型）。放宿主内部类型 = 补丁被迫依赖宿主 = 本层存在的意义归零。
  UI 类锚点也守这条：契约里**不出现 Avalonia 类型**（`IStartupScreen` 只有 `Show/SetPhase/ShowFailuresAsync/Close`），
  窗口自己 show 自己，宿主连 `desktop.MainWindow` 都不碰——依据与实测见 `startup-timeline.md`。
- **payload 用 `record`，加字段不改构造调用**：确实要加信息时，给已有 `record` 加一个带默认值的属性，别改位置参数的个数/顺序。

### 现有锚点

| 锚点 id | 方向 | 触发时机 | payload / 契约 | 适合干什么 |
|---|---|---|---|---|
| `app.startup` | 通知 | 配置/翻译/日志就绪后，Avalonia 起来**之前** | `AppStartupAnchor`（含命令行参数） | 进程级准备：读自己的配置、装全局钩子。**别碰 UI**。 |
| `app.ready` | 通知 | 主窗口已建、启动参数已分流（工程已打开） | `AppReadyAnchor` | 需要窗口/编辑器的注入。 |
| `app.shutdown` | 通知 | 退出时，宿主引擎销毁**之前** | `AppShutdownAnchor` | 收尾。宿主服务此时仍可用；要快，别阻塞退出。 |
| `app.splash` | 贡献 | `OnFrameworkInitializationCompleted` **开头**（Avalonia 已起，启动工作一行都还没干） | `IStartupScreen` | 提供启动闪屏。没人提供 = 宿主走无闪屏的原样路径。 |

通知型锚点触发顺序固定为 `app.startup` → `app.ready` → `app.shutdown`，各**恰好一次**；`app.splash` 是索取，不参与这个序列。

### 订阅与回收

订阅/登记一律返回 `IDisposable`：`FunctionHooker` 在 `Detach`/退出时统一回收，补丁作者不必自己记账。想提前取消就持有它并 `Dispose()`。

**异常隔离**：`AnchorHub` 逐个 handler 兜异常 —— 一个补丁抛错不影响其余补丁，更不崩主程序（记进宿主日志）。但别拿它当控制流：该 `try` 的地方自己 `try`。

## 3. 加一个补丁

1. 在 `Patches/<你的功能>/` 下新建一个类：

```csharp
using TuneLab.ExperimentalPatch.Abstractions;

namespace TuneLab.ExperimentalPatch.Patches.MyFeature;

[Patch(Order = 0)]                 // 顺序敏感时给 Order；同序按 Id 字典序，装配顺序永远一致
public sealed class MyPatch : IPatch
{
    public string Id => "my-feature.thing";   // 稳定、唯一、能反查

    public void Attach(IAnchorTable anchors)
    {
        anchors.OnAppReady(a => a.Host.Log("my feature is live"));
    }

    public void Detach() { }
}
```

2. 完事。**没有注册表要改**——`PatchRegistry` 扫的正是本程序集里带 `[Patch]` 的 `IPatch` 实现。

- **停用**：把 `[Patch]` 摘掉，或写 `[Patch(Enabled = false)]`。不必删代码。
- **删除**：删掉那个文件/目录即可。
- **不要**在 `Patches/` 里写跨补丁的共享代码——那会变成新的隐性耦合点。共享的东西属于 `Abstractions/`（若它是契约）或 `FunctionHooker/`（若它是机制）。

## 4. 加一个锚点

锚点是**双方共享的契约**，所以故意让它「加得有点响」——加一个方法，宿主与全部补丁都会收到编译期提示，不会悄悄漂移。

**通知型**三步：

1. **契约**：`Abstractions/Anchors/` 加 payload `record`，`AnchorIds` 加 id，`IAnchorTable` 加订阅方法。
2. **耦合层**：`AnchorHub` 加 handler 清单 + `RaiseXxx`；`PatchHost` 暴露 `RaiseXxx()`。
3. **宿主接一处**：在真正的注入点加**一行** `ExperimentalPatchBootstrap.Xxx(...)`（或经 `HostServices` 递出宿主能力）。同时**把新锚点登记到本文件 §2 的表里**。

**贡献型**三步：

1. **契约**：在 `Abstractions/` 定义被贡献对象的接口（如 `IStartupScreen`），`IAnchorTable` 加 `ProvideXxx(Func<T> factory)`。
2. **耦合层**：`AnchorHub` 存工厂清单 + `CreateXxx()`（第一个非 null 胜出、逐个兜异常）；`PatchHost` 暴露 `CreateXxx()`。
3. **宿主接一处**：调 `ExperimentalPatchBootstrap.CreateXxx()`，**并且必须处理好"没有"**（`null` 时走与上游逐行一致的原样路径）。

payload 需要宿主能力时：优先在 `IHostServices` 上加一个**语义化**成员（如 `UiLanguage`、`OpenFolder(string)`），而不是把宿主的某个管理器整个漏出去。漏出去多少，上游改动就会波及多少。

> **按需设锚点**：不要预先把「可能有用」的钩子都挂上。补丁功能开发到需要进宿主内部时，再按上面三步开这一个锚点。锚点越少，上游合并越轻。

## 5. 冲突最小化纪律

| 类别 | 规则 |
|---|---|
| 宿主已有文件 | **只允许** §0 表格里那六处。其余一律不碰。 |
| 宿主侧适配器 | 需要宿主 `internal` 类型时，**新增** `TuneLab/ExperimentalPatch<功能>.cs`（上游没有同名文件 → 零冲突）。别把这些代码塞进 `ExperimentalPatchBootstrap.cs`。 |
| `TuneLab/ExperimentalPatchBootstrap.cs` | 属于「缝」，**尽量只增不改**；逻辑写到这里面就等于写回了上游文件。 |
| `TuneLab/App.axaml.cs` 的启动段 | 已被 `app.splash` 整段搬进 `ContinueStartup`（含音频设备）。**搬移时逐字保留上游原文**（只加相位标记），上游改这段时的对齐成本才最低。 |
| 上游 `AGENTS.md` / `docs/` | 不改（改了就是新增冲突面）。本层文档一律放 `ExperimentalPatch/` 内。 |
| 命令 / 动作 / 设置 | 需要补丁能新增这些时，**先加锚点**再在补丁里注册，不要直接把补丁代码写进宿主的注册表。 |
| 上游 `TuneLab.sln` | 只多了 `ExperimentalPatch` 一条工程项；上游改 sln 时这一条极易手工保留。 |

**无补丁 = 逐行等价**：这是本层的底线，`app.splash` 也不例外——没有补丁提供闪屏时，宿主仍走原来的同步路径（`ContinueStartup` 里所有 `await` 都走早返回）。改动宿主结构后，必须确认这条仍成立。

## 6. 调试

- 补丁日志经 `IHostServices` 汇入宿主日志流，前缀 `[ExperimentalPatch]`。
- 装配结果：`PatchHost.LoadedPatches` / `PatchHost.FailedPatches`。
- 日志里能直接看到：`patch host booted: N loaded, M failed.` 与逐个 `patch attached: <id>`。
- 补丁抛出的一切异常都带锚点 id：`patch handler for anchor 'app.ready' threw`。
- 补丁层**永远不会**把异常抛回宿主：最坏情况是「这次补丁不生效」，主程序照常启动。

**跑起来看**：`tools/ScreenshotBot` 与 `TuneLab.Cli` 都**不**装配补丁层（只有 `TuneLab/Program.cs` 调
`ExperimentalPatchBootstrap.Start`），所以它们不会弹闪屏、截图不受影响。要单独看补丁层的行为，
可照 `host-seams.md` 里的模板写一个小 harness：`PatchHost.Boot(你的 IHostServices, [])` → `CreateXxx()` →
headless 渲染。这条链路能覆盖契约、注册与工厂调用时机。

## 7. 与上游同步

```powershell
git fetch upstream
git checkout expermental
git merge upstream/master      # 或 rebase
```

预期冲突面（全部在 §0 表格的六个位置）：`TuneLab.sln`、`TuneLab.csproj`、`Program.cs`、`App.axaml.cs`
（含被搬到 `ContinueStartup` 的启动慢段）、`ExperimentalPatchBootstrap.cs`（后者除非上游新动，否则不冲突）。
`ExperimentalPatch/` 整体与上游无关，不会产生冲突。

合并后**必做**：

1. 确认 §0 的接缝还在（`CreateStartupScreen`/`Ready`/`Stop` 三处调用）。
2. 确认上游若重写了启动慢段，`ContinueStartup` 里的对应内容要跟着对齐（搬迁时加了哪些相位标记见 `startup-timeline.md`）。
3. 跑一遍"无补丁 = 逐行等价"的确认（见 §5）。

## 8. 当前范围（有意为之）

- **只有桌面宿主会装配补丁**（`Program.Main`）。生命周期锚点都是桌面窗口的生命周期，无头宿主
  （`TuneLab/Headless/HeadlessHost.cs`）与截图工具不触发它们，故不装配。
- 将来若确实需要无头下跑补丁：在 `HeadlessHost.RunOnThread` 的 `ExtensionManager.LoadExtensions()`
  之后加一行 `ExperimentalPatchBootstrap.Start([])`、并在 `Teardown` 里加一行
  `ExperimentalPatchBootstrap.Stop()`，然后**删掉本节第一条**。
- `PatchHost.Shutdown()` 之后可以再 `Boot()`（状态会重置）；`Boot()` 重复调用（中间没有 `Shutdown`）
  会被拒绝并打 warning。

## 9. 相关文档

| 文档 | 什么时候看 |
|---|---|
| `AGENTS.md`（本文） | 第一次接触本层；想知道"规矩是什么"。 |
| `host-seams.md` | **要往宿主里插手**：每一处接缝的确切位置、模板、以及"怎么装一个补丁"。 |
| `startup-timeline.md` | 碰启动时序（闪屏、初始化、窗口创建）：硬顺序约束与实测依据。 |
