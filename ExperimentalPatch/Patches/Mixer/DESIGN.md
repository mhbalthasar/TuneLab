# 多轨混音台（Mixer）— 设计稿 v2

> 状态：**v2 设计稿，待拍板**（2026-10-19）。
> v1 的外观稿（通道条 / 推子 / 电平表 / 窗口）已落地并出图；v2 在它之上加**路由（AUX / output / send）
> 与插入效果** —— 那已经不是"给已有状态加一个新视图"，而是要改音频引擎的骨架。
> §7 说这条边界怎么划，§12 列了需要你拍板的五件事。
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

- 门控在**源头**（轨道节点），不在总线上：一条轨被静音 / 未 solo，它的 output 与 send **一起**哑掉。
  这与"你听到的就是你看到的"一致，也避免"静音了但 AUX 里还在响"这种反直觉。
- AUX 不参与 mute/solo（v1）；Master 也不参与。
- solo 判据不变：**有任何一条轨 solo，则未 solo 的轨全哑**（照搬 `AudioGraph.MixData:92-105`）。

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
| Track | ✓ | ✓ | ✓ | Aux / Master / Device / Cable | 音源来源 |
| AUX | ✗（v1） | ✗ | ✓ | Aux / Master / Device / Cable | 纯总线，可增删 |
| Master | ✗ | ✗ | ✗ | Device / Cable | 终点，唯一 |

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
- 多设备留 v2：每个设备一个 sink + 漂移补偿。

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
| VST2.4 | Windows | VST.NET2（托管封装） | 可行。注意 VST2 SDK 早已停止发放许可，VST.NET2 自带头文件；我们只做运行时加载、不分发 SDK |
| VST3 | 全平台 | OwnAudioVST3 | 可行，但要先确认它的加载 / 编辑 / 状态 API 覆盖面（§5.4 / §5.5） |
| JSFX | 全平台 | 需要 EEL2 解释器 | **最大风险**：没有现成 .NET 实现。要么包 `ysfx`（C++ 解释器，P/Invoke），要么自己写（数月）。见 §12 Q4 |

- 三条路线共用一层**中间契约** `IInsertHost`：`Process(block)`、`LatencySamples`、`OpenEditor()`、
  `SaveState()` / `LoadState()`、`Parameters`。上层（图、UI、持久化）只认它，不认 VST2 / VST3 / JSFX。
- 扫描 / 枚举插件单独线程 + 缓存（一次几十毫秒到几秒），不阻塞 UI。

### 5.3 隔离（VST3 那条特别要求）

"避免与其他引用该库的插件冲突"要分两层看：

- **托管层**：把 OwnAudioVST3 连同它的依赖装进独立的 `AssemblyLoadContext`
  （可回收、自己的依赖解析），与别的扩展各自一份 → 版本冲突解决。
- **原生层**：ALC **管不到**。如果这个库带一个原生 dll（VST3 host 的 native 部分），
  两个版本同时加载就会按文件名撞车。可选解法：① 原生 dll 改名带版本号；② 插入宿主放进**独立进程**。
- 建议：v1 做 ALC + 改名；**独立进程**留 v2（它同时是"插件崩了不带走应用"的正解）。
- 插件崩溃：v1 承认"在进程内，插件崩 = 应用崩"，写进 §11 风险，不做假承诺。

### 5.4 界面

- VST2 / VST3：插件的**原生编辑器窗口**（VST2 `effEditOpen`、VST3 `IPlugFrame`）。
  v1 用**独立浮窗**（简单、跨平台），不做嵌进 Avalonia 窗口的子 HWND。
- JSFX：`@gfx` 画到位图上贴进 Avalonia（ysfx 有 gfx 缓冲）；没有 `@gfx` 的用参数滑杆列表。

### 5.5 持久化（随 tlpx）

- 每个插入存：`format`（vst2 / vst3 / jsfx）、`pluginId`、`bypass`、`state`（byte[]）。
- VST2 = `getChunk` / `setChunk`；VST3 = `IComponent::getState` / `setState`；JSFX = slider 值 + `@serialize` 段。
- 存进 `NativeTrackInfo` / AUX 的 native DTO（§9），**不进 SDK 的 `TrackInfo`**。
- state 可能几百 KB，CBOR 里就是 byte string，先不压缩。
- 插件找不到时：**保留槽位**、标红、bypass 直通，提示一次。绝不静默丢链——
  那会让工程听起来变了却不知道为什么。

### 5.6 实时安全

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
- AUX 条：没有 pan、没有 M/S，其余一样；色条用固定的 AUX 色（不与轨道色撞），标题 `AUX 1`。
- Master 条：**也能挂插入**（母带链），只是没有 send；其余同 AUX。
- 条高：固定行 22+20+42+22+22+22+24+22 = 196，剩下给推子。窗口 560 高时推子约 330px，够用。

### 6.2 send 浮窗

- 点 `Send N` → 一个**独立浮动小窗**（非模态、可一直开着、跟着调音台窗口）。
- 每条 send 一行：目的地名（`AUX 1`）+ 横向增益推子 + 当前 dB + `✕` 移除；
  底部 `+ 添加 send ▾`（菜单列 Aux / Master）。
- 增益推子量程沿用 §4.3（−∞..+12）。
- 不是"确认 / 取消"式：改动即时生效（与调音台其它控件一致），undo 粒度 = 一次拖动。
- 关掉调音台窗口时，浮窗一起关。

### 6.3 effect 浮窗

- 点 `FX N` → 同样风格的浮窗：每条插入一行 —— 名称、bypass 开关、上移 / 下移、`✕` 移除、`打开界面`；
  底部 `+ 添加效果 ▾`（按格式分组：VST2 / VST3 / JSFX，不可用的格式禁用）。
- 顺序 = 处理顺序（从上到下）。
- 添加流程：选格式 → 插件列表（带搜索）→ 选中即插到末尾。
- 加载失败 / 文件缺失：行内标红 + bypass，并给"重新定位"入口（v1 可以先只提示）。

### 6.4 通道状态怎么读

- 直接进设备的通道（绕过 Master）：output 行显示 `设备`，条头加一个小标记 `DEV`，
  提醒"这条不受 Master 推子影响"。
- 被静音 / 未 solo：表变黑（v1 已定），M/S 按钮自身状态已说明原因。
- AUX 没有输入时：表全黑，正常。

### 6.5 AUX 的增删

- 调音台标题栏加 `+ AUX`（新建，插在 AUX 区末尾）。
- 删除：AUX 条右键菜单 `删除此 AUX`（要确认，因为会改路由，§4.1）。
- v1 只放调音台，不在编辑器轨道列表里重复开一个入口（两处 UI 容易两套状态）。

## 7. 补丁层边界（本次最需要你拍板的事）

v1 能"零冲突"，是因为它**只读宿主已有状态**。v2 要改引擎：图、设备 sink、插入宿主、工程格式。
这些**不可能**塞进补丁层——补丁层碰不到宿主 internal 类型，更不该在别人的音频线程里跑 DSP。

| 选项 | 做法 | 代价 |
|---|---|---|
| **A（推荐）** | 引擎进宿主（新目录 `TuneLab/Audio/Mixer/`、`TuneLab/Audio/Inserts/`，对 `AudioGraph` / `AudioEngine` 只做小改），**界面仍留补丁层** | 合并冲突从"零"变"中"（集中在 3~5 个核心文件）。但这是唯一能让补丁层继续只管 UI 的划法 |
| B | 全塞补丁层 | 需要一个巨大的宿主契约（节点 / 缓冲 / DSP / 设备），等于把引擎重新暴露一遍；补丁层失去存在意义 |
| C | 直接按上游特性做（PR 系列），补丁层只留实验性 UI | 最干净，但要先和上游维护者对齐（这是个大特性） |

**推荐 A，并承认**：这个特性已经不适合长期留在 `experimental` 分支上。
它是"引擎特性 + 实验性界面"，最终应该走 C 进上游；A 只是让它在实验期能跑起来。

具体划法：
- **补丁层**：调音台窗口、通道条、send / FX 浮窗、output 菜单、`+ AUX` 入口，以及 `IMixerModel` 的**消费**。
- **宿主**：图的构建与渲染、PDC、设备 sink、插入宿主（VST2 / VST3 / JSFX）、
  数据模型（AUX / Send / Insert）、工程格式、undo。
- 契约只加"宿主 → 补丁"方向：`ProvideMixerModel` 返回的模型上多了路由 / 插入的读写口。

## 8. 宿主改动清单

| # | 位置 | 改动 | 冲突风险 |
|---|---|---|---|
| 1 | `TuneLab/Audio/Mixer/`（**新目录**） | 节点 / 边 / 快照 / 拓扑序 / PDC | 零（新文件） |
| 2 | `TuneLab/Audio/Inserts/`（**新目录**） | `IInsertHost` + VST2 / VST3 / JSFX + 扫描缓存 | 零（新文件） |
| 3 | `Audio/AudioGraph.cs` | `MixData` 改为"渲染图"；回调里的 `lock` 换快照；保留 `AddData` 给 stem 导出 / 电平 | **中**（核心文件） |
| 4 | `Audio/AudioEngine.cs` | 设备 sink、Master 增益移进 Master 节点、Master 电平查询、导出走图 | **中**（核心文件） |
| 5 | `Data/Track.cs` / `ITrack.cs` | 加 `Output` / `Sends` / `Inserts` | 中 |
| 6 | `Data/Project.cs` | AUX 有序表 + 增删命令 | 中 |
| 7 | `Data/NativeTrackInfo.cs` + `TuneLabProjectCbor.cs` | 序列化路由 / 插入；**版本 +1** | 中（格式文件） |
| 8 | `UI/MainWindow/Editor/Editor.cs` | 「实验功能」菜单（v1 已规划） | 低 |
| 9 | `ExperimentalPatchBootstrap.cs` + `TuneLab/ExperimentalPatch*.cs`（新） | `CreateMixerModel()` 薄转发 + 适配器 | 零 / 低 |

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

## 10. 里程碑（每步都能单独验证）

| 里程碑 | 内容 | 验收 |
|---|---|---|
| M0 | 外观稿（**已完成**）：控件 + 通道条 + 窗口 | 出图 |
| M1 | 契约 + `app.mixer` 锚点 + 适配器（只读） | harness 打印所有轨的 gain/pan/mute/solo/选中 |
| M2 | 双向写回（推子 / pan / M/S）+ 一次拖动一步 undo | 编辑器与调音台互相同步 |
| M3 | **图引擎（无 AUX）**：节点 / 边 / 快照 / 拓扑序；退化时行为不变 | 播放与导出**听感一致**；`TuneLab.Tests` 全绿 |
| M4 | AUX + output + send：模型 / 图 / UI（下拉 + 浮窗）/ undo / 持久化 | 建两条 AUX、做 send，电平与听感符合预期；环路被拒；存读一致 |
| M5 | 插入宿主 `IInsertHost` + **VST3** 一条能跑通的链 | 挂一个 VST3：出声、存读、延迟被补偿 |
| M6 | VST2（Windows） | 同上 |
| M7 | FX 浮窗 UI + 插件扫描缓存 + 找不到插件的降级 | 与设计图一致 |
| M8 | JSFX（若 §12 Q4 选做）/ 多设备（另行立项） | — |

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

## 12. 待你拍板

| # | 问题 | 我的建议 |
|---|---|---|
| Q1 | 引擎代码放哪：A（进宿主、界面留补丁）/ B（全塞补丁）/ C（直接做上游特性）？ | **A**，接受合并冲突从"零"变"中"；长期应走 C |
| Q2 | 多设备输出进 v1 吗？ | **不进**。v1 只有 settings 里那一个设备；多设备要解决时钟漂移 |
| Q3 | `ExportMaster` 是否把 `MasterGain`（以及 Master 插入）算进去？ | **算**。Master 有插入之后"导出不含母带链"就说不通了；实际影响面≈0 |
| Q4 | JSFX 进 v1 吗？ | **先 spike 再定**。VST2 / VST3 先跑通；JSFX 依赖一个 C++ 解释器或自写 EEL2，风险与工作量都大一个量级 |
| Q5 | send 上限 +12 dB、AUX 不设 pan、v1 不做 pre-fader send | 按此执行，除非你有别的偏好 |

## 13. v1 已定稿、v2 不改的部分

这些细节在 v1 里定过并已实现，v2 只在通道条里**插入三行**（send / FX / output），其余不动：

| 项 | 位置 |
|---|---|
| 调色板镜像（宿主主题色） | `ExperimentalPatch/UI/Palette.cs` |
| 推子：分段线性刻度、−∞ 档、滚轮 1 dB / Shift 0.1 dB、双击回 unity | `Patches/Mixer/Controls/Fader.cs` |
| 电平表：瞬时起、48 dB/s 落、1 s peak-hold、−12 / −3 / 0 dB 渐变锚点、量程 −48..+6 | `Patches/Mixer/Controls/StereoMeter.cs` |
| pan 旋钮 / M S 开关 / 标题栏按钮 | `Patches/Mixer/Controls/` |
| 通道条与窗口骨架 | `MixerChannelStrip.cs`、`MixerWindow.cs` |


