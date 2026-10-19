# 多轨混音台（Mixer）— 设计稿 v2

> 状态：**v2 设计稿**（2026-10-19）。v1 的外观稿已落地；v2 加了**路由（AUX / output / send）与插入效果**，
> 并已出第二版外观稿（通道条三行 + 两个浮窗 + output 菜单）。§12 是决策记录，§10 是分阶段计划。
>
> 参考对象：Adobe Audition 的多轨混音台。参考的是**术语、信息层级与交互**，不是照抄像素。

## 0. 一句话

每个通道（轨 / AUX / Master）是一个**节点**：进 → 插入效果 → 推子 → 出。
"出"可以去 Master、去某条 AUX（= 子混音），或直接去音频设备；
另外可以**并联**若干 send 到别的 AUX / Master，每条 send 有自己的增益。
AUX 与 Master 是同一类东西（收别人的音频、混合、再往下送），Master 是终点（只能去设备）。

## 1. 功能清单（逐条对应你的描述）

| # | 你要的 | 设计里落成 |
|---|---|---|
| 1 | 除 track / master 外有 AUX，可增删 | §4.1：AUX 是一等通道，数量不限；增删进 undo、进工程 |
| 2 | 每个 track / AUX 都能选 output，默认 Master，可选 AuxN / Master | §4.2：output 单选，图里一条边 |
| 3 | track / AUX 还能设 send 到多个 AuxN / Master，每条独立增益 | §4.3：send 多选，post-fader 抽头，每条一条边 + 自己的增益 |
| 4 | 增益 / 减益建立在轨道自身 vol 之后 | §4.3：抽头 = 插入之后、推子之后（post-fader） |
| 5 | 接收方把收到的波形与自己的音频直接混合 | §3.2：目的地输入缓冲累加，不做额外处理 |
| 6 | AUX 与 master 差不多：收 → 混 → 再 output | §4.1：同一条处理路径，Master 只是"只能去设备" |
| 7 | Master 的 output 只能是音频设备（settings 里的默认设备） | §4.4 |
| 8 | AUX / track 也能 output 到音频设备 | §4.4（v1 只有一个设备，理由见 §4.4） |
| 9 | daw_cable 保留、先不实现 | §4.5：菜单里有这一类，里面是"未实现" |
| 10 | output 是下拉，一级四类 Aux / Master / Device / Cable | §4.6 |
| 11 | send 按钮在 M/S 上方，按下弹浮动 list 窗；无 send 显示黑色（禁用配色），有则高亮 `Send N` | §6.1 / §6.2 |
| 12 | send 下面同样一个按钮，浮动 effect 窗 | §6.1 / §6.3 |
| 13 | 效果：VST2.4（Win，VST.NET2）/ VST3（全平台，OwnAudioVST3，隔离）/ JSFX（全平台，GUI + 随 tlpx 持久化） | §5 |

## 2. 事实基础（宿主现状 —— 决定了工作量）

| 事实 | 出处 | 对本设计的意义 |
|---|---|---|
| 混音是**单趟累加**：`MixData` 遍历所有轨，`AddData` 直接写进同一个输出缓冲 | `Audio/AudioGraph.cs:48-110` | 没有节点、没有总线缓冲、没有拓扑序。加 AUX 就得重写这一层（§3） |
| 播放回调：先 `MixData` → 再 `AudioPlayer.AddData`（预览音）→ 再乘 `MasterGain` | `Audio/AudioEngine.cs:466-493` | Master 增益是"事后乘一下"，不是一条通道 |
| 导出：`ExportTrack` 走 `AddData`（单轨，**忽略** mute/solo）；`ExportMaster` 走 `MixData`（**忽略** `MasterGain`） | `AudioEngine.cs:138-206` | Master 一旦有插入，导出必须跑同一条链（§3.8） |
| 逐轨电平：`InvokeRealtimeAmplitude` 用 `AddData` 取 64 sample 窗峰值；静音 / 未 solo 返回 null | `AudioEngine.cs:208-240` | 新模型里"通道电平"改成从节点输出取（§3.4） |
| **只有一个输出设备**：`SetDeviceToPlaybackHandler` 挑一个 | `AudioEngine.cs:442-456` | 多设备输出是另一块工作（§4.4） |
| 已有 `Effect`，但它是**离线整段变换**（SVC 一类），挂 `MidiPart`，文档明确"不是实时 VST" | `docs/effect-migration.md`、`docs/plugin-development.md:773` | **命名必须分开**：新东西叫"插入效果 / Insert"（§5.1） |
| `.tlpx` = CBOR + `ProjectInfo`，有版本字段与 legacy 分支；每轨还有 app 私有的 `NativeTrackInfo` | `Extensions/Formats/TLP/TuneLabProjectCbor.cs`、`Data/NativeTrackInfo.cs` | 路由 / 插入塞进 `NativeTrackInfo` → **不动 SDK 冻结面**（§9） |
| `Track` 的 Gain/Pan/IsMute/IsSolo 都在 `DataObject` 上 → undo / 序列化 / 脚本全部免费 | `Data/Track.cs`、`Data/ITrack.cs` | AUX / send / insert 照这个做，别自己造一套状态（§3.9） |

**结论**：v1 那个"只做视图"的前提到此结束。路由与插入是**引擎功能**，补丁层只能保留界面（§7）。

## 3. 新音频模型

### 3.1 节点与边

```
  轨道节点 ──insert──> 推子(vol/pan) ─┬─ output ───────────────> Master / AuxN / Device
                                       └─ send × N（各自增益）──> AuxN / Master

  AUX 节点：输入缓冲 ──insert──> 推子(vol) ─┬─ output ──> Master / AuxN / Device
                                            └─ send × N ──> AuxN / Master

  Master 节点：输入缓冲 ──insert──> 推子(master gain) ──> 设备（唯一出口）
```

- 节点种类：`TrackNode`（音源求和）、`AuxNode`、`MasterNode`、`DeviceSink`、`CableSink`（保留，不实现）。
- 边：每个通道 **1 条 output** + **N 条 send**，每条边一个增益（dB）。
- 抽头位置（v1 固定）：**插入之后、推子之后**（post-fader）——就是你说的"建立在轨道自身 vol 之后"。
  pre-fader send 留 v2（Audition 的 send 有这个开关）。
- AUX 与 Master 同构：都是"输入缓冲 → 插入 → 推子 → 分发"。差别只有两点：Master 出口只能是设备；
  Master 的推子就是 `MasterGain`。

### 3.2 每个块怎么算

1. 清空所有节点的输入缓冲（预分配；块大小 = 设备块大小）。
2. 按**拓扑序**遍历节点：
   - 轨道节点：把该轨的音源按当前 gain / pan 求和进自己的缓冲（就是今天的 `AddData`）；
   - 任何节点：跑插入链（就地）→ 乘推子 → 把结果按各条边的增益**累加**到目的地节点的输入缓冲。
3. 设备 sink：把 Master 的输出交给播放器；`AudioPlayer` 的预览音也累加在 Master 输入上（与今天一致）。
4. 每个节点的输出同时是它的**电平表抽头**（§3.4）。

缓冲数量 = 节点数 × 2 声道 × 块长。每轨 1024 样本 × 2 × 4B ≈ 8KB，可以忽略。

### 3.3 静音 / 独奏的门控位置

- 门控在**源头**（通道节点），不在总线上：一条轨被静音 / 被 solo 压制，它的 output 与 send **一起**哑掉。
  这与"你听到的就是你看到的"一致，也避免"静音了但 AUX 里还在响"这种反直觉。
- **solo 有两个独立域**（AUX 也有 M/S，但它的 solo 只在 AUX 之间生效）：

| 域 | 成员 | 判据 | 影响谁 |
|---|---|---|---|
| 轨道域 | 所有 Track | 有任一轨 solo → 未 solo 的轨全哑 | 只影响 Track |
| AUX 域 | 所有 AUX | 有任一 AUX solo → 未 solo 的 AUX 全哑 | 只影响 AUX |

  - 两个域互不影响：AUX solo **不会**压掉轨道，也不影响 Master（Master 没有 solo）。
  - 这是按你的要求定的；代价是"AUX 1 solo 时轨道仍在响"，所以 AUX 的 solo 只适合"单独听某条混响"，
    不是"只听这一条"。UI 上不额外解释，S 键亮着就说明它只在 AUX 域里生效。
- Master 没有 mute / solo。

### 3.4 电平表从哪取

- 通道表 = 该节点**推子之后、路由之前**的信号（= 它自己贡献了多少），与今天 `InvokeRealtimeAmplitude` 的语义一致。
- Master 表 = Master 推子之后的最终输出（这就是原先规划的那个"Master 电平查询"）。
- 静音 / 未 solo 时仍返回 null → 表变黑（v1 已定）。

### 3.5 延迟补偿（PDC）

插入效果会报延迟（VST3 `getLatencySamples`、VST2 `initialDelay`）。
**不做 PDC 的后果**：一条轨既 output 到 Master、又 send 到 AUX（AUX 上有延迟插件），
两条路在 Master 汇合时差几毫秒 → 梳状滤波，听感是"薄、空心"。

- 每个节点上报延迟 = 插入链延迟之和。
- 建图时算：到每个 sink 的最长路径延迟；每条入边补 `最长 − 本路径` 个样本的延迟线。
- 延迟线按块处理、预分配、只做整数样本（v1 不做分数延迟）。
- 没有插入时全部为 0 → 行为与今天一致。

### 3.6 环路

- 只有 AUX 之间可能成环（轨道没有输入）：`Aux1 → Aux2 → Aux1`。
- 编辑时就检测（改 output / 加 send 时做一次 DFS），成环则**拒绝并提示**，不落进模型。
- 自环（`Aux1 → Aux1`、send 到自己）同样拒绝。

### 3.7 实时安全

- 图变更（改路由、加删 AUX、加删插入）全部发生在 UI 线程；音频线程只读一份**不可变快照**，
  用一次原子换指针切换（RCU 风格）。换下来的旧快照延迟到音频线程不再持有才释放。
- 音频回调里**不分配、不加锁、不碰 UI 对象**。现在 `AudioGraph` 在回调里 `lock(mTrackLockObject)`，
  改造时一并去掉（改成快照）。
- 插入的加载 / 卸载（扫描、实例化）都在 UI 线程，装好再换快照。

### 3.8 导出

- `ExportMaster` = **跑同一条图**到文件 sink。这样"导出 == 听到的"，也是 Master 插入能进文件的唯一办法。
  ⚠️ 这会顺带让 `MasterGain` 进导出（今天是忽略的）。因为 Master 从来没有 UI，实际影响面接近零，
  但它是**行为变更**，要你确认（§12 Q3）。
- `ExportTrack` = 只跑该轨节点（插入之后、推子之后、路由之前），**忽略** mute/solo ——
  与今天 `AddData` 的单轨导出语义一致，只是现在能说清它是什么：**stem 导出，不是混音**。
- 分轨批量导出不在 v1。

### 3.9 状态放在哪

- AUX、send、insert 都做成 `DataObject` + `DataProperty`（与 `Track.Gain` 同一套），
  于是 undo、序列化、脚本可见**全部免费**。
- 唯一例外：插入的**参数状态**（VST state chunk / JSFX slider 值）不逐参数进 undo，
  而是插入对象上的一段二进制 + 版本号；参数由插件 GUI 直接写，保存工程时落盘（多数 DAW 就是这样）。
  加删插入、bypass、换序是 undo 的。

## 4. 路由

### 4.1 通道类型

| 类型 | pan | mute/solo | send | 出口 | 备注 |
|---|---|---|---|---|---|
| Track | ✓ | ✓（轨道域） | ✓ | Aux / Master / Device / Cable | 音源来源 |
| AUX | ✓ | ✓（**AUX 域**，§3.3） | ✓ | Aux / Master / Device / Cable | 纯总线，可增删 |
| Master | ✗ | ✗ | ✗ | Device / Cable | 终点，唯一 |

- AUX 的 pan 是"母线平衡"（沿用同一套线性 pan 律：L=(1−p)、R=(1+p)）。它作用在**总线输出**上，
  不是作用在每个输入上——所以从两条轨送进同一条 AUX 的声音会被同一个 pan 一起推。

- AUX 数量不限（工程里一个有序表）。命名自动：`AUX 1`、`AUX 2`…（v1 不做重命名）。
- **删除 AUX 时**：所有指向它的 output / send 改指 Master，并在窗口里提示"3 条路由已改指 Master"。
  静默丢掉路由比报错更糟。
- AUX 也是 `DataObject` → "新增 AUX"就是往有序表里插一个对象 = 一步 undo。
- `AuxNode` / `MasterNode` 与 `TrackNode` 共用一个 `ChannelNode` 基类（推子、插入链、路由、电平抽头都在基类上；
  Track 只多一个"音源求和"、AUX/Master 只多一个"输入缓冲"）。

### 4.2 output（单选）

- 每个通道一条，默认 `Master`。
- 改 output = 改图里的一条边，换快照。`Track.Output` 是 `DataProperty` → 一步 undo。
- 不允许指向自己或造成环（§3.6）。

### 4.3 send（多选，post-fader）

- 每个通道可挂多条 send，目的地是任意 AUX 或 Master，**每条自己的增益**。
- 抽头在推子之后（你说的"在轨道自身 vol 之后"）。
- 增益域 **−∞..+12 dB**。为什么不是 +6：send 是"再多送一点"，+12 留出把一条轨大量推进混响的余地；
  上限固定后横推子的刻度才好定。**增益与减益是同一个推子**，不是两个参数。
- 目的地收到后**直接累加**进它的输入缓冲（"和轨道当前的音频直接混合"），不做任何额外处理。
- 同一通道对同一目的地只允许一条 send（否则是两个增益叠一起，没有意义）。
- 增删与增益都是 `DataProperty` → undo。

### 4.4 设备输出

- **v1：只有一个设备** —— settings 里选的那个。`Device` 类下面列的就是它（外加"未连接"空态）。
  理由：两个设备 = 两个时钟，长时间播放必然漂移；要解决得给每个设备做异步重采样 / 丢补样。
  那是独立的一块工作，不该混进 v1。
- 所以 v1 的 `Device` 输出 = **绕过 Master、直接进默认设备**。实际用途是"把某条轨 / 某条 AUX 单独送出去"，
  与 Master 共用同一个物理设备（混在同一个流里）。**注意：它不经 Master 推子**，
  这一点必须在 UI 上可读（§6.4）。
- 多设备留 v2：每个设备一个 sink + 漂移补偿。**用途明确是监听**（例如主输出走音箱、某条轨或某条 AUX
  单独走耳机），不是"多房间扩声"；所以 v2 可以接受"监听端允许极少量丢补样"的简化漂移补偿。

### 4.5 daw_cable（保留）

- 菜单里保留 `Cable` 这一类，里面只有一条禁用项"（未实现）"。
- 模型里留 `OutputKind.Cable` 与 `CableSink` 的空壳，但不建、不序列化、不显示端口。
- 将来它就是一个"虚拟设备 sink"：把音频写进共享内存 / 命名管道，另一个 daw_client 读。
  需要先定协议（采样率、时钟、缓冲深度），比多设备更靠后。

### 4.6 output 菜单结构

```
┌──────────────────────────┐
│ Aux    ▸  AUX 1 / AUX 2 …│   ← 没有 AUX 时禁用
│ Master ▸  Master         │   ← 单选，通常就是它
│ Device ▸  默认设备名      │   ← v1 只有一项
│ Cable  ▸  （未实现）      │   ← 禁用
└──────────────────────────┘
```

- Avalonia `MenuFlyout` + 子菜单，一级就是这四类（你说的"第一级分为四类"）。
- 当前选中的一项打勾。
- 通道条的 output 行显示当前目的地短名（`Master` / `AUX 1` / `设备`），点击弹这个菜单。

## 5. 插入效果（Insert）

### 5.1 先解决命名

宿主里已经有 `Effect`：**离线整段变换**（SVC 换声那类），挂 `MidiPart`，`IEffectEngine`，
文档明确"不是实时 VST"。你要的是**实时插入**，两者不是一回事。

- 中文：**插入效果**（Audition 的 Effects Rack 里那类）。
- 代码：`IInsert` / `InsertSlot` / `InsertChain` / `InsertFormat`。**不复用 `Effect` 这个词。**
- `docs/naming-glossary.md` 加一行，免得以后有人混着叫。

### 5.2 三种格式，三条路线（风险差别很大）

| 格式 | 范围 | 库 | 现实评估 |
|---|---|---|---|
| VST2.4 | **仅 Windows** | VST.NET2（托管封装）+ **桥进程**（§5.3） | 可行。VST2 SDK 早已停止发放许可，VST.NET2 自带头文件；我们只做运行时加载、不分发 SDK。**x86 老插件必须走 32 位桥进程** |
| VST3 | 全平台 | OwnAudioVST3（带托管程序集，必要时工程自编译） | 可行，但**放到 v2/v3**：先把混音器做完（§10 的阶段划分）。托管程序集的版本冲突用独立 ALC 隔离（§5.4） |
| JSFX | 全平台 | 需要 EEL2 解释器 | **最大风险**：没有现成 .NET 实现。要么包 `ysfx`（C++ 解释器，P/Invoke），要么自己写（数月）。见 §12 Q4 |

- 三条路线共用一层**中间契约** `IInsertHost`：`Process(block)`、`LatencySamples`、`OpenEditor()`、
  `SaveState()` / `LoadState()`、`Parameters`。上层（图、UI、持久化）只认它，不认 VST2 / VST3 / JSFX。
- 扫描 / 枚举插件单独线程 + 缓存（一次几十毫秒到几秒），不阻塞 UI。

### 5.3 VST2 的位数：x86 老插件与 AnyCPU

- VST2 **只在 Windows 上出现**（其他平台菜单里没有这一类）。
- 位数必须与承载它的进程一致：x64 的进程 `LoadLibrary` 不了 32 位插件。而大量经典 VST2
  恰恰是 32 位的——"只支持 64 位"等于放弃一半老插件。
- **方案：桥进程（out-of-process bridge）。** 每个位数一个极小的宿主进程：

```
  TuneLab (AnyCPU)                      桥进程（x86 或 x64）
  ┌────────────────┐   共享内存（音频）  ┌──────────────────┐
  │ Vst2BridgeInsert│◀─────────────────▶│ VST.NET2 + 插件   │
  │  （实现 IMixInsert）│  命名管道（控制） │  process/editor   │
  └────────────────┘                    └──────────────────┘
```

  - 主进程保持 **AnyCPU**，发布时按 RID 出 x64 / arm64，与今天完全一致；
  - 桥是独立 csproj，`PlatformTarget = x86`（另一个是 x64），**只在需要时由 VST2 加载器启动**；
  - 32 位插件 → 起 x86 桥；64 位插件 → 起 x64 桥。
- **为什么 VST2 一律走桥**（而不是"64 位就在进程内"）：
  ① 一套代码路径，行为一致；② VST2 是遗留格式，崩溃概率高于 VST3，隔离收益大；
  ③ 主工程不必引用任何与位数相关的原生库 → **AnyCPU 这条不变量不被破坏**。
- **共享内存**：环形缓冲；块大小 / 采样率在握手时协商。桥的往返延迟（约一个块）计入 PDC（§3.5）。
  断线 / 超时 = 该插入 bypass + 标红，**不静默静音**。
- **构建纪律**：桥是独立工程，`Platforms = x86;x64`；`dotnet build TuneLab.sln` 默认配置不编它，
  由发布脚本按平台编。**绝不给 `TuneLab.csproj` 加 `x86` 目标或 `PlatformTarget`**——
  那会把整个 AnyCPU 工程钉死在一个位数上。

### 5.4 隔离（VST3 那条特别要求）

"避免与其他引用该库的插件冲突"要分两层看：

- **托管层**：把 OwnAudioVST3 连同它的依赖装进独立的 `AssemblyLoadContext`
  （可回收、自己的依赖解析），与别的扩展各自一份 → 版本冲突解决。
- **原生层**：ALC **管不到**。如果这个库带一个原生 dll（VST3 host 的 native 部分），
  两个版本同时加载就会按文件名撞车。可选解法：① 原生 dll 改名带版本号；② 插入宿主放进**独立进程**。
- 建议：v1 做 ALC + 改名；**独立进程**留 v2（它同时是"插件崩了不带走应用"的正解）。
- 插件崩溃：v1 承认"在进程内，插件崩 = 应用崩"，写进 §11 风险，不做假承诺。

### 5.5 界面

- VST2 / VST3：插件的**原生编辑器窗口**（VST2 `effEditOpen`、VST3 `IPlugFrame`）。
  v1 用**独立浮窗**（简单、跨平台），不做嵌进 Avalonia 窗口的子 HWND。
  注意：VST2 走桥进程时，编辑器窗口在桥进程里，主进程只负责"把那个窗口带到前面"
  （跨进程 `SetParent` 不可行，所以是"桥自己开窗 + 主进程激活"）。
- JSFX：`@gfx` 画到位图上贴进 Avalonia（ysfx 有 gfx 缓冲）；没有 `@gfx` 的用参数滑杆列表。

### 5.6 持久化（随 tlpx）

- 每个插入存：`format`（vst2 / vst3 / jsfx）、`pluginId`、`bypass`、`state`（byte[]）。
- VST2 = `getChunk` / `setChunk`；VST3 = `IComponent::getState` / `setState`；JSFX = slider 值 + `@serialize` 段。
- 存进 `NativeTrackInfo` / AUX 的 native DTO（§9），**不进 SDK 的 `TrackInfo`**。
- state 可能几百 KB，CBOR 里就是 byte string，先不压缩。
- 插件找不到时：**保留槽位**、标红、bypass 直通，提示一次。绝不静默丢链——
  那会让工程听起来变了却不知道为什么。

### 5.7 实时安全

- `Process` 在音频线程：不分配、不加锁、不碰 UI。参数改动走**无锁参数队列**或原子写。
- 加删插入 = 换快照（§3.7），不在音频线程里构造 / 析构插件。
- 延迟变化（换插件、改 lookahead）→ 重建图并重算 PDC。

## 6. 界面

### 6.1 通道条（新布局）

```
┌─ 84px ─────────────┐
│ ① 主唱   （色条）   │  22
│ Lead Vocal          │  20
│      ●(pan)         │  42    ← 仅 Track
│ [ Send 2 ]          │  22    ← 无 send = 禁用黑；有 = 高亮 + 数量
│ [ FX 1 ]            │  22
│ [M] [S]             │  22    ← 仅 Track
│ ┌───┐ ▍▍            │
│ │推子│ ▍▍           │  *     ← 推子 + 双通道表
│ └───┘ ▍▍            │
│      -3.0           │  24
│ → Master        ▾   │  22    ← output 下拉
└─────────────────────┘
```

- 顺序按你的描述：**send 在 M/S 上方，effect 紧接在 send 下面**（两个都压在 M/S 之上）。
- 按钮文本 `Send N` / `FX N`；N = 0 时禁用（`Palette.Back` 底、`TextLabel` 字），
  N > 0 时高亮（`Palette.Item` 底、`HighLight` 字）——一眼看出这条通道挂了几条 send / 几个插入。
- AUX 条：**与 Track 同一套行**（有 pan / M / S），只是色条用固定的 AUX 色、标题 `AUX n`。
  AUX 的 solo 只在 AUX 之间生效（§3.3），界面上不做额外标注。
- Master 条：**也能挂插入**（母带链），但没有 pan / send / M S —— 那几行**留空**，
  不是画假控件；留空是为了让推子与轨道条对齐（比较电平的前提）。
- 条高：固定行 22+20+42+20+20+22+24+22 = 192，剩下给推子。默认窗口 660 高时推子约 408px。

### 6.2 send 浮窗

- 点 `Send N` → 一个**独立浮动小窗**（非模态、可一直开着、跟着调音台窗口）。
- 每条 send 一行：目的地名（`AUX 1`）+ 横向增益推子 + 当前 dB + `✕` 移除；
  底部 `+ 添加 send ▾`（菜单列 Aux / Master）。
- 增益推子量程沿用 §4.3（−∞..+12）。
- 不是"确认 / 取消"式：改动即时生效（与调音台其它控件一致），undo 粒度 = 一次拖动。
- 关掉调音台窗口时，浮窗一起关。

### 6.3 插入效果浮窗

- 点 `FX N` → 同样风格的浮窗。每条插入一行：序号、名称 + 格式小标签、`旁通`、`界面`、`✕`。
  - `旁通` = **跳过这个效果器**（音频直通），槽位与顺序都不变；按下后该行变暗、按钮保持按下态。
  - `界面` = 打开插件自己的设置窗；插件没有界面（或 JSFX 没有 `@gfx`）时按钮置灰。
- 顺序 = 处理顺序（从上到下）。v1 只做"加到末尾"与"移除"；调整顺序留到 M7（▲▼ 或拖拽）。
- 底部 `+ 添加效果 ▾`：按格式分组（VST3 / VST2 / JSFX），不可用的格式禁用并写明原因
  （`仅 Windows` / `尚未实现`）。
- 加载失败 / 文件缺失：行内标红 + 自动旁通，给"重新定位"入口（v1 先只提示）。

### 6.4 窗口尺寸

- 无边框窗没有系统可拖的边角，所以自己铺一层**透明命中区**：左右两条边（宽 5）、下边（高 5）、
  两个下角（14×14）。拖左右边 = 改宽度；拖下边 / 下角 = 改高度；拖标题栏 = 移窗。
- 最小 420×420，默认 1180×660。
- 尺寸记在 `{DataFolder}/mixer-window.json`（受 `TUNELAB_DATA_DIR` 重定向），关闭时写、打开时读。
- 两个坑记在这里，免得下次再踩：
  ① 命中区必须用**透明 Border**（`Background = Transparent` 才参与命中）；第一版用 Path 画把手，
     Path 只有描边能命中，那条 1px 斜线实际上拖不到。
  ② 拖左边 / 左下角时窗口原点也在动，增量必须用**屏幕坐标**算（再按 `RenderScaling` 换回 DIP），
     用窗口内坐标会把"指针位移"和"窗口位移"叠在一起。

### 6.5 通道状态怎么读

- 直接进设备的通道（绕过 Master）：output 行显示 `设备`，条头加一个小标记 `DEV`，
  提醒"这条不受 Master 推子影响"。
- 被静音 / 未 solo：表变黑（v1 已定），M/S 按钮自身状态已说明原因。
- AUX 没有输入时：表全黑，正常。

### 6.6 AUX 的增删

- 调音台标题栏加 `+ AUX`（新建，插在 AUX 区末尾）。
- 删除：AUX 条右键菜单 `删除此 AUX`（要确认，因为会改路由，§4.1）。
- v1 只放调音台，不在编辑器轨道列表里重复开一个入口（两处 UI 容易两套状态）。

## 7. 引擎放哪、怎么接（已定）

**决定**：引擎代码**放在 `ExperimentalPatch/` 下**，但是一个**独立工程**（`MixerEngine`），
宿主反过来引用它，只在少数几个位置"接驳"。

为什么这样放（而不是放进宿主目录）：

1. 上游更新后 `ExperimentalPatch/` 整个目录不产生冲突——引擎和 UI 一样天然躲开合并；
   冲突只剩宿主那几处接驳点，而接驳点是**带标记的小块**，重接成本可估。
2. 引擎是纯 DSP + 图，**不引用宿主、不引用 Avalonia** → 可以独立单测。
   这是最值钱的一条：环路检测、PDC、拓扑序、退化路径都能在没有音频设备的情况下验证。
3. 依赖方向仍是单向的，与现有纪律同构：

```
   TuneLab（宿主） ──引用──> ExperimentalPatch（UI 补丁）
        │
        └──────────引用──> ExperimentalPatch/MixerEngine（纯 DSP）
                           ExperimentalPatch/InsertHost（VST/JSFX 适配）

   两个补丁侧工程都不引用宿主。
```

- 引擎只认自己的三个接口：`IMixSource`（一块一块出样本）、`IMixInsert`（处理 / 延迟 / 状态）、
  `IMixBlock`（缓冲）。
- 宿主把 `Track` / `AudioSource` / 工程模型**编译**成引擎的 `MixSnapshot`
  （新文件 `TuneLab/ExperimentalPatchMixerAudio.cs`，零冲突）。引擎不知道 `Track` 存在。
- 路由 / 插入的**数据模型**（undo、序列化）仍在宿主 `Data/`（那是 `DataObject` 的地盘）；
  引擎只吃编译后的快照。
- 插入的 VST / JSFX 适配放 `ExperimentalPatch/InsertHost/`：同样不引用宿主，
  只实现引擎的 `IMixInsert`，这样它也能独立测（用一个假插件）。

**代价要说明**：这样一来 `ExperimentalPatch/` 里有一个**必需**的工程——从解决方案里删掉它，
宿主就不编译。这是有意的取舍：引擎不是"可选补丁"，而是**寄居在补丁目录里的宿主组件**。
可选的是**界面**：没有 UI 补丁时引擎照跑，路由仍然生效。

### 7.1 接驳点必须带标记

宿主里每一处改动都用固定注释标记，例如：

```csharp
// [EP-MIXER-1] MixData -> 渲染图。见 ExperimentalPatch/.agents/mixer-reconnect.md
```

- 标记 id 与 `mixer-reconnect.md` 的表格一一对应。
- 一个接驳点**只做一件事**，尽量"整段替换"而不是散点修改。
- 上游改了这些代码 → 重接就是"按表逐个核对标记"，而不是"重新理解整个引擎"。
- 重接指南单独成文：`ExperimentalPatch/.agents/mixer-reconnect.md`（假设执行者是 AI agent）。

### 7.2 无路由时的等价性（本层底线的新版本）

- 没有路由、没有插入时，图退化成"每轨求和 → 按轨序累加到 Master → 乘 MasterGain"，
  **算术顺序与今天完全一致**（同一批乘法、同一批加法、同样的顺序）。
- 因此验收标准是**逐样本 bit 相同**（不是"听感一致"）：同一工程分别用旧 `MixData` 与新图渲染，
  缓冲区必须完全相等。这是 M3 的硬验收。

## 8. 宿主改动清单（= 接驳点清单）

| # | 标记 | 位置 | 改动 | 冲突风险 |
|---|---|---|---|---|
| 1 | — | `ExperimentalPatch/MixerEngine/`（**新工程**） | 节点 / 边 / 快照 / 拓扑序 / PDC / 环路 | 零 |
| 2 | — | `ExperimentalPatch/InsertHost/`（**新工程**） | VST2 / VST3 / JSFX 的 `IMixInsert` 实现 + 扫描缓存 | 零 |
| 3 | — | `ExperimentalPatch/Patches/Mixer/`（**已有**） | UI：通道条新三行、send / FX 浮窗、output 菜单、`+ AUX` | 零 |
| 4 | `EP-MIXER-1` | `Audio/AudioGraph.cs` | `MixData` → 渲染图；回调里的 `lock` 换快照；保留 `AddData` 给 stem 导出 / 电平 | **中** |
| 5 | `EP-MIXER-2` | `Audio/AudioEngine.cs` | `ExportMaster` / `ExportTrack` 走图 | **中** |
| 6 | `EP-MIXER-3` | `Audio/AudioEngine.cs` | 播放回调：Master 增益移进 Master 节点、设备 sink、Master 电平查询 | **中** |
| 7 | `EP-MIXER-4` | `Data/Track.cs` / `ITrack.cs` | `Output` / `Sends` / `Inserts` | 中 |
| 8 | `EP-MIXER-5` | `Data/Project.cs` | AUX 有序表 + 增删命令 | 中 |
| 9 | `EP-MIXER-6` | `Data/NativeTrackInfo.cs` + `TuneLabProjectCbor.cs` | 序列化路由 / 插入；**版本 +1** | 中 |
| 10 | `EP-MIXER-7` | `TuneLab.csproj` | 两条 `ProjectReference`（`ExperimentalPatch` 已有，加 `MixerEngine` + `InsertHost`） | 低 |
| 11 | `EP-MIXER-8` | `UI/MainWindow/Editor/Editor.cs` | 「实验功能」菜单（v1 已规划） | 低 |
| 12 | `EP-MIXER-9` | `ExperimentalPatchBootstrap.cs` + `TuneLab/ExperimentalPatchMixerAudio.cs`（新） | `CreateMixerModel()` 薄转发 + 快照编译器 | 零 / 低 |

`EP-MIXER-*` 的逐条说明、断点症状与重接步骤见 `.agents/mixer-reconnect.md`。

## 9. 工程格式（tlpx）

- 版本 +1。**老版本打开新工程会直接报 "Unsupported Version"**（CBOR reader 已有这个分支）——
  这是我们要的行为：静默丢掉路由会让工程听起来变了却不报警。
- 新字段全部进 **app 私有的 `NativeTrackInfo`** 与 AUX 的 native DTO，**不动 SDK 的 `TrackInfo`**：
  冻结的插件 ABI 一行都不改；通用格式（MIDI 等）导出时自然丢弃路由，这是正确的。
- 结构草案：

```
TrackInfo(native) += output : { kind: "aux"|"master"|"device"|"cable", id: string }
                     sends  : [ { kind, id, gainDb } ]
                     inserts: [ { format, pluginId, bypass, state: bytes } ]
ProjectInfo(native) += auxes : [ { id, name, gainDb, output, sends, inserts } ]
```

- AUX 用**稳定 id**（不是序号）引用：换序 / 删中间一条时路由不会错位。
- `pluginId`：VST3 用 `classId` + 插件名兜底；VST2 用 `uniqueId` + 路径兜底；JSFX 用工程内相对路径。

## 10. 里程碑

**分两个阶段**：先把混音器（图 / AUX / 路由 / 浮窗）做完并稳定，再碰插入效果。
理由：插入效果依赖图与 PDC，而图不依赖插入；先把地基验收掉，VST 那一层才有东西可挂。

### 阶段一：混音器（先做）

| 里程碑 | 内容 | 验收 |
|---|---|---|
| M0 | 外观稿（**已完成**，v2）：通道条含 send / FX / output 三行，AUX 条，send 与 FX 浮窗，output 菜单 | 出图（见 `Patches/Mixer/` 的说明与提交记录） |
| M1 | 契约 + `app.mixer` 锚点 + 适配器（只读）（**已完成**：契约、`IMixerModel` 适配器、`CreateMixerModel`、读/写/undo 单测） | harness 打印所有通道的 gain/pan/mute/solo/选中/output/send 数/插入数 |
| M2 | 双向写回（推子 / pan / M/S）+ 一次拖动一步 undo（**代码已就位**：控件 `EditStarted/Ended` + 适配器 merge 作用域；端到端要等 M5 的菜单入口） | 编辑器与调音台互相同步 |
| M3 | **图引擎（无 AUX）**：节点 / 边 / 快照 / 拓扑序；退化时行为不变（**已完成**） | 同一工程分别用旧 `MixData` 与新图渲染，缓冲区**逐样本 bit 相同**（§7.2）；`TuneLab.Tests` 全绿 |
| M4 | AUX + output + send：模型 / 图 / 两个 solo 域 / undo / 持久化 | 建两条 AUX、做 send，电平与听感符合预期；环路被拒；存读一致；AUX solo 不影响轨道 |
| M5 | 混音器打磨：`+ AUX` / 删除 AUX（改指 Master）、output 菜单、空态、窗口几何记忆（**已完成**）、「实验功能」菜单入口（**已完成**）；`+AUX` 待 M4 | 菜单能开关窗口；加一条 AUX 立刻出现；删 AUX 后路由被改指并提示 |

### 阶段二：插入效果（后做）

| 里程碑 | 内容 | 验收 |
|---|---|---|
| M6 | `IInsertHost` + **VST3**（OwnAudioVST3，独立 ALC） | 挂一个 VST3：出声、存读、延迟被 PDC 补偿 |
| M7 | **VST2**：64 位进程内 + **x86 桥进程**（§5.3） | 32 位老插件能出声；主工程仍 AnyCPU |
| M8 | FX 浮窗接线：旁通 / 界面 / 增删；插件扫描缓存；找不到插件的降级 | 与外观稿一致；插件缺失不丢链 |
| M9 | JSFX（§12 Q4：先 spike 再定） | — |

### 阶段三：以后

多设备输出（监听用，§4.4）、`daw_cable`（§4.5）、插入参数自动化、分轨批量导出。

## 11. 风险登记

| 风险 | 影响 | 应对 |
|---|---|---|
| 引擎重写引入实时毛刺 / 爆音 | 高 | 先做 M3（无 AUX 的退化图），用现有测试与听感做基线；回调里零分配 |
| PDC 算错 | 中 | 用"两条等长路径"与"一条带 1024 样本延迟插件"的用例验证相位 |
| VST 崩溃带走应用 | 高 | v1 明说；v2 独立进程 |
| JSFX 没有现成解释器 | 高 | §12 Q4；先 spike（ysfx 能否 P/Invoke + @gfx + 许可） |
| VST2 许可与 SDK 合规 | 中 | 只做运行时加载、不分发 SDK；文档写明 |
| 工程格式兼容 | 中 | 版本 +1，老版本硬失败；native DTO 不进 SDK |
| 上游合并冲突 | 中 | §7 选项 A；改动集中在 3~5 个文件，尽量整段替换而非散点修改 |
| 范围失控 | 高 | v1 明确不含：多设备、pre-fader send、AUX 重命名 / pan、分轨批量导出、参数自动化 |

## 12. 决策记录（2026-10-19，均已拍板）

| # | 问题 | 决定 |
|---|---|---|
| Q1 | 引擎代码放哪？ | **放在 `ExperimentalPatch/` 下、独立工程**（`MixerEngine` / `InsertHost`），宿主反向引用并在少数位置接驳；接驳点带 `EP-MIXER-*` 标记，重接指南见 `.agents/mixer-reconnect.md`。§7 有依赖方向图 |
| Q2 | 多设备输出进 v1 吗？ | **不进**。v1 只有 settings 里那一个设备；多设备 v2，用途是**监听** |
| Q3 | `ExportMaster` 算不算 `MasterGain` 与 Master 插入？ | **算**。Master 有插入之后"导出不含母带链"说不通；实际影响面≈0 |
| Q4 | JSFX 进 v1 吗？ | **先做 VST2 / VST3，JSFX 等 spike 后再定**（ysfx 能否 P/Invoke + @gfx + 许可） |
| Q5 | send 上限 +12 dB、v1 不做 pre-fader send | 按此执行 |
| Q6 | VST2 的 x86 老插件怎么办？ | **桥进程**：32 位插件走独立的 `VstBridge.x86`，主工程保持 AnyCPU（§5.3） |
| Q7 | AUX 有 pan / M / S 吗？solo 的范围？ | **都有**；AUX 的 solo 只在 AUX 域内生效，不影响轨道与 Master（§3.3） |
| Q8 | 插入浮窗每行放什么按钮？ | **`旁通` + `界面`**：旁通跳过该效果器（槽位保留），界面打开插件自己的设置窗（§6.3） |
| Q9 | OwnAudioVST3 什么时候接？ | **v2/v3**。它带托管程序集，必要时工程自编译；先把混音器做完（§10 阶段一） |

仍然开放（实现到那一步再定，不阻塞现在）：

- AUX 条是否要重命名 / 换色（现在自动命名 + 固定 AUX 色）。
- 插入效果是否需要参数自动化（v1 只存 state，不做曲线）。
- 分轨批量导出（v1 只做单轨 stem）。

## 13. v1 已定稿、v2 不改的部分

这些细节在 v1 里定过并已实现，v2 只在通道条里**插入三行**（send / FX / output），其余不动：

| 项 | 位置 |
|---|---|
| 调色板镜像（宿主主题色） | `ExperimentalPatch/UI/Palette.cs` |
| 推子：分段线性刻度、−∞ 档、滚轮 1 dB / Shift 0.1 dB、双击回 unity | `Patches/Mixer/Controls/Fader.cs` |
| 电平表：瞬时起、48 dB/s 落、1 s peak-hold、−12 / −3 / 0 dB 渐变锚点、量程 −48..+6 | `Patches/Mixer/Controls/StereoMeter.cs` |
| pan 旋钮 / M S 开关 / 标题栏按钮 | `Patches/Mixer/Controls/` |
| 通道条与窗口骨架 | `MixerChannelStrip.cs`、`MixerWindow.cs` |
| v2 新增控件：`StripButton`（`Send N` / `FX N`）、`OutputSelector`（output 行）、`SendGainSlider`（send 增益） | `Patches/Mixer/Controls/` |
| v2 浮窗：send 列表、插入效果列表 | `SendPopupWindow.cs`、`FxPopupWindow.cs` |


