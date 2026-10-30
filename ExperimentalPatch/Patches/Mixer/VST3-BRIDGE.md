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

## 9. 实现进度（随提交更新）

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
  尚未覆盖"运行期音频挂起"（桥在 process 里卡死 → 音频直通、实例还活着，没标红）—— 留给 P4。
- 验收状态：解决方案 / 两套测试全绿；两个桥重新发布。**尚未**在真机 + 真插件上跑通"经桥出声、
  state 存读"（需要开着 TuneLab 挂一个真 VST3），留待 P3 真机回归一起做。

**P2（未做）**

桥侧编辑器：独立 STA 线程 + Win32 消息循环，`OpenEditorResult.WindowHandle` 回传真实句柄。
当前 `Vst3PluginHost.OpenEditor` 一律回 false（`HasEditor` 也回 false），宿主走参数面板。

**已知杂物**

（暂无：引用 `InsertHost` 会让 x86 桥输出多一份 x64 的 `ownvst3.tlhost.dll`，
已在 `VstBridge.csproj` 用 `AfterTargets=Build/Publish` 删掉。）
