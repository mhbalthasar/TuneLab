# 混音器引擎：接驳点与上游更新后的重接指南

> 读者：**将来做上游同步的 AI agent（或人）**。本文假设你手上是一个刚 merge / rebase 完 upstream 的分支，
> 可能编译不过、也可能编译过但混音不对，而你对混音器引擎一无所知。
>
> 状态：**阶段一（混音器）已完成；阶段二（插入效果）进行中**——已落地插入链骨架
> （`MixerEngine/IMixInsert` + 图内跑链 + PDC）、VST3 进程内后端（OwnAudioVST3 托管层降级 vendor 到 net8）、
> 32 位 VST2 的 x86 桥进程（`VstBridge/`，含 JobObject 生命周期与共享内存音频环）、
> 插件扫描路径设置（`EP-MIXER-12`）。每落地一个接驳点，就把"实际代码形状"补进 §2 的表里
> （这是硬要求——否则下一次重接就退化成重新理解整个引擎）。

## 0. 先读这三行

- 引擎（纯 DSP）在 `ExperimentalPatch/MixerEngine/`，插入宿主在 `ExperimentalPatch/InsertHost/`。
  **这两个目录与上游无关，永远不会冲突**——出问题时不要先去那里改。
- 冲突只可能发生在宿主里的接驳点（§2 的表），每个点都带 `[EP-MIXER-n]` 注释。- 判断"接驳是否完好"：`dotnet build` 通过只是第一关，**必须**跑完 §4 的三级验证。

## 1. 依赖方向（违反这条，后面的设计都不成立）

```
TuneLab（宿主）
  ├──> ExperimentalPatch            （UI 补丁：调音台窗口、浮窗、菜单）
  ├──> ExperimentalPatch/MixerEngine（纯 DSP：图、节点、PDC、环路、IMixInsert 契约）
  ├──> ExperimentalPatch/InsertHost （VST3 进程内后端 + VstBridgeClient）
  │      └─ 共享源码（各自编译一份）：VstBridge/BridgeProtocol.cs + BridgeSharedMemory.cs
  └──> ExperimentalPatch/VstBridge  （x86 独立进程，宿主按需启动，桥只被宿主引用）
         └─ 由宿主以 --job <句柄> --pipe <名字> 启动；JobObject 令其与宿主同生共死

补丁侧四个工程都不引用 TuneLab。
```

- 引擎**不知道** `Track` / `Project` / Avalonia 存在：宿主把模型编译成 `MixSnapshot` 再交给它。
- 如果你为了让引擎编译而想加一条 `ProjectReference` 到 `TuneLab` → **方向反了，停下来**，
  正确做法是在引擎里加接口、在宿主侧写适配器。

## 2. 接驳点清单

| 标记 | 文件 | 改什么 | 形状 | 上游改坏时的症状 | 重接要点 |
|---|---|---|---|---|---|
| `EP-MIXER-1` | `Audio/AudioGraph.cs` | `MixData` 走图；`AddData` 拆成"算增益 + 按增益求和" | **已落地**：`AddData(...)` 原签名转调新重载 `AddData(..., float leftVolume, float rightVolume)`（逐段循环原样搬进去）；`MixData` 开头 `if (isStereo && mMixer.HasSnapshot) { mCompiler.RefreshSolo(); mMixer.Render(...); return; }`，旧循环整段留作 `MixDataLegacy`；新增 `MixIdle` / `ApplyPreviewAndMasterGain` / `RebuildSnapshot`（`AddTrack`/`RemoveTrack` 里调）；字段 `Mixer mMixer` + `MixerGraphCompiler mCompiler` | 无声 / 只出一部分声 / 音量差一倍 | 先确认 `AddData` 的逐段循环没被改动（bit 相同依赖它）；`MixData` 里**只有一处**决定走图还是走旧路 |
| `EP-MIXER-2` | `Audio/AudioEngine.cs` | `ExportMaster` / `ExportTrack` 走图 | 两个方法体各一段 | 导出无声、导出比播放小/大、Master 插入不进文件 | `ExportMaster` = 渲染图到文件 sink；`ExportTrack` = 只渲染该轨节点（pre-routing） |
| `EP-MIXER-3` | `Audio/AudioEngine.cs` | 播放回调把预览音与 Master 增益交给图；新增 AUX/Master 电平查询 | **已落地**：`AudioSampleProvider.Read` 删掉 `mAudioPlayer.AddData(...)` 与手乘 `MasterGain`；改为 `MixData(..., PreviewSource)`，未播放时 `MixIdle(...)`；新增懒建字段 `PreviewSource`；新增 `InvokeRealtimeChannelAmplitude(channelId, out …)`（读图的节点峰值，停止时返回 null） | 预览音消失 / 音量被乘两次 / AUX 或 Master 表不动 | Master 增益**只能在一个地方乘**（图里的 Master 节点，或 `MixIdle`）。回调里不许再乘。节点峰值只在渲染时更新，所以"没在播"必须由宿主判定 |
| `EP-MIXER-4` | `Data/Track.cs` / `ITrack.cs` / `Audio/IAudioTrack.cs` | `Output` / `Sends` 三个 `DataProperty` | **已落地**：`Track.Output`（`DataStruct<MixerRoute>`）+ `Track.Sends`（`DataObjectList<Send>`）；`IAudioTrack` 加 `Output`/`Sends`（混音图要按它连边）；`Send`/`MixerRoute`/`AuxBus` 在 `Data/MixerRouting.cs` | 路由不生效 / 不进 undo | 路由值必须是 `DataStruct`（值相等才去重）；加 send 时先填值再加进列表，否则"加一条"会变成三步 undo |
| `EP-MIXER-5` | `Data/Project.cs` + `Audio/AudioEngine.cs` + `Audio/AudioGraph.cs` | AUX 有序表 + 增删 + 路由变化触发重建 | **已落地**：`Project.Auxes`/`AddAux`/`RemoveAux`；`AudioEngine.AddAux/RemoveAux/RebuildMixerGraph`；`AudioGraph` 持有 `mAuxes` 并在结构变化时重建快照 | 加 AUX 没声音 / 改路由后还是旧声音 | 推子/声像/静音是活值**不重建**；只有 output/send 增删才重建（`RebuildMixerGraph`） |
| `EP-MIXER-6` | `Data/NativeTrackInfo.cs` + `Extensions/Formats/TLP/TuneLabProjectCbor.cs` + `NativeProjectFormat.cs` | 路由 / 插入的落盘 | **已落地（路由部分）**：`NativeProjectFile.EpMixer`（紧凑 JSON 文本）+ CBOR 读写各一段；`version` **保持 1**（原版 `SkipValue` 跳过未知键），另写同目录 sidecar 兜住原版往返。见 DESIGN.md §9 | 老工程打不开 / 原版打不开我们的工程 / 原版往返后路由没了 | **别升 version**；`ProjectFormatCompatibilityTests` 四条是封条；插入效果的状态将来也走这个载荷（base64），不要塞进 `PropertyObject`（它没有字节串） |
| `EP-MIXER-7` | `TuneLab.csproj` | 加 `MixerEngine`（以及将来 `InsertHost`）的 `ProjectReference` | **已落地**：两条引用；另在 `ExperimentalPatch.csproj` 里 `Compile Remove="MixerEngine/**"` | 编译找不到命名空间 / **CS0579 特性重复** | 排除项不能丢（嵌套工程的 obj 会被默认 `**/*.cs` 吃进来）；上游若重排 ItemGroup，把这两条加回去 |
| `EP-MIXER-8` | `UI/MainWindow/Editor/Editor.cs` | 「实验功能」菜单（从补丁收集命令） | **已落地**：`CreateMenu()` 末尾一段（`ExperimentalPatchBootstrap.GetCommands()`，空表时整块跳过）；标题按 `I18N.TranslationManager.CurrentLanguage` 选词（**不走 `SetTrName`**，免得动上游翻译表）；命令调用点 `InvokeExperimentalCommand` 兜异常 | 菜单消失 / 点了没反应 / **`ActionCoverageTests` 红** | 这个入口必须在 `docs/action-coverage.md` 的 Editor.cs 段落里有一行（裁决 `by-design`）；上游合并后那一行若丢了，封条会直接报错——按提示补回即可 |
| `EP-MIXER-9` | `ExperimentalPatchBootstrap.cs` + `TuneLab/ExperimentalPatchMixerModel.cs`（新） | `CreateMixerModel()` 转发 + `IMixerModel` 适配器 + 快照编译器 | **已落地**：`CreateMixerModel()`（按工程实例缓存、`Stop()` 里 Dispose）；`ExperimentalPatchMixerModel.cs` 有 `MixerModelAdapter` / `MixerTrackChannel` / `MixerAuxChannel` / `MixerMasterChannel` / `MixerSendAdapter` / `MixerOutput` / `MixerInsertFormat`；`ExperimentalPatchMixerAudio.cs` 有 `MixerGraphCompiler` 等 | 调音台打开是空的 / 读到旧数据 / 拖动不进 undo | 读写一律落到 `Track` / `AuxBus` 的 `DataProperty`；一次拖动用 `BeginMergeNotify`+`DiscardTo`+`Commit`，单次写入写完要 `Commit()` |
| `EP-MIXER-10` | `Audio/NAudio/NAudioCodec.cs` | 导出编码的**兜底夹紧** | **已落地**：`To16BitsBytes` / `To24BitsBytes` 改成"先 `Clamp(-1,1)` 再换算"（16-bit 用 32767 做满量程）；两个方法改成 internal 以便单测 | 导出有"刺啦"声 / 满量程正峰变成负峰 | 原来的 `(short)(x * 32768)` 是未检查转换：1.0 回绕成 −32768。封条见 `AudioClippingTests`；限幅器本体在 `MixerEngine/PeakLimiter.cs` |
| `EP-MIXER-11` | `TuneLab/ExperimentalPatchMixerPersistence.cs`（新）+ `Editor.cs` 四处 + `NativeProjectFormat.cs` / `TuneLabProjectCbor.cs` | 路由落盘（文件内 + sidecar） | **已落地**：三处保存都填 `NativeProjectFile.EpMixer`，打开时 `MixerPersistence.Load(project, path, file.EpMixer)`（文件内优先、sidecar 兜底）；CBOR 读写各一段 | 存盘后重开路由丢了 / 打开工程报错 / 原版打不开 | **不要给 SDK 的 `TrackInfo` 加字段**（冻结面）；载荷格式改了就抬 `branch` 号，**别动宿主 `version`**；读失败只许记日志，不许挡住打开工程。插入的 `state`（base64）将来也进这个载荷，**不要**塞进 `PropertyObject`（它没有字节串） |
| `EP-MIXER-12` | `TuneLab.csproj` + `ExperimentalPatchBootstrap.cs` + `TuneLab/ExperimentalPatchInsertSettings.cs`（新） | 插入效果：宿主引用 `InsertHost`、经 `IHostServices.InsertPlugins` 递出插件扫描设置 | **已落地**：`TuneLab.csproj` 多一条 `ProjectReference`（与 `EP-MIXER-7` 同一个 ItemGroup）；`InsertHost.csproj` 用 `<Compile Include="..\VstBridge\*.cs">` **各自编译一份**桥协议与共享音频环（两端布局必须逐字节一致）；`HostServices.InsertPlugins` 返回进程内单例 `InsertPluginSettings`（实现 `Abstractions/IInsertPluginSettings`）。扫描进度：`Scan(IProgress<InsertScanProgress>?)` + `IInsertBackend.ListPlugins(dirs, onDirectory, probeInstruments, onDetail)`（**按目录**回调，两个格式的累计偏移在宿主里算）。**注意**：`ExperimentalPatch.csproj` 必须 `Compile Remove="VstBridge/**"`，否则默认 `**/*.cs` 会把桥的源码吞进 UI 补丁工程（还会因为没有 `AllowUnsafeBlocks` 而 CS0227） | 编译找不到命名空间 / CS0227 / CS0579 / 补丁窗显示"宿主尚未接入" | 宿主→补丁方向**不许反**：补丁只用 `Abstractions/IInsertPluginSettings`，不得 `using TuneLab.InsertPluginSettings`。宿主要加能力时优先往 `IHostServices` 加带默认实现的成员。`Scan` 是**同步**的，调用方负责扔后台线程（设置页就是这么做的）；`Progress<T>` 在构造它的线程上回调，所以进度回调里碰控件是安全的 |
| `EP-MIXER-13` | `InsertHost/VstBridgeClient.cs` + `VstBridge/*.cs`（均为新文件）+ `TuneLab/ExperimentalPatchMixerModel.cs` | 32 位 VST2 的桥进程：JobObject 生命周期 + 共享内存音频环 | **已落地**：宿主侧 `VstBridgeClient` 建 `JobObject`（`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`）→ 启动 `TuneLab.ExperimentalPatch.VstBridge.exe --job <句柄> --pipe <名字>` → 桥 `AssignProcessToJobObject` 后按名字 `Open` 两段共享内存。音频数据走 `SharedAudioRing`（4 槽环形 + `Volatile` 序号 + ManualReset gate），指令走命名管道 JSON。宿主的 `SendProcess` 同步等一块，`BlockWaitMs = 20` 超时即直通。`VstBridge.csproj` 的 `Platforms = x86`、`RuntimeIdentifier = win-x86`——**绝不给 `TuneLab.csproj` 加 x86 目标** | 桥起不来 / 音频卡死或撕裂 / 宿主退出后桥变孤儿 | 桥**不被 `TuneLab.sln` 引用**（与 `MixerEngine` 同策略），由发布脚本按平台单独构。改 `BridgeSharedMemory.cs` 的布局 = 两端一起改，封条是 `tests/TuneLab.Tests/SharedAudioRingTests.cs`（2 万块双线程对撞） |
| `EP-MIXER-14` | `Data/Track.cs` + `Data/ITrack.cs` + `Audio/IAudioTrack.cs` + `Data/MixerRouting.cs` + `TuneLab/ExperimentalPatchMixerInserts.cs`（新）+ `ExperimentalPatchMixerModel.cs` + `ExperimentalPatchMixerAudio.cs` + `ExperimentalPatchMixerPersistence.cs` | 插入链接进宿主模型、混音图与落盘 | **已落地**：`InsertSlot`（Id/Format/PluginId/Name/IsBypassed/State/Locator）挂在 `Track` 与 `AuxBus` 的 `Inserts`；`InsertInstanceFactory.Shared` 是**唯一**实例源（图侧编译与落盘侧采 state 必须同一份，各建一份会采到空）；`MixerGraphCompiler.BuildInserts` 把链编进节点，建不出来 → `BypassedInsert` 占位 + 日志（一个插件卸了不能让整张图失败）；`IInsertPluginTarget` 是补丁绑插件的窄口；payload 的 `inserts` 数组带 base64 `state` | 加插入崩 / 存盘后插件参数回默认 / 卸了插件整个混音台没声音 | 实例**不能**挂在 DataObject 上（undo 会把它连同原生句柄一起撤走）；插件定位信息 `Locator` 不落盘（换机器要重扫，见 DESIGN.md §9.5）；坏 base64 只丢那一段，不挡工程打开 |

| `EP-MIXER-15` | `TuneLab/ExperimentalPatchMixerModel.cs`（删掉 VST2 占位行）+ `TuneLab/ExperimentalPatchMixerInserts.cs`（注册 VST2 后端 / 按格式取路径）+ `InsertHost/{Vst2Backend,Vst2Insert,PluginBitness}.cs` + `VstBridge.X64/` + `VstBridge/{PluginHost,BridgeProgram}.cs` | VST2 端到端接上：一律过桥（x86 + x64 两个位数的桥） | **已落地**：`Vst2Backend.Create` → `Vst2Insert.Create` → `PluginBitness.Read` 选桥 → `VstBridgeClient.ForBitness(32/64).EnsureStartedBlocking()` → `LoadBlocking`；音频走 `SendProcess`（共享环），参数/状态走管道。JobObject 改为**宿主启动后自己 `AssignProcessToJobObject`**（作业句柄是进程本地的，命令行传数值只会得到 `ERROR_INVALID_HANDLE`）。桥的 stderr 被客户端接管，失败原因带进 `reason`。`VstBridge.X64` 与 x86 那个共用同一套 `.cs`（`EnableDefaultCompileItems=false` + `Compile Include="..\VstBridge\*.cs"`） | 菜单里出现两个 VST2 / 桥起来就退出 / 32 位插件报“找不到运行时” | 模型层原来的 VST2 占位行**必须删**（否则和 Backends 重复，`Single()` 直接抛）；`ExperimentalPatch.csproj` 要同时 `Compile Remove="VstBridge.X64/**"`，否则那个工目录的 `obj/*.AssemblyInfo.cs` 会被吞进来撞 CS0579；x86 桥发布要 `--self-contained`（多数机器没有 x86 .NET 运行时），两个位数分目录放（`bridge/win-x86`、`bridge/win-x64`，同名不同架构的 `hostfxr.dll` 不能混放） |

| `EP-MIXER-16` | `UI/Settings/SettingsWindow.axaml.cs` + `ExperimentalPatchBootstrap.cs` + `ExperimentalPatch/Abstractions/{IAnchorTable,SettingsTabContribution}.cs` + `ExperimentalPatch/Patches/Mixer/InsertPluginPathsPage.cs`（新） | 「设置」窗尾部接上补丁贡献的 tab（插件扫描路径） | **已落地**：`IAnchorTable.AddSettingsTab(SettingsTabContribution)` → `AnchorHub` 存清单 → `PatchHost.GetSettingsTabs()` → `ExperimentalPatchBootstrap.GetSettingsTabs()`；`SettingsWindow` 建完自己的 `tabPages` 后 `foreach` 追加（贡献快照在**每次开窗**时取，晚登记也看得到）。`SettingsTabContribution.CreateContent()` 返回 `object` 而不是 `Control`：Abstractions 里不许出现 Avalonia 类型，宿主就地转型。没有补丁 / 没有贡献 → 空循环，设置窗与上游逐项一致 | 设置窗少了一页 / 侧栏多出空白项 / 点开崩 | 追加必须在 `tabPages` 初始化**之后**、`foreach (var tabPage in tabPages)` 建按钮**之前**；内容工厂每次开窗都会新建一页（别缓存控件，那会跨窗复用视觉树）；tab 标题由补丁按 `UiLanguage` 给，**别往上游翻译表加条目**（`Tr` 查不到会原样保留）。插件扫描的路径清单与缓存仍由 `TuneLab/ExperimentalPatchInsertSettings.cs` 负责，页只经 `IHostServices.InsertPlugins` 读写 |
| `EP-MIXER-17` | `TuneLab/ExperimentalPatchMixerInserts.cs` + `ExperimentalPatch/MixerEngine/IMixInsert.cs` + `TuneLab/ExperimentalPatchInsertEditor*.cs`（4 个新文件）+ `ExperimentalPatch/VstBridge/Vst2EditorWindow.cs`（新）+ `VstBridge/{PluginHost,BridgeProtocol,BridgeSession}.cs` + `InsertHost/{Vst2Insert,Vst3Insert,VstBridgeClient}.cs` | 插件的原生编辑器（FX 浮窗那行「界面」键） | **已落地**：`IMixInsert` 加 **`SetBypassed(bool)`**（旁通是活值，不换快照）；引擎 `Mixer.RenderChunk` 逐块现读 `IsBypassed` 并**跳过**旁通的插入。宿主侧 `InsertSlotAdapter` 同时写数据槽位与活实例；`InsertInstanceFactory.TryGetLive/SetBypassed`。编辑器按格式分两条路：VST3 进程内 → `Vst3EditorWindow`（Avalonia Window 的平台句柄交给插件）；VST2 在桥进程 → `Vst2EditorWindow`（桥自己开 Win32 窗 + STA 线程跑消息循环，宿主只发 `OpenEditor` 消息）。`Vst3EditorWindow` 关窗前必须 `CloseEditor`（摘 view 早于 `effClose`）。桥侧 `PluginHost.Unload` 先关编辑器再卸插件 | 「界面」键点了没反应 / 混音台浮窗不显示 / 关插件时进程崩 | `HasEditor` 要向**活实例**要（不是恒定 false）；`SetPlugin` 之后必须重新编图（`Owner.RaiseRoutingChanged`），否则新选的插件进不了链；x86 与 x64 两个桥都要重新发布（`tools/publish-bridges.ps1`），否则老桥不认 `OpenEditor` 消息会回 Error |
| `EP-MIXER-18` | `TuneLab/Data/AudioPart.cs`（两处调用）+ `TuneLab/ExperimentalPatchAudioPartPath.cs`（新，格式全部在这里） | 音频部件在 .tlpx 里的路径格式（相对化 / 还原） | **已落地**：`GetInfo` 存、`Reload` 读，都只调 `ExperimentalPatchAudioPartPath.ToStored/FromStored`。旧写法是 `".." + path[base.Length..]`，默认 base 末尾带分隔符；工程在**盘符根目录**时（`GetDirectoryName("Y:\x.tlpx")` = `"Y:\"`）余下部分没有前导分隔符，于是存成 `..Insts\…`，读回来按 `path[3..]` 切会多切一个字符 → 定位必失败。现在编码显式补分隔符并做**目录边界**判断（`C:\Music` 不再匹配 `C:\Music2\…`），解码同时容忍老文件那种缺分隔符的形态 | 重新打开工程音频全丢 / 波形空白 / 提示找不到文件 | 改格式必须同时照顾**已经存坏**的工程（解码端容忍 `..Insts\…`）；`tests/TuneLab.Tests/AudioPartPathTests.cs` 钉住往返与两种历史形态 |
| `EP-MIXER-19` | `TuneLab/Data/Project.cs`（`MasterInserts` 槽位）+ `TuneLab/Data/ProjectDocument.cs`（接驳点）+ `TuneLab/Audio/{AudioGraph,AudioEngine}.cs` + `TuneLab/ExperimentalPatchMixer{Persistence,Model}.cs` | Master（总轨）的插入链 | **已落地**：槽位挂在 `Project.MasterInserts`（与 `Tracks`/`Auxes` 同级，undo/落盘跟着工程走），编译时经 `Compile(..., mMasterInserts)` 挂到 `master` 节点。**接进音频图的是 `ProjectDocument`，不是 `Project`**：持有者切换时先 `SetMasterInserts(null)`、再 `SetMasterInserts(Project?.MasterInserts)`。以前写在 `Project` 构造/析构里，两条日常路径都会把指针弄空 —— 打开另一个工程（新 Project 先构造、旧 Project 后 Dispose 清空）、导入轨道（`TrackScrollView` 临时 `new Project` 只为拿 TempoManager，直接抢走指针）。指针一空 master 节点就没有插入实例，`InsertSlotAdapter.HasEditor` 拿不到活实例，Master 轨 FX 的「界面」键**永远是灰的** | Master 轨加插件后「界面」一直点不动（轨道/AUX 正常）/ 工程一打开 Master 插入就失效 | `Project.Dispose` **不要**再去清全局指针（它清的是别人的）；`Project` 构造也**不要**接管（临时工程会抢）。判据是"当前文档的工程"，只有 `ProjectDocument` 知道。`HasEditor` 依赖 `InsertInstanceFactory.TryGetLive`，所以现象是"静默灰键"而不是报错 —— 排查先看 master 节点有没有被编译出插入链 |
| `EP-MIXER-21` | `TuneLab/Data/MixerRouting.cs`（`Send` 加 3 个字段）+ `TuneLab/ExperimentalPatchMixer{Model,Persistence,Audio}.cs` + `ExperimentalPatch/Abstractions/Mixer/IMixerModel.cs` + `ExperimentalPatch/MixerEngine/{MixNode,MixGraphBuilder,Mixer,IMixEdgeGain,DelayLine}.cs` + `ExperimentalPatch/Patches/Mixer/{SendPopupWindow,MixerMetrics}.cs` | send 的 pre/post、声向、静音（roadmap 2.2） | **已落地**：`Send` 加 `IsPreFader` / `Pan` / `IsMute` 三个 DataProperty；`SendEdgeGain` 实现 `IMixEdgeGain` 新加的 `LeftGain` / `RightGain` / `IsSilent`（都带 DIM 默认值，老实现不受影响）。引擎侧 `MixEdge.IsPreFader` + `MixNode.HasPreFaderOutput`（建图时算一次，音频线程只读）。**关键**：有 pre-fader 边时 `Source.AddData` 改用 unity 读、推子留到 tap 之后才上 —— 折叠的话 buffer 一诞生就带推子，再也取不回推子前的信号。没有 pre-fader 边时走原折叠路径，退化图与旧版逐位相同（`DegenerateGraph_IsBitIdenticalToLegacyMix` 钉住） | pre-fader 的 send 跟着推子一起变小（= 推子没拆出来）/ 一份信号送两遍（post-fader 分发没跳过 pre-fader 边）/ send 声向不生效 | post-fader 分发循环必须 `if (edge.IsPreFader) continue;` —— 两条路读同一份 target，只是时机不同；`SendDto` 新字段缺省即旧行为（post-fader / 居中 / 不静音） |
| `EP-MIXER-22` | `ExperimentalPatch/MixerEngine/{LoudnessMeter,Mixer}.cs` + `ExperimentalPatch/Abstractions/Mixer/IMixerModel.cs` + `TuneLab/Audio/{AudioEngine,AudioGraph}.cs` + `TuneLab/ExperimentalPatchMixerModel.cs` + `ExperimentalPatch/Patches/Mixer/MixerChannelStrip.cs` | 响度表：LUFS / True Peak（roadmap 2.3） | **已落地**：引擎侧 `LoudnessMeter`（K 加权两级 = BS.1770 公布的 48 kHz 系数 + 按双线性预 warp 换采样率；400 ms 块 75% 重叠；两级门限 −70 / 相对 −10；True Peak 走 4× 多相 FIR）。`Mixer.Runtime` 每节点一份，构造期分配、音频线程只读，与 `Hpf` 同一套纪律。读数在 `MeasurePeak` 旁边取（推子后、插入链前）。宿主侧 `AudioEngine.InvokeRealtimeChannelLoudness` / `InvokeRealtimeTrackLoudness`（与峰值同一套"只有播放中才有意义"的规矩），`IMixerChannel.Lufs` / `TruePeakDb` 可空。界面：Master 的推子读数行右侧显示 LUFS（> −9 变红），没有读数时显示占位符而不是 0 | 读数恒为 0 / 一直 -- / 与参考实现对不上 / 44.1k 与 48k 差 8% | BS.1770 的搁架 **S = 0.5** 且 `b0 > 1`（标准不把 a0 归一成 1）—— 按 cookbook 自己推会得到发散的滤波器；参考方波是 **997 Hz**，用低频方波会被 K 加权高通整段滤掉（读数低 16 dB） |
| `EP-MIXER-23` | `ExperimentalPatch/MixerEngine/{WetDryInsert,TrimInsert}.cs`（新）+ `TuneLab/Data/MixerRouting.cs`（`InsertSlot` 加 2 字段）+ `TuneLab/ExperimentalPatchMixer{Audio,Inserts,Persistence}.cs` + `ExperimentalPatch/Abstractions/Mixer/IMixerModel.cs` + `ExperimentalPatch/Patches/Mixer/{FxPopupWindow,MixerMetrics}.cs` | 插入的湿/干混合 + 增益微调（roadmap 2.4） | **已落地**：`WetDryInsert` / `TrimInsert` 两个包装器放在引擎层（三个后端零改动，且旁通/顺序/PDC 全自动继承）。**默认值完全透明**：mix = 1 时连干信号拷贝都跳过，gain = 0 dB 时连一次乘法都不做 —— "没用这两个功能"的工程与旧路径逐位相同。都是**活值**（音频线程每块现读 volatile 字段），拖一下不必重建图。宿主侧 `InsertInstanceFactory` 多一张 `mWrapped` 表：`mLive` 存裸插件（编辑器/参数/旁通用），`mWrapped` 存链上那一环（推 mix / trim 用）；编译还没跑过时表是空的，`SetMix` 静默返回即可。落盘 `InsertDto.Mix`（缺省 1）/ `GainDb`（缺省 0） | 拖 mix 没反应（值烘在快照里了 / 包装器没登记）/ 全干时混响尾巴断了（mix = 0 时把插件旁通了）/ A/B 对比听不出差异（trim 缺省不是 0） | 包装顺序**trim 在湿/干之后**：trim 的用途是"插入让电平变了，把它调回原位"，而"变了"的量本来就是混合后的结果；反过来做会让 mix = 0 时 trim 突然失效；`InsertSlot.Mix` 构造时 `SetInfo(1)`（DataStruct 默认 0 = 全干，不是想要的值） |
| `EP-MIXER-24` | `ExperimentalPatch/MixerEngine/{PanLaw,Mixer}.cs`（新）+ `TuneLab/Audio/{AudioEngine,AudioGraph}.cs` + `TuneLab/ExperimentalPatchBootstrap.cs` + `ExperimentalPatch/Abstractions/{IHostServices,Mixer/IMixerModel}.cs` + `ExperimentalPatch/Patches/Mixer/MixerWindow.cs` | 声向法则选项（roadmap 2.5） | **已落地**：`PanLaw` 枚举 + `PanLawMath.Resolve`（纯函数）。`Mixer.PanLaw` 属性，音频线程每块现读。三个法则：Linear0Db（默认 = 上游老公式）、ConstantPower3Db、ConstantPower4_5Db。**默认值必须保持逐位相同** —— DESIGN §7.2 的硬验收，`MixerEngineTests.DegenerateGraph_IsBitIdenticalToLegacyMix` 钉住。补丁工程看不到 MixerEngine 类型（独立工程，只有宿主引用），所以契约上是 `IHostServices.PanLaw` / `SetPanLaw(string)`，取值 "linear" / "power3" / "power4.5" | 切了等功率没声音变化（值烘进快照了）/ Master 整条母线被压低 3 dB（Master 没有 pan，却对它用了 pan law）/ 破坏了逐位相同（默认值不是 Linear0Db） | **Master 节点必须永远走 Linear0Db** —— 它是总线没有声向，用 pan law 会让 pan = 0 在等功率下算出 0.7071，等于 Master 推子变成额外 −3 dB 衰减；上游老公式本身有个怪癖（硬偏时那一侧 +6 dB），这是 §7.2 要求保留的，等功率法则是给用户的**选项**而非修正 |
| `EP-MIXER-25` | `TuneLab/Data/{Track,ITrack,MixerRouting}.cs`（`IsSoloSafe`）+ `TuneLab/Audio/IAudioTrack.cs` + `TuneLab/ExperimentalPatchMixer{Audio,Model,Persistence}.cs` + `ExperimentalPatch/Abstractions/Mixer/IMixerModel.cs` + `ExperimentalPatch/Patches/Mixer/{MixerChannelStrip,MixerWindow}.cs` + `ExperimentalPatch/Patches/Mixer/Controls/MixerToggle.cs` | Solo 安全 + 独占 solo（roadmap 2.6） | **已落地**：`IsSoloSafe` 挂在 `Track` / `AuxBus`（DataProperty，随 epMixer 落盘）；`IAudioTrack` 用 DIM 默认 false，老实现不受影响。判据改为 `!IsSolo && (IsMute \|\| (HasSolo && !IsSoloSafe))` —— **solo-safe 只豁免"被别人的 solo 压制"，不豁免自己的静音**。独占 solo 是 `MixerModelAdapter.ExclusiveSolo`（运行期开关，不落工程：工作流偏好），`ClearOtherSolos` 只清**同域**（Track↔Track、AUX↔AUX），跨域不清。界面：右键 S = 切换 solo 安全（角标画在右上角，不占宽度），标题栏 `Solo+` 按钮 = 独占开关 | 标了 safe 之后按静音没反应（豁免范围搞错了）/ solo 一条轨把 AUX 的 solo 也清了（跨域清了）/ 老工程读进来 solo 行为变了（DTO 缺省值不是 false） | 判据只在宿主侧（`TrackChannelState.IsSilent`），引擎不认识 solo；`MixerToggle.IsMarked` 是新加的视觉态，与 `IsChecked` 正交（solo 中的安全通道两者同时为真） |
| `EP-MIXER-26` | `TuneLab/Data/{MixerRouting,Project,Track,ITrack}.cs`（`VcaBus` / `Project.Vcas` / `VcaId`）+ `TuneLab/Audio/{IAudioTrack,AudioGraph,AudioEngine}.cs` + `TuneLab/Data/ProjectDocument.cs` + `TuneLab/ExperimentalPatchMixer{Audio,Model,Persistence}.cs` + `ExperimentalPatch/Abstractions/Mixer/IMixerModel.cs` + `ExperimentalPatch/Patches/Mixer/{MixerChannelStrip,MixerWindow}.cs` + `ExperimentalPatch/UI/Palette.cs` | VCA 控制组（roadmap 2.7） | **已落地**：`VcaBus`（id / 名字 / 增益 / 静音）挂在 `Project.Vcas`，随 epMixer 落盘（`VcaDto` + `TrackDto.VcaId` / `AuxDto.VcaId`）。**混音图里没有 VCA 节点** —— 编译器把 `VcaId` 解析成对象交给 `TrackChannelState` / `AuxChannelState`，状态在 `Volume` 里乘、在 `IsSilent` 里或静音。界面：`+ VCA` 按钮、琥珀色 VCA 条（只有 标题 / M / 推子，行高总和与普通条相同所以推子对齐）、右键「分配到 VCA」单选子菜单 | 删了 VCA 音量没回来（状态握着对象引用，`RemoveVca` 必须 `RaiseRoutingChanged`）/ 删了 VCA 后成员还留着脏 id（要在同一个 merge 里清 `VcaId`）/ VCA 静音把非成员也哑了（`IsSilent` 里写成无条件）/ 改了归属但声音没变（`VcaId` setter 必须重建图） | **Group 不需要新模型** —— AUX 就是子编组，ROADMAP 旧结论已更正；VCA 是控制组（音频不流过它）。`ProjectDocument` 在 `WillModify` 里 `SetVcas(null)`、`Modified` 里 `SetVcas(Project?.Vcas)`（与 MasterInserts 同一套生命周期）。一个通道只属于一个 VCA；VCA 没有 solo / pan / 插入 |

## 3. 重接流程（merge / rebase 之后照这个顺序）
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
