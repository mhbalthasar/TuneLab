# VST3 崩溃隔离：把 VST3 也接到桥进程

> 2026-10-30 方案稿。目标是把 VST3 的加载 / 处理 / 状态 / 编辑器全部搬到已有的 x64 桥进程，
> 让插件**崩**或**挂**都只死桥，宿主继续活着。VST2 已经走这条路（DESIGN §5.3），
> 这里只处理"VST3 也要走同一套"的增量。

## 0. 为什么必做

2026-10-30 的真插件验收（[NOTES-vst3-vendor.md](NOTES-vst3-vendor.md)）：

- 21 个 64 位 VST3 通过"加载 → 出声 → 存读 state"；
- 4 个 chowdsp 插件**挂起**：CHOWTapeModel / ChowMatrix 卡在 `Create`，
  BYOD / ChowCentaur 卡在第二个实例；
- 历史上 Blue Cat 调制类还有 native abort（本轮未复现，但 native abort 兜不住这件事没变）。

进程内的两个后果：

- 崩：整个 TuneLab 消失；
- 挂：UI 线程被 native 调用占死，软件永远卡住，连"忽略/卸载"都点不到。
  托管侧无法中止一条卡在 native 里的调用。

VST2 已经证明桥进程是可行形态；VST3 只是同一套机制接一个新后端。

## 1. 边界

**做**：

- VST3 的 load / initialize / process / state / parameters / editor 全部过桥；
- 桥侧的 load / initialize / teardown / open-editor 都带超时，超时即杀桥；
- 桥崩 / 超时后：该插入 **bypass + 标红**，工程不丢槽位、不静默静音；
- 复用现有的 x64 桥进程与共享内存音频环；所有 VST3 插入共用一个桥，不每插件起一个。

**不做（本轮）**：

- 32 位 VST3（vendored native 目前只有 win-x64）；
- VST3 标准 `kSidechain` 总线（要 fork native，另立一项）；
- VST3 参数自动化。

## 2. 现状

```
TuneLab (x64)                          VstBridge.x64.exe
┌────────────────────────┐             ┌────────────────────────┐
│ Vst2Insert ─ VstBridgeClient ◀─pipe─▶│ BridgeSession           │
│ Vst3Insert ─ OwnVst3Wrapper（进程内） │ └ PluginHost（VST2）    │
│ Vst3EditorShell          │  共享内存   │    Process / Editor     │
└────────────────────────┘             └────────────────────────┘
```

要变成：

```
TuneLab (x64)                          VstBridge.x64.exe
┌────────────────────────┐             ┌────────────────────────┐
│ Vst2Insert ─┐          │             │ BridgeSession           │
│ Vst3BridgeInsert ─ VstBridgeClient ◀─pipe─▶├ Vst2PluginHost    │
│ IVst3Insert │          │  共享内存    │ └ Vst3PluginHost        │
└─────────────┴──────────┘             └────────────────────────┘
```

关键点：桥已经从"只为 VST2 存在"变成"VST 宿主进程"，接口按格式分派。

## 3. 协议改动

在 `VstBridge/BridgeProtocol.cs`（两端各编译一份，改一处两端一起编译失败）：

- `LoadPluginMessage` 增加 `format`（`"vst2"` / `"vst3"`，缺省 `"vst2"` 兼容旧调用）。
- `HelloMessage.Formats` 由 x64 桥声明为 `["vst2","vst3"]`；x86 桥仍是 `["vst2"]`
  （它没有 x64 的 native dll）。
- `LoadPluginResultMessage` 增加 `hasEditor` 已有；补 `needsOpenGl`（VST3 的
  NanoVG 判据，宿主界面据此决定"开 GUI 还是参数面板"）。
- `OpenEditorResultMessage` 增加 `windowHandle`（VST3 编辑器在桥进程里自己开顶层窗，
  宿主需要句柄才能 `SetForegroundWindow`）。
- `ParameterInfo` 增加 `defaultValue`（`VST3Parameter.DefaultValue`，参数面板要用）。
- **`BridgeProtocol.ProtocolVersion` 2 → 3**：字段语义与格式分派都变了。旧桥会在 Hello
  被拒，由 `tools/publish-bridges.ps1` 重新发布解决——这是既有纪律。

音频仍走共享内存，指令仍走 JSON；VST3 的 state 可能到 270 KB（ChowMultiTool），
base64 后 ~365 KB，远低于 16 MB 的帧上限，先用现有通道。

## 4. 桥侧

### 4.1 主机抽象

给 `BridgeSession` 一个按槽位分派的主机接口，而不是把 VST3 塞进 `PluginHost`：

```csharp
interface IBridgePluginHost
{
    (bool Ok, string? Reason, ...) Load(LoadPluginMessage request, out int inCh, out int outCh);
    bool Unload(int slot);
    bool Process(int slot, bool bypass, out int frames, out int channels);
    byte[]? GetState(int slot);
    bool SetState(int slot, string base64);
    List<ParameterInfo>? GetParameters(int slot);
    void SetParameter(int slot, long id, double value);
    (bool Ok, string? Reason, int ChildWindows, IntPtr Window) OpenEditor(int slot, string title);
    bool CloseEditor(int slot);
    void Attach(SharedAudioRing input, SharedAudioRing output);
}
```

- `Vst2PluginHost`：现有 `PluginHost` 加一层薄适配，逻辑一行不改。
- `Vst3PluginHost`：新文件，内部用 vendored `OwnVst3Wrapper`。
- `BridgeSession` 在 `Load` 时按 `format` 选主机、把 `slot → 主机` 记进字典；
  其余消息按 slot 查表转发。字典只在桥的主线程读写，不需要锁。

### 4.2 源码与 native 的归属

- `Vst3PluginHost.cs` 放 `VstBridge/` 目录（两端各自编译一份）。
- x64 桥把 `InsertHost/Vendored/*.cs` 也 `Compile Include` 进来，复用同一份降级 wrapper
  （不引用 `InsertHost.csproj`，避免把 `MixerEngine` 拖进桥）。
- x64 桥把 `InsertHost/Native/win-x64/ownvst3.tlhost.dll` 复制到输出目录；
  x86 桥不复制（它不声明 `vst3`）。
- 发布脚本 `tools/publish-bridges.ps1` 已有两个 RID 的构；native dll 随 x64 桥走。

### 4.3 VST3 主机实现

- `Load`：`new OwnVst3Wrapper(nativePath)` → `LoadPlugin(locator)` → `Initialize(rate, block)`，
  读 `ActualInputChannels` / `ActualOutputChannels` / `LatencySamples` / `Name` / `Vendor` /
  `HasEditor` / `NeedsOpenGl`；乐器照旧拒绝。全部包 try/catch，失败回 `LoadPluginResult`。
- `Process`：把共享环里的平面（最多 4 条）直接喂 `wrapper.ProcessAudio`，
  输出写回共享环。实时路径不分配。
- `GetState` / `SetState` / `GetParameters` / `SetParameter`：直接转 wrapper。
- `OpenEditor`：桥侧起一条 STA 线程，创建插件自己那扇顶层窗并跑 Win32 消息循环，
  同时周期性 `SafeDispatchMessage()` / `ProcessIdle()`；把新窗口的 HWND 回给宿主。
  GL 不足且 `needsOpenGl` 时不建窗，回 `Ok=false`，宿主退回参数面板。
  形状参考现有 `Vst2EditorWindow`，只是 VST3 的窗口由插件自己建，我们只负责泵消息。
- `CloseEditor` / `Unload`：先关编辑器、Join 编辑器线程（带超时），再销毁 wrapper。

### 4.4 超时与杀进程

桥的每条慢路径都要有上限，超时即失败：

| 路径 | 上限 | 超时后 |
|---|---|---|
| load + initialize | 20 s | 杀桥；宿主该槽位标红 + bypass |
| open editor | 10 s | 杀桥；退回参数面板 |
| unload / close editor | 3 s | 不再等，直接杀桥 |
| process | 现有 `BlockWaitMs = 20` | 该块直通，不杀桥；连续失败再杀 |

桥崩 / 被杀后 `VstBridgeClient` 检测管道 EOF 与进程退出，把该桥的所有槽位标失败。
宿主 `InsertInstanceFactory` 收到失败：旁通该插入、界面标红、提示可重新加载。

## 5. 宿主侧

### 5.1 抽一个 `IVst3Insert`

`Vst3Insert` 现在是 sealed 具体类，宿主几十处 `is Vst3Insert`。抽接口，两种实现：

```csharp
interface IVst3Insert : IMixInsert
{
    bool HasEditor { get; }
    bool NeedsOpenGl { get; }
    (int Width, int Height)? EditorSize { get; }
    IReadOnlyList<VST3Parameter> Parameters { get; }
    double GetParameter(int id);
    void SetParameter(int id, double value);
    void FlushParameterChanges();
    byte[]? SaveState();
    bool LoadState(byte[] data);
    // 编辑器：进程内由本类泵；桥模式由桥泵，这里 no-op
    bool OpenEditor(IntPtr parent);
    bool IsEditorOpen { get; }
    void PumpEditorMessages();
    void CloseEditor();
    void ResizeEditor(int width, int height);
}
```

- 现有 `Vst3Insert` 实现接口（进程内路径，行为不变）；
- 新 `Vst3BridgeInsert` 实现接口，内部持有 `VstBridgeClient` + slot；
- 宿主里 `is Vst3Insert` 改成 `is IVst3Insert` 或 `IVst3Insert` 变量。
  `Vst3EditorShell` 对桥模式要换成"用桥回传的 HWND 置前 + 不再本进程泵"。

### 5.2 后端选择

`Vst3Backend.Create`：

- 新增设置 `Settings.IsolateVst3`（本机设置）。**P1 默认 false**（进程内，线上行为不变）；
  P3 才改成"Windows 且 x64 桥可用时 true"，其它平台继续进程内；
- 选桥模式：`VstBridgeClient.ForBitness(64)` → `EnsureStartedBlocking` →
  `LoadBlocking(..., format: "vst3")` → `Vst3BridgeInsert`；
- 桥不可用 / 启动失败：回退进程内 `Vst3Insert`，并把原因写日志（不静默）。
  注意：回退意味着失去隔离，界面上要能看出"这个实例没被隔离"。

### 5.3 编辑器壳

`Vst3EditorShell` 现在有三件事：开、泵、关。桥模式下：

- 开：`OpenEditor` 等桥回复，成功后拿 `windowHandle`；
- 泵：桥自己泵，宿主定时器改成"用返回的 HWND 做置前 + 查桥侧 `IsEditorOpen`"，
  不再调 `PumpEditorMessages`；
- 关：`CloseEditor` 发消息；桥侧 Join 编辑器线程。
- GL：桥侧判断，宿主按 `needsOpenGl` + 桥回复决定退参数面板。

## 6. 分阶段落地

| 阶段 | 内容 | 验收 |
|---|---|---|
| P1 | 协议 format / 版本 3、`IBridgePluginHost` 路由、x64 桥的 `Vst3PluginHost`（音频 + state + 参数，**不含编辑器**）、`IVst3Insert` + `Vst3BridgeInsert`、设置开关（默认关） | 真插件：出声、state 存读、参数读写、latency 正确；桥被杀后宿主存活、插入 bypass |
| P2 | 桥侧编辑器线程 + HWND 回传 + GL 判据；宿主 `Vst3EditorShell` 适配 | 打开/关闭插件 GUI；杀掉编辑器线程不带走宿主 |
| P3 | 默认打开（Windows x64）；桥不可用时回退进程内并提示；文档 / 用户手册更新 | 真机回归；老工程（进程内挂的 VST3）打开后仍是同样声音 |
| P4 | 挂起测试：造一个会卡住的假后端 / 插件，验证 load 20s 超时后杀桥、宿主恢复（超时机制已在 P1 落地，剩端到端验证） | 宿主不冻结 |
| P5 | 跨平台隔离（linux / macOS）+ 架构扩展；VST3 锁 64 位、放弃 32 位 VST3（VST2 维持 Windows-only）。设计见 §9 | §9.10 的隔离验证清单在目标矩阵上全过 |

## 7. 测试

- 协议：`BridgeProtocol` 的 format 分派、版本校验、`ParameterInfo.DefaultValue` 往返；
- 桥主机：`Vst3PluginHost` 用假 wrapper（不加载真插件）覆盖 load/process/state/params；
- 宿主：`Vst3BridgeInsert` 用假的 `IBridgeChannel`（`VstBridgeClient` 的簿记子集）
  覆盖音频往返、旁通、桥失败直通、state 回灌、参数往返；
- 真插件集成（opt-in，独立进程）：复用 2026-10-30 的探针，加"桥模式"跑一遍；
- 回归：`TuneLab.Tests` 与 legacy 全绿；进程内 VST3 路径一条不删。

## 8. 风险

| 风险 | 应对 |
|---|---|
| native dll 随桥部署漏拷 | 发布脚本 + 启动时 `NativePath` 检查；桥声明 `formats` 前先确认 dll 在 |
| 协议 v3 与旧桥不兼容 | Hello 校验版本；旧桥直接拒；发布脚本重发两个桥 |
| 编辑器窗口归桥，桥重启后窗口消失 | 明确"桥崩 = 编辑器关、插入 bypass"；UI 提示重新加载 |
| GL 在桥里 | 复用 `OpenGlAvailability`；桥侧判 `needsOpenGl`，不够就退参数面板 |
| 大 state | 现有 base64 帧上限 16 MB；ChowMultiTool 274 KB 已实测可过 |
| 进程内回退变成静默降级 | 回退必须写日志 + 界面标示"未隔离" |

## 9. 跨平台隔离设计（linux / macOS）

> 2026-10-31 设计稿（P5 的输入）。目标是把"VST3 崩溃隔离"从 Windows x64 扩展到
> linux / macOS，并覆盖用户要求的架构集合。**本章不改 P1–P4 已落地的 Windows 路径**，
> 而是给出等价的跨平台机制与落地清单。

### 9.1 范围与目标平台

- **VST2 保持 Windows-only**（既有决策，`Vst2Backend.IsAvailable => OperatingSystem.IsWindows()`）。
  非 Windows **不提供 VST2**，本章不为它做任何事。
- **VST3 锁 64 位**：放弃 32 位 VST3（上游 ownvst3 不构建 x86，且 32 位 VST3 插件已边缘化）。
  目标矩阵：

  | OS \ 架构 | x64 | arm64 |
  |---|---|---|
  | Windows | ✅ 已有（P1–P4） | ➕ 新增 |
  | Linux | ➕ 新增 | ➕ 新增 |
  | macOS | ➕ 新增 | ➕ 新增（CI 已在构建 host） |

- 隔离覆盖面与 Windows 对齐：load / initialize / process / state / parameters / unload 的
  **进程隔离与生命周期**（宿主怎么死都不留孤儿；桥崩/卡宿主照常活、可重建、可标红）。
- **不在本章**：编辑器 GUI 的跨平台消息泵（macOS Cocoa runloop / Linux X11-Wayland）。
  那是独立工作项；本章的桥在无编辑器能力时与 P1 一样**退回参数面板**。
- **暂缓**：loongarch64（见 §9.12）。

### 9.2 现状盘点：哪些已天然跨平台，哪些是 Windows 专属

| 机制 | 现位置 | Windows 专属？ |
|---|---|---|
| 控制通道 | `BridgeProgram` 的 `NamedPipeServerStream` | 否（.NET 在 Unix 上映射 Unix domain socket） |
| 音频环 | `BridgeSharedMemory.SharedAudioRing` | 否（纯 `MemoryMappedFile` + `Volatile` + 裸指针 SPSC） |
| 命名事件 gate | `EventWaitHandle(name)` | 否（Unix 上文件支撑，比 Windows 命名事件慢一档） |
| 命名共享段 | `MemoryMappedFile.CreateNew(name, …)` | 否（Unix 上其实是 temp 目录里一个文件） |
| **孤儿防止** | `JobObject` + `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` | **是**（`VstBridgeClient` 行 138/198/956、`BridgeProgram` 行 14） |
| 桥 exe 选择 | `VstBridgeClient.FindBridgeExecutable` 写死 `win-x86` / `win-x64`（行 743） | **是** |
| 编辑器 | `Vst3EditorWindow` / `Vst3Insert` 的 user32 | **是** |
| 后端闸门 | `Vst3BridgeInsert.Create`（行 331）、`Vst3PluginHost.FindNativeLibrary`（行 36）要求 `IsWindows && Is64BitProcess` | **是** |

结论：**真正要重做的只有"孤儿防止"这一条内核机制，加上若干接线**；音频通道、控制通道、
无锁环的逻辑本身已经跨平台。

### 9.3 Windows 的保证，拆成四条

JobObject 给的其实不止一件事。把它拆开，逐条找 Unix 对应物：

1. **宿主死（正常 / 崩溃 / 被强杀）→ 桥死**，不留孤儿。
2. **桥卡死 / 崩溃 → 宿主存活**，能杀掉整棵树、能重建、能标红。
3. **无残留**：不留下游离进程、句柄、共享内存或临时文件。
4. **音频路径零额外开销**。

下面 §9.4–§9.7 逐条给方案。

### 9.4 保证 1：宿主死 → 桥死

Unix **没有**内核级"父死子死"。用两个手段合成，跨 Linux + macOS 都成立：

**A1. 存活管道（liveness pipe）——便携主力**

- 宿主启动桥**之前**建一根管道：**读端交给桥，写端只有宿主持有**，务必 `O_CLOEXEC`
  （否则插件或桥的其它子进程继承了写端，EOF 永不触发）。
- 桥里起一个**专职看门狗线程**，阻塞 `read()` 这根管道。宿主进程一死，写端句柄被内核回收
  → `read()` 返回 0（EOF）→ 看门狗立刻 `kill(-pgid, SIGKILL)` 杀掉自己的整个进程组后退出。
- **为什么不用"监听控制管道断开"**：插件卡死时桥的主循环读不到控制管道，但看门狗线程独立，
  照样会醒。这是关键。
- 对"宿主被 SIGKILL"同样有效：进程一死句柄必被回收，EOF 是确定性的，**无 PID 复用竞态**。

**A2. `prctl(PR_SET_PDEATHSIG, SIGKILL)`——Linux 上的加速路径（可选）**

- 桥（Linux 限定）启动时向内核登记"父线程一死就 SIGKILL 我"。内核级、零延迟，作为 A1 之前的第一道闸。
- **两个坑必须处理**：
  1. 它触发于**创建它的那个线程**死亡，不是整个进程死亡 —— .NET 的 `Process.Start` 可能在线程池
     线程上 fork，所以要固定承载线程，或在设置后再校验 `getppid()`；
  2. 只管**直接子进程**，孙进程不管 —— 靠进程组（§9.5）补齐。
- **macOS 没有这个**，所以 A2 只是 Linux 的锦上添花，**A1 才是全平台保底**。

### 9.5 保证 2：桥卡/崩 → 宿主存活 + 杀掉整棵树

- **进程组化**：桥启动时 `setpgid(0, 0)` 自成一个进程组（Windows 侧继续用 JobObject）。
- 宿主超时后用 `kill(-pgid, SIGKILL)` 一次干掉桥**和它可能拉起的任何孙进程**。
- 保留 `Process.Kill(entireProcessTree: true)` 作为兜底（Linux 走 `/proc/*/stat` 的 PPID 链，
  macOS 走 `sysctl`），但**进程组为主用**——它不受"孙进程恰好被 reparent"影响。
- 桥退出/被杀后，宿主侧 `Process.Exited`（跨平台）或既有超时逻辑照旧标红、重建。

### 9.6 保证 3：共享内存 / 临时文件不残留

- Windows：命名段最后一个句柄关闭即自动消失，天然无残留。
- **Unix 实测修正（P5.1，2026-10-31）**：.NET 在 Unix 上**既不支持命名 `MemoryMappedFile`**
  （`CreateNew(name)` 直接抛 "Named maps are not supported."），**也不支持命名 `EventWaitHandle`**
  （抛 "The named version of this synchronization primitive is not supported on this platform."）。
  设计稿原以为它们都是"temp 目录里一个文件"。实际做法：
  - 共享段改用**宿主私有目录里的文件**（`Path.GetTempPath()/tunelab-vstbridge/<段名>`），两端交换
    段名、各自映射到同一路径；创建者（宿主）`Dispose` 时显式删文件。
  - **gate 在 Unix 上先不装**：消费者退化成自旋 + `Thread.Yield()`（`WaitUntil` 既有兜底）——
    正确，只是 b2h 那条实时路多烧一点 CPU。真正的 eventfd / 自管道门铃留待 §9.7 的后续项。
  - 控制用的 UDS socket 文件、以及上面的段文件，启动清扫兜底（见上一条的后半）。
- **Linux 可选的更干净方案**：`memfd_create` + 通过控制 socket `SCM_RIGHTS` 传 fd —— 匿名内存、
  进程死即回收、零残留、零竞态。但 **macOS 没有 memfd**，所以跨平台保底仍是
  "temp 文件 + 显式 unlink + 启动清扫"。

### 9.7 保证 4：效率

- **音频路径完全不变**：还是 `SharedAudioRing` 那段共享内存无锁环。`Volatile` 在 ARM64 上正确
  发屏障（该文件注释已确认），x64/arm64 都零额外拷贝、零 IPC。**没有效率损失。**
- **gate 的唤醒（Unix）**：命名 `EventWaitHandle` 在 .NET 的 Unix 上**不可用**（见 §9.6 实测修正），
  故 P5.1 版先退化成自旋；好在这条只有 b2h（桥→宿主）用 gate，h2b 本就纯自旋，影响面有限。
  若实测 CPU 有影响，换成**自管道写一个字节 / `eventfd`（Linux）**即可 ——
  本质都是"发布时给消费端一个可睡眠的唤醒"，开销可忽略。
- **A2 `prctl` 本版没做**（见 §9.4 的创建线程语义坑）：先只上 A1（存活管道）——它是全平台保底，
  且已实测生效（`BridgeIsolationTests.BridgeDiesWhenTheLivenessPipeCloses`）。
- b2h（桥→宿主）那条才用 gate；h2b 本就是纯自旋，影响面很小。

### 9.8 与 Windows 的差异（诚实版）

| 维度 | Windows (JobObject) | Unix 方案 | 实际等效度 |
|---|---|---|---|
| 宿主崩溃 → 桥死 | 内核级、即时 | 存活管道 EOF → 看门狗自杀 | **等效**（都确定、无竞态） |
| 宿主被强杀（SIGKILL / 任务管理器） | 生效 | 生效（EOF） | **等效** |
| 杀整棵进程树 | 内核按 job 跟踪 | 进程组 `kill(-pgid)` | 近似（插件 `setsid` 逃亡才漏；VST3 是 in-proc dll，几乎不存在） |
| 桥陷入不可中断内核等待（D 状态） | 杀不动，要等它出来 | 同样杀不动 | **打平** —— JobObject 也救不了 D 状态 |
| 音频速度 | 零拷贝共享环 | 零拷贝共享环 | **等效** |

**要接受的两点**：
1. Unix 多一个**用户态看门狗线程**（每桥一个，成本可忽略），而非纯内核机制；因此
   "宿主死 → 桥死"最短路径比 Windows 略长一档（微秒 → 毫秒级），但**结果一致、不产生残留**。
2. macOS 拿不到 PDEATHSIG，只有存活管道；Linux 两条都有。

### 9.9 每 RID 产物清单

隔离要在某平台上可用，该 RID 必须同时具备：

| 产物 | 现位置 | 说明 |
|---|---|---|
| 桥 exe | `bridge/<rid>/TuneLab.ExperimentalPatch.VstBridge[.x64].exe` | 每 RID 各发一份（现 `publish-bridges.ps1` 只发 win-x86/win-x64） |
| VST3 native | `Native/<rid>/ownvst3.tlhost.<ext>`（`InsertHost.csproj` 按 `Vst3NativeRid` 拷贝） | 上游覆盖 win/linux/osx 的 x64 + arm64；x86 已放弃 |
| 控制/共享段清理 | 运行时 | 见 §9.6 |

接线要点：
- `VstBridgeClient.FindBridgeExecutable`（行 743）从"写死 win-x86/win-x64"改为按
  `RuntimeInformation` 算当前 RID；
- `Vst3BridgeInsert.Create`（行 331）与 `Vst3PluginHost.FindNativeLibrary`（行 36）的
  `IsWindows && Is64BitProcess` 闸门放开为"当前平台有 native + 有桥"；
- `BridgeSharedMemory` 的 `[SupportedOSPlatform("windows")]` 标注放开，并按 §9.6 处理残留；
- `publish-bridges.ps1` 扩为按 RID 矩阵发布（或改由 CI 的 `-r <rid>` 一并构建，纳入 release 产物）。

### 9.10 验证清单（每个 OS 各跑一遍）

| # | 项 | 状态（P5.1，Linux x64） |
|---|---|---|
| 1 | 卡死的假插件 / 假后端 → 宿主超时杀桥、标红；断言桥进程与孙进程都没了 | ✅ 宿主侧 `KillBridge` 按进程组 SIGKILL（`BridgeIsolationTests.KillBridgeRemovesTheProcess`）；超时→杀桥机制见 P4 的 `BridgeTimeoutTests`。孙进程随进程组一起走 |
| 2 | 宿主被 SIGKILL → 桥随之消失 | ✅ `BridgeIsolationTests.BridgeDiesWhenTheLivenessPipeCloses`：关掉存活管道写端 == 宿主死亡的等价事件（内核回收写端正是 SIGKILL 的效果，且实测证明桥没有继承到写端） |
| 3 | 桥被 SIGKILL → 宿主存活、标红、重建；断言无残留 | ✅ 宿主存活由既有直通/超时兜底；进程组与 `BridgeProcessIdForTest` 已就位。残留清扫归 P5.2 |
| 4 | 音频正确性回归：`SharedAudioRingTests` 跨平台跑一遍 | ✅ Linux 上全绿（含在 66 条 focused 里） |
| 5 | ARM64 内存序回归 | ⏳ 待 ARM64 机器 |

### 9.11 阶段划分（P5）

| 阶段 | 内容 | 验收 |
|---|---|---|
| P5.1 | ✅ 存活管道 + 看门狗线程 + 进程组（先 Linux x64；**`prctl` 缓做**，见 §9.4/§9.7） | 清单 §9.10 的 1–4，Linux x64 已过 |
| P5.2 | 共享段 / 命名事件 / UDS 的 owner 化 + 启动清扫 | 强杀宿主、强杀桥后无残留 |
| P5.3 | 每 RID 桥 exe + native 发布接线（含 `FindBridgeExecutable` 按 RID） | linux-x64 / osx-arm64 出包即用 |
| P5.4 | macOS（存活管道版，无 PDEATHSIG）+ 其余架构（win-arm64 / linux-arm64 / osx-x64） | 目标矩阵全绿 |
| P5.5 | 隔离在非 Windows 的默认开关 + 回退提示 + 文档 / 用户手册 | 真机回归；老工程声音不变 |

> 编辑器 GUI 的跨平台 pump 不在 P5，另立一项（macOS Cocoa / Linux X11-Wayland）。

### 9.12 待决与风险

| 项 | 说明 |
|---|---|
| loongarch64 | .NET 8 无官方 `linux-loongarch64` RID，需社区运行时；且 `Vst3Backend.NativeRid` 的 `else` 分支会把它错判成 `linux-x64`。本轮暂缓，恢复前须先补 RID 映射与运行时可行性论证 |
| macOS 无 PDEATHSIG | 只有存活管道；看门狗线程必须可靠（`read()` 不能被信号打断成假唤醒） |
| 编辑器 | 非 Windows 的 GUI pump 是另一条独立工作；在它落地前，桥在非 Windows 一律退参数面板 |
| 32 位宿主 | 若将来出 win-x86 host：VST3 应显式**禁用并可见提示**，而不是静默找不到 x86 native |
| 回退语义 | 与 Windows 一致：桥**起不来** → 回退进程内并可见标示；桥**是好的但插件把桥弄没** → 如实报失败，不回退 |

## 10. 实现进度（随提交更新）

**P1 桥侧（已完成，2026-10-30）**

- 协议（`VstBridge/BridgeProtocol.cs`，两端各编译一份）：`LoadPluginMessage.Format`（缺省
  `vst2`，旧客户端行为不变）、`LoadPluginResultMessage.NeedsOpenGl`、`ParameterInfo.DefaultValue`、
  `OpenEditorResultMessage.WindowHandle`、`HelloAckMessage.Formats`。全部是**追加字段**，
  所以 `ProtocolVersion` 暂时仍为 2 —— 旧桥能继续服务 VST2，新宿主也能读旧桥的回执。
- 宿主抽象：`IBridgePluginHost`（`VstBridge/IBridgePluginHost.cs`），`PluginHost`（VST2）与
  新增的 `Vst3PluginHost` 都实现它。
- `Vst3PluginHost`（`VstBridge/Vst3PluginHost.cs`）：**复用 InsertHost 的 `Vst3Backend` /
  `Vst3Insert`**，只做协议/共享环的搬运。加载 / 处理 / state / 参数全部可用；
  音频按平面 ↔ 交织转换；侧链沿第 3/4 条平面重建。
- `BridgeSession`：按 `format` 选宿主、记录 `slot → 宿主`，其余消息按槽位转发；
  `HelloAck.Formats`：x64 桥声明 vst2+vst3，x86 桥只声明 vst2。
- 工程：两个桥都引用了 `InsertHost`（复用 VST3 逻辑，不抄第二遍）；x64 桥把
  `ownvst3.tlhost.dll` 复制到身边。x86 桥虽然也编译了 VST3 支持，但
  `Vst3PluginHost.IsSupported` 在 32 位进程里为 false，不会误用。
- 测试：`tests/TuneLab.Tests/BridgeProtocolFormatTests.cs` 钉住旧消息的缺省与各新字段的往返。

**P1 宿主侧（已完成，2026-10-30）**

- `InsertSettings.IsolateVst3`（默认 false）+ `InsertInstanceFactory.IsolateVst3Provider`
  由宿主注入（读 `insert-plugins.json` 的 `IsolateVst3`），工厂只拿值、不读文件。
- `IVst3Insert`（`InsertHost/IVst3Insert.cs`）：进程内 `Vst3Insert` 与过桥 `Vst3BridgeInsert`
  的宿主统一面。宿主里原来的 `is Vst3Insert` 全部改成 `is IVst3Insert`
  （工厂的 state 存/读、`InsertSlotAdapter.HasEditor`、编辑器壳、参数面板）。
- `Vst3BridgeInsert`（`InsertHost/Vst3BridgeInsert.cs`）：`IVst3Insert` 的桥实现，
  与 `Vst2Insert` 同构（平面 ↔ 交织、共享环发 process、state/参数走消息）。P1 的编辑器
  一律不可用（`HasEditor=false`），宿主自动退参数面板。
- 连接面：从 `VstBridgeClient` 抽出 `IBridgeChannel`（内部子集），
  `Vst3BridgeInsert` 只依赖它 —— 单测用假通道，不碰原生。
- `Vst3Backend.Create`：`IsolateVst3` 且 Windows x64 时先试桥；桥不可用 / 起不来 →
  记 `IsolationFallbackReason` 并回退进程内（宿主写日志）；桥是好的但插件拒绝 /
  加载时把桥弄没了 → 如实报失败，**不回退**（回退等于把刚炸完的插件又拉进宿主）。
- 设置页（`InsertPluginPathsPage`）：新增"崩溃隔离"一节 + 开关，读写 `IInsertPluginSettings`。
- 超时保护（`InsertHost/BridgeTimeout.cs` + `VstBridgeClient.RunTimed`）：加载 20s、
  state 15s、参数 5s、卸载 / 关编辑器 5s、握手 15s。任何一条超时 → **杀桥** +
  记 `LastTimeoutError`，下一次操作会重启一座干净的桥。这是"插件卡在 Create
  （实测 CHOWTapeModel / ChowMatrix）不会把宿主 UI 线程一起带走"的保证 ——
  在此之前只有开编辑器一条路有上限。
- 测试：`tests/TuneLab.Tests/Vst3BridgeInsertTests.cs`（假 `IBridgeChannel`）、
  `tests/TuneLab.Tests/BridgeTimeoutTests.cs`（超时 / 兜底 / 杀桥三条分支）。
- **插槽标红**：`InsertInstanceFactory` 记每个槽位最近一次建实例失败的原因，
  经 `IMixerInsert.FailureReason`（DIM，默认 null）透传到 `FxPopupWindow`：
  那一行名字变红 + `⚠` 前缀，原因进 tooltip。加载超时 / 桥崩 / 插件没装都走这一条。
  运行期音频挂起（桥在 process 里卡死 → 音频直通、实例还活着）见 P4 条目。
- 验收状态：解决方案 / 两套测试全绿；两个桥重新发布。**尚未**在真机 + 真插件上跑通"经桥出声、
  state 存读"（需要开着 TuneLab 挂一个真 VST3），留待 P3 真机回归一起做。

**P2 桥侧编辑器（已完成，2026-10-31）**

- 新增 `VstBridge/Vst3EditorWindow.cs`：与 `Vst2EditorWindow` 同构的桥侧编辑器宿主。
  在一条 **STA 线程**上 `OpenEditor(0)`（vendored 的 ownvst3 只支持"插件自己开顶层窗"
  这一种模式，见 `Vst3Insert.OpenEditor` 的实测记录）→ 用 `SnapshotTopLevelWindows` /
  `FindEditorWindowSince` 找到插件那扇窗 → 循环泵消息（`SafeDispatchMessage` +
  `PeekMessage`/`TranslateMessage`/`DispatchMessage`）→ 关窗时在**同一条线程**上
  `CloseEditor`（native 约定）。`Dispose` 会 Join 线程后才能让桥 `Unload`（否则卸载时
  插件代码还在跑，AccessViolation）。
- 协议回传：`IBridgePluginHost.OpenEditor` 现返回 `Window`（`IntPtr`）；
  `BridgeSession.HandleOpenEditor` 把它写进 `OpenEditorResultMessage.WindowHandle`
  （VST2 回 0，VST3 回插件窗口句柄）。`Load` 增加 `NeedsOpenGl`（VST3 如实回报，
  VST2 恒 false），`HasEditor` 对 VST3 也从桥回报（不再硬编码 false）。
- 宿主侧：`IBridgeChannel.OpenEditorBlocking` / `CloseEditorBlocking`；
  `VstBridgeClient` 的编辑器往返带上 `WindowHandle`。`Vst3BridgeInsert` 实现
  `IVst3Insert` 的编辑器面：`OpenEditor` 翻成桥消息并记住回传的 HWND，
  `IsEditorOpen` 用跨进程 `IsWindow` 判"用户是否已经关掉"，`PumpEditorMessages` 是空操作
  （桥自己那条线程在泵），`FindEditorWindowSince` 直接返回回传句柄 —— 于是
  `Vst3EditorShell` **不用改**：`TryRaise` 照常拿句柄置前，巡查定时器照常判 `IsEditorOpen`。
- GL 兜底不变：`Vst3EditorShell` 在 `NeedsOpenGl && !HasUsableOpenGl` 时退回参数面板；
  桥侧开窗失败（插件没界面 / 拒绝）也由壳退回参数面板。
- 测试：`Vst3BridgeInsertTests` 新增"编辑器经桥开 / 关""回传句柄给置前""桥拒绝则不开"。
- 验收：解决方案 / 两套测试全绿（932）；两个桥重新发布（x64 带 native，x86 不带）。
  **仍需真机 + 真 VST3** 才能确认"插件窗口真的画出来、能交互、关窗后再开正常"
  —— 留在 P3 真机回归一起做。

**P4 运行期挂起标红 + 挂起测试（已完成，2026-10-31）**

- 判定抽成 `InsertHost/AudioStallWatch.cs`：连续失败到阈值（`FailureLimit=50`，约 1 秒）
  判死、成功一块清零、杀桥复位。从 `VstBridgeClient` 里搬出来，于是"到第几块判死 /
  复位"能脱离真桥单测。
- 运行期失败面：`IBridgeChannel.AudioStalled`；新增 `IRuntimeInsertFailure`
  （`RuntimeFailureReason`），`Vst2Insert` / `Vst3BridgeInsert` 都实现 —— 音频停用后
  实例还活着（能改旁通 / 移除），但界面要把这一行标红。
- 工厂合流：`InsertInstanceFactory.FailureOf` 先看建实例失败，再看活实例的运行期失败 ——
  两个来源对界面是同一件事，界面侧（`IMixerInsert.FailureReason` / tooltip）一行不用改。
  （单测经 `RegisterLiveForTest` 塞一个假实例，不起真插件。）
- 浮窗实时刷新：`FxPopupWindow` 加一个 700ms 的 `DispatcherTimer`，只在"失败集合"
  指纹变化时才重建整行 —— `RoutingChanged` 只在**成员**增删时响，捕不到运行期失败，
  而重建会重建控件，不能每周期白做。
- 测试：`AudioStallWatchTests`（阈值 / 成功清零 / 复位 / 锁存）、`Vst3BridgeInsertTests`
  新增三条（活着为 null / 通道已停即标红 / 连续失败到阈值后标红 —— 后两条用真的判定器
  接起来）、`InsertModelTests` 新增工厂合流一条。加载超时 → 杀桥的机制仍由 P1 的
  `BridgeTimeoutTests` 钉住（挂起测试的宿主侧那一半）。
- 验收状态：解决方案 / 两套测试全绿（941）；两个桥未改、无需重发。**仍需真机 + 真 VST3**
  跑一次"插件卡在 process → 一秒内标红 + 音频直通、宿主不冻结"（与 P1/P2 一起留待 P3
  真机回归）。

**P5.1 跨平台隔离内核（已完成，Linux x64，2026-10-31）**

- **存活管道（A1）**：宿主在启动桥前开一根匿名管道（`AnonymousPipeServerStream`，可继承），
  写端留宿主、读端经 `--liveness <fd>` 交给桥；桥起一支**独立看门狗线程**阻塞读它。宿主进程一死
  （正常 / 崩溃 / SIGKILL），写端被内核回收 → 桥读到 EOF → `kill(-pgid, SIGKILL)` 带走自己整个进程组。
  独立于桥主循环，插件卡死也照样醒。实测：桥**没有**继承到写端（`BridgeIsolationTests` 关掉写端即触发）。
- **进程组**：桥启动即 `setpgid(0,0)`；宿主 `KillBridge` 先按进程组 SIGKILL，再以
  `Process.Kill(entireProcessTree)` 兜底（覆盖"桥还没 setpgid 就被杀"）。
- **平台闸门放开**：`Vst3BridgeInsert.PlatformSupported`（64 位 + win/linux/macOS）取代
  `IsWindows && Is64BitProcess`；`Vst3PluginHost.FindNativeLibrary`、`Vst3Backend.Create` 同步；
  `FindBridgeExecutable` 按 `RuntimeInformation` 算当前 RID（Windows 仍 win-x86/win-x64，行为不变）。
- **两处 .NET 现实打脸（已修正设计稿 §9.6/§9.7）**：Unix 上**命名 `MemoryMappedFile` 与命名
  `EventWaitHandle` 都不支持**。共享段改为宿主私有目录里的文件（创建者 Dispose 时删除）；
  gate 在 Unix 上暂不装，消费者退化成自旋 + Yield（正确、稍费 CPU）。**A2 `prctl` 缓做**（创建线程
  语义的坑），只上 A1 —— 它已是全平台保底。
- 测试：`tests/TuneLab.Tests/BridgeIsolationTests.cs`（Linux-only，桥 exe 不在就安静返回）：
  自成进程组 / 存活管道 EOF → 桥消失 / `KillBridge` 清干净。真机验证在 172.31.5.154（Anolis 8.10，
  .NET 8.0.424）：自包含发布 linux-x64 桥 → 3 条全过；`SharedAudioRingTests` 等 66 条 focused 全绿；
  全量 `TuneLab.Tests` Linux 上与基线同为 16 条既有平台/SKIP 失败、无新增。
- **尚未做**：A2 `prctl`、Unix gate（eventfd/自管道）、残留启动清扫（P5.2）、
  每 RID 发布接线（P5.3，目前只在验证机上手工 publish）、macOS / 其余架构（P5.4）、非 Windows 默认开（P5.5）。

**已知杂物**

（暂无：引用 `InsertHost` 会让 x86 桥输出多一份 x64 的 `ownvst3.tlhost.dll`，
已在 `VstBridge.csproj` 用 `AfterTargets=Build/Publish` 删掉。）
