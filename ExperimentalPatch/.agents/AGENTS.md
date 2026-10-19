# ExperimentalPatch — 补丁层约定

> 本目录是**实验性补丁层**：上游（`LiuYunPlayer/TuneLab`）功能之外的东西都写在这里，经**锚点**接进宿主。
> 目标只有一个：**上游更新时，冲突面小到可以手工合并**。所有设计取舍都服从这一条。

## 0. 一句话架构

```
宿主 TuneLab  ──ProjectReference──▶  ExperimentalPatch
      │                                    │
      │  只认三个调用点 + IHostServices      ├─ Abstractions/   契约（双方共享的唯一界面）
      └───────────────────────────────────▶├─ FunctionHooker/ 耦合层（宿主唯一的入口）
                                           └─ Patches/        补丁实现（加/删只动这里）
```

宿主对补丁层的认知被压到**四个静态方法 + 一份 `IHostServices`**：

| 宿主文件 | 位置 | 干什么 |
|---|---|---|
| `TuneLab/Program.cs` | `InitCoreServices()` 之后 | `ExperimentalPatchBootstrap.Start(args)` → 装配补丁 + 触发 `app.startup` |
| `TuneLab/App.axaml.cs` | `HandleStartupArgs(args[1..])` 之后 | `ExperimentalPatchBootstrap.Ready()` → 触发 `app.ready` |
| `TuneLab/App.axaml.cs` | `desktop.Exit` 首行 | `ExperimentalPatchBootstrap.Stop()` → 触发 `app.shutdown` + 卸载补丁 |
| `TuneLab/ExperimentalPatchBootstrap.cs` | — | `IHostServices` 实现 + 异常兜底 |
| `TuneLab/TuneLab.csproj` + `TuneLab.sln` | — | 一个 `ProjectReference` + 一条工程项 |

除以上五处，**宿主其余文件一律不改**。

## 1. 目录职责

| 路径 | 职责 | 谁改 |
|---|---|---|
| `Abstractions/` | 契约：`IPatch` / `IAnchorTable` / 锚点 payload / `IHostServices`。**补丁能看到宿主的全部**就是这里。 | 加锚点时改 |
| `FunctionHooker/` | 耦合层：`PatchHost`（宿主唯一入口）、`PatchRegistry`（反射发现）、`AnchorHub`（锚点分发 + 异常隔离 + 订阅回收）。 | 极少改 |
| `Patches/<功能>/` | 补丁实现，一个补丁一个文件。 | 日常改这里 |
| `.agents/` | 本约定。 | 约定变了才改 |

**依赖方向是单向的**：`Patches` → `Abstractions`；`FunctionHooker` → `Abstractions`。**没有任何东西依赖宿主程序集**——所以上游怎么重构，补丁都不会编译失败（只会在锚点语义变了时运行期失配，而那是可见的）。

## 2. 锚点（anchor）

**锚点 = 宿主在某个确定时机/位置向补丁提供的接驳点。** 它由三件东西组成：

1. **id**（`AnchorIds` 里的常量，如 `app.startup`）：只用于日志与诊断，**改实现不改 id**。
2. **payload**（`Anchors/*.cs` 里的 `record`）：宿主递给补丁的数据。用 `record` 而非接口/参数列表，是为了将来加字段不破坏既有补丁的编译。
3. **`IAnchorTable` 上的一个方法**：补丁经它订阅。

### 两条硬规矩

- **payload 里只放契约类型**（BCL + `Abstractions` 里的类型）。放宿主内部类型 = 补丁被迫依赖宿主 = 本层存在的意义归零。
- **payload 用 `record`，加字段不改构造调用**：确实要加信息时，给已有 `record` 加一个带默认值的属性，别改位置参数的个数/顺序。

### 现有锚点

| 锚点 id | 触发时机 | payload | 适合干什么 |
|---|---|---|---|
| `app.startup` | 配置/翻译/日志就绪后，Avalonia 起来**之前** | `AppStartupAnchor`（含命令行参数） | 进程级准备：读自己的配置、装全局钩子。**别碰 UI**。 |
| `app.ready` | 主窗口已建、启动参数已分流（工程已打开） | `AppReadyAnchor` | 需要窗口/编辑器的注入。 |
| `app.shutdown` | 退出时，宿主引擎销毁**之前** | `AppShutdownAnchor` | 收尾。宿主服务此时仍可用；要快，别阻塞退出。 |

触发顺序固定为 `app.startup` → `app.ready` → `app.shutdown`，各**恰好一次**。

### 订阅与回收

订阅一律返回 `IDisposable`：`FunctionHooker` 在 `Detach`/退出时统一回收，补丁作者不必自己记账。想提前取消就持有它并 `Dispose()`。

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

锚点是**双方共享的契约**，所以故意让它「加得有点响」——加一个方法，宿主与全部补丁都会收到编译期提示，不会悄悄漂移。三步：

1. **契约**：在 `Abstractions/Anchors/` 下加 payload `record`，在 `AnchorIds` 加 id，在 `IAnchorTable` 加订阅方法。
2. **耦合层**：在 `FunctionHooker/AnchorHub.cs` 加 handler 清单 + `RaiseXxx`，在 `PatchHost` 暴露对应的 `RaiseXxx()`。
3. **宿主接一处**：在真正的注入点加**一行** `ExperimentalPatchBootstrap.Xxx(...)`（或经 `HostServices` 递出宿主能力）——这一行就是宿主代码里为本次能力付出的全部改动。同时**把新锚点登记到本文件 §2 的表里**。

payload 需要宿主能力时：优先在 `IHostServices` 上加一个**语义化**成员（如 `OpenFolder(string)`），而不是把宿主的某个管理器整个漏出去。漏出去多少，上游改动就会波及多少。

> **按需设锚点**：不要预先把「可能有用」的钩子都挂上。补丁功能开发到需要进宿主内部时，再按上面三步开这一个锚点。锚点越少，上游合并越轻。

## 5. 冲突最小化纪律

| 类别 | 规则 |
|---|---|
| 宿主已有文件 | **只允许**上面 §0 表格里那五处。其余一律不碰。 |
| `TuneLab/ExperimentalPatchBootstrap.cs` | 属于「缝」，**尽量只增不改**；逻辑写到这里面就等于写回了上游文件。 |
| 上游 `AGENTS.md` / `docs/` | 不改（改了就是新增冲突面）。本层文档一律放 `ExperimentalPatch/` 内。 |
| 命令 / 动作 / 设置 | 需要补丁能新增这些时，**先加锚点**再在补丁里注册，不要直接把补丁代码写进宿主的注册表。 |
| 上游 `TuneLab.sln` | 只多了 `ExperimentalPatch` 一条工程项；上游改 sln 时这一条极易手工保留。 |

## 6. 调试

- 补丁日志经 `IHostServices` 汇入宿主日志流，前缀 `[ExperimentalPatch]`。
- 装配结果：`PatchHost.LoadedPatches` / `PatchHost.FailedPatches`。
- 日志里能直接看到：`patch host booted: N loaded, M failed.` 与逐个 `patch attached: <id>`。
- 补丁抛出的一切异常都带锚点 id：`patch handler for anchor 'app.ready' threw`。
- 补丁层**永远不会**把异常抛回宿主：最坏情况是「这次补丁不生效」，主程序照常启动。

## 7. 与上游同步

```powershell
git fetch upstream
git checkout expermental
git merge upstream/master      # 或 rebase
```

预期冲突面（全部在 §0 表格的五个位置）：`TuneLab.sln`、`TuneLab.csproj`、`Program.cs`、`App.axaml.cs`、`ExperimentalPatchBootstrap.cs`（后者除非上游新动，否则不冲突）。
`ExperimentalPatch/` 整体与上游无关，不会产生冲突。

## 8. 当前范围（有意为之）

- **只有桌面宿主会装配补丁**（`Program.Main`）。三个生命周期锚点都是桌面窗口的生命周期，无头宿主
  （`TuneLab/Headless/HeadlessHost.cs`）与截图工具不触发它们，故不装配。
- 将来若确实需要无头下跑补丁：在 `HeadlessHost.RunOnThread` 的 `ExtensionManager.LoadExtensions()`
  之后加一行 `ExperimentalPatchBootstrap.Start([])`、并在 `Teardown` 里加一行
  `ExperimentalPatchBootstrap.Stop()`，然后**删掉本节第一条**。
- `PatchHost.Shutdown()` 之后可以再 `Boot()`（状态会重置）；`Boot()` 重复调用（中间没有 `Shutdown`）
  会被拒绝并打 warning。
