# 混音器引擎：接驳点与上游更新后的重接指南

> 读者：**将来做上游同步的 AI agent（或人）**。本文假设你手上是一个刚 merge / rebase 完 upstream 的分支，
> 可能编译不过、也可能编译过但混音不对，而你对混音器引擎一无所知。
>
> 状态：**M3 已落地**（图引擎 + 接驳点 1 / 3 / 7 / 9；验证：混音相关 19 条测试全绿，
> 其中"退化图与旧 `MixData` 逐样本 bit 相同"是硬验收）。每落地一个接驳点，就把"实际代码形状"
> 补进 §2 的表里（这是硬要求——否则下一次重接就退化成重新理解整个引擎）。

## 0. 先读这三行

- 引擎（纯 DSP）在 `ExperimentalPatch/MixerEngine/`，插入宿主在 `ExperimentalPatch/InsertHost/`。
  **这两个目录与上游无关，永远不会冲突**——出问题时不要先去那里改。
- 冲突只可能发生在宿主里的 9 个接驳点（§2 的表），每个点都带 `[EP-MIXER-n]` 注释。
- 判断"接驳是否完好"：`dotnet build` 通过只是第一关，**必须**跑完 §4 的三级验证。

## 1. 依赖方向（违反这条，后面的设计都不成立）

```
TuneLab（宿主）
  ├──> ExperimentalPatch            （UI 补丁：调音台窗口、浮窗、菜单）
  ├──> ExperimentalPatch/MixerEngine（纯 DSP：图、节点、PDC、环路）
  └──> ExperimentalPatch/InsertHost （VST2 / VST3 / JSFX 适配）

补丁侧三个工程都不引用 TuneLab。
```

- 引擎**不知道** `Track` / `Project` / Avalonia 存在：宿主把模型编译成 `MixSnapshot` 再交给它。
- 如果你为了让引擎编译而想加一条 `ProjectReference` 到 `TuneLab` → **方向反了，停下来**，
  正确做法是在引擎里加接口、在宿主侧写适配器。

## 2. 接驳点清单

| 标记 | 文件 | 改什么 | 形状 | 上游改坏时的症状 | 重接要点 |
|---|---|---|---|---|---|
| `EP-MIXER-1` | `Audio/AudioGraph.cs` | `MixData` 走图；`AddData` 拆成"算增益 + 按增益求和" | **已落地**：`AddData(...)` 原签名转调新重载 `AddData(..., float leftVolume, float rightVolume)`（逐段循环原样搬进去）；`MixData` 开头 `if (isStereo && mMixer.HasSnapshot) { mCompiler.RefreshSolo(); mMixer.Render(...); return; }`，旧循环整段留作 `MixDataLegacy`；新增 `MixIdle` / `ApplyPreviewAndMasterGain` / `RebuildSnapshot`（`AddTrack`/`RemoveTrack` 里调）；字段 `Mixer mMixer` + `MixerGraphCompiler mCompiler` | 无声 / 只出一部分声 / 音量差一倍 | 先确认 `AddData` 的逐段循环没被改动（bit 相同依赖它）；`MixData` 里**只有一处**决定走图还是走旧路 |
| `EP-MIXER-2` | `Audio/AudioEngine.cs` | `ExportMaster` / `ExportTrack` 走图 | 两个方法体各一段 | 导出无声、导出比播放小/大、Master 插入不进文件 | `ExportMaster` = 渲染图到文件 sink；`ExportTrack` = 只渲染该轨节点（pre-routing） |
| `EP-MIXER-3` | `Audio/AudioEngine.cs` | 播放回调把预览音与 Master 增益交给图 | **已落地**：`AudioSampleProvider.Read` 删掉 `mAudioPlayer.AddData(...)` 与手乘 `MasterGain`；改为 `MixData(..., PreviewSource)`，未播放时 `MixIdle(...)`；新增懒建字段 `PreviewSource` | 预览音消失 / 音量被乘两次 / 未播放时预览音不受 Master 影响 | Master 增益**只能在一个地方乘**（图里的 Master 节点，或 `MixIdle`）。回调里不许再乘 |
| `EP-MIXER-4` | `Data/Track.cs` / `ITrack.cs` / `Audio/IAudioTrack.cs` | `Output` / `Sends` 三个 `DataProperty` | **已落地**：`Track.Output`（`DataStruct<MixerRoute>`）+ `Track.Sends`（`DataObjectList<Send>`）；`IAudioTrack` 加 `Output`/`Sends`（混音图要按它连边）；`Send`/`MixerRoute`/`AuxBus` 在 `Data/MixerRouting.cs` | 路由不生效 / 不进 undo | 路由值必须是 `DataStruct`（值相等才去重）；加 send 时先填值再加进列表，否则"加一条"会变成三步 undo |
| `EP-MIXER-5` | `Data/Project.cs` + `Audio/AudioEngine.cs` + `Audio/AudioGraph.cs` | AUX 有序表 + 增删 + 路由变化触发重建 | **已落地**：`Project.Auxes`/`AddAux`/`RemoveAux`；`AudioEngine.AddAux/RemoveAux/RebuildMixerGraph`；`AudioGraph` 持有 `mAuxes` 并在结构变化时重建快照 | 加 AUX 没声音 / 改路由后还是旧声音 | 推子/声像/静音是活值**不重建**；只有 output/send 增删才重建（`RebuildMixerGraph`） |
| `EP-MIXER-6` | `Data/NativeTrackInfo.cs` + `Extensions/Formats/TLP/TuneLabProjectCbor.cs` | 路由 / 插入的落盘 | **未落地**。计划：`version` **永远保持 1**，数据写顶层键 `epMixer`（原版 `SkipValue` 会跳过）+ 同目录 sidecar（原版往返后仍在）。见 DESIGN.md §9 | 老工程打不开 / 原版打不开我们的工程 / 原版往返后路由没了 | **别升 version**（原版只认 1，升了就拒绝打开）；`ProjectFormatCompatibilityTests` 四条是封条，改格式必须让它保持绿 |
| `EP-MIXER-7` | `TuneLab.csproj` | 加 `MixerEngine`（以及将来 `InsertHost`）的 `ProjectReference` | **已落地**：两条引用；另在 `ExperimentalPatch.csproj` 里 `Compile Remove="MixerEngine/**"` | 编译找不到命名空间 / **CS0579 特性重复** | 排除项不能丢（嵌套工程的 obj 会被默认 `**/*.cs` 吃进来）；上游若重排 ItemGroup，把这两条加回去 |
| `EP-MIXER-8` | `UI/MainWindow/Editor/Editor.cs` | 「实验功能」菜单（从补丁收集命令） | **已落地**：`CreateMenu()` 末尾一段（`ExperimentalPatchBootstrap.GetCommands()`，空表时整块跳过）；标题按 `I18N.TranslationManager.CurrentLanguage` 选词（**不走 `SetTrName`**，免得动上游翻译表）；命令调用点 `InvokeExperimentalCommand` 兜异常 | 菜单消失 / 点了没反应 / **`ActionCoverageTests` 红** | 这个入口必须在 `docs/action-coverage.md` 的 Editor.cs 段落里有一行（裁决 `by-design`）；上游合并后那一行若丢了，封条会直接报错——按提示补回即可 |
| `EP-MIXER-9` | `ExperimentalPatchBootstrap.cs` + `TuneLab/ExperimentalPatchMixerModel.cs`（新） | `CreateMixerModel()` 转发 + `IMixerModel` 适配器 + 快照编译器 | **已落地**：`CreateMixerModel()`（按工程实例缓存、`Stop()` 里 Dispose）；`ExperimentalPatchMixerModel.cs` 有 `MixerModelAdapter` / `MixerTrackChannel` / `MixerMasterChannel` / `MixerOutput` / `MixerInsertFormat`；`ExperimentalPatchMixerAudio.cs` 有 `MixerGraphCompiler` 等 | 调音台打开是空的 / 读到旧数据 / 拖动不进 undo | 读写一律落到 `Track` 的 `DataProperty`；一次拖动用 `BeginMergeNotify`+`DiscardTo`+`Commit`（与轨道头同一套），单次写入写完要 `Commit()` |

## 3. 重接流程（merge / rebase 之后照这个顺序）

1. `git diff upstream/master -- TuneLab/Audio/AudioGraph.cs TuneLab/Audio/AudioEngine.cs ...`
   —— 只看 §2 里那 5 个宿主文件的差异，别的不用管。
2. `grep -rn "EP-MIXER-" TuneLab/` —— 标记还在不在？被上游整段替换掉的，按 §2 的"重接要点"重新插入。
3. 编译。报错信息通常会直接指出是哪个接驳点（类型/签名变了）。
4. 跑 §4 的三级验证。**任何一级不过，都不算重接完成。**
5. 把这一轮"上游改了什么、你怎么接的"补进 §6（给下一个人/AI 留经验）。

## 4. 验证（三级，缺一不可）

| 级别 | 做法 | 通过标准 |
|---|---|---|
| 1 编译 | `dotnet build TuneLab.sln -c Debug` | 0 error |
| 2 单测 | `dotnet test tests/TuneLab.Tests/...` + `dotnet test tests/MixerEngine.Tests/...`（引擎自己的纯 DSP 测试） | 全绿；引擎测试**不依赖音频设备** |
| 3 等价性 | 同一工程分别用"旧 MixData"与"新图"渲染（引擎测试里保留一条 legacy 参照实现） | 缓冲区**逐样本 bit 相同**（无路由时，见 DESIGN.md §7.2） |

再补一条人工冒烟（有 UI 时）：打开调音台 → 播放 → 推子/pan/M/S 与编辑器互相同步 → 建一条 AUX 与一条 send →
听感正常 → 存盘再打开，路由与插入都还在。

## 5. 断点症状 → 先查哪

| 症状 | 先查 |
|---|---|
| 播放完全无声 | `EP-MIXER-1`（图没跑）或 `EP-MIXER-3`（sink 没接） |
| 只有一部分轨出声 | `EP-MIXER-1`（快照没包含全部轨，或拓扑序漏节点） |
| 音量整体差一倍 / 差 6 dB | `EP-MIXER-3`：Master 增益乘了两次，或 pan 律被重复应用 |
| 静音按钮不起作用 | `EP-MIXER-1`：门控被放到了总线而不是源头（DESIGN.md §3.3） |
| 导出与播放不一致 | `EP-MIXER-2`（导出没走图）或 `EP-MIXER-3`（播放多乘了一次） |
| 老工程打不开 | `EP-MIXER-6`：版本号或 reader 分支 |
| 存盘再开路由丢了 | `EP-MIXER-4/5/6`：属性不是 `DataProperty`，或 native DTO 没写 |
| 挂上插件后声音"薄、空心" | PDC（DESIGN.md §3.5）：延迟补偿没生效 |
| 改路由后爆音 / 崩溃 | 快照切换：音频线程还在用旧快照就被释放了 |

## 6. 上游更新日志（每轮同步后补一行）

| 日期 | 上游改了什么 | 我们怎么接的 | 遗留 |
|---|---|---|---|
| — | （还没有发生过） | — | — |

## 7. 维护纪律

- **新增任何宿主改动**：必须先在 §2 加一行、再给代码打 `[EP-MIXER-n]` 标记，最后写清"断点症状"。
  没有登记表的接驳点，下一次同步时会被当成上游代码删掉。
- 接驳点**只做一件事**；需要更多逻辑时，写进补丁侧工程或 `TuneLab/ExperimentalPatch*.cs`（新文件）。
- 每个接驳点的代码旁边留一句"为什么"，不要只留"做了什么"——重接的人需要判断"上游的改动是否让这个理由失效"。
