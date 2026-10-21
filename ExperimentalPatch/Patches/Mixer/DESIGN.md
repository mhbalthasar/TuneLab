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

- 通道表 = 该节点**推子与声像之后、路由之前**的信号（= 它自己贡献了多少），与今天 `InvokeRealtimeAmplitude` 的语义一致。
- Master 表 = Master 推子之后的最终输出（这就是原先规划的那个"Master 电平查询"）。
- 静音 / 未 solo 时仍返回 null → 表变黑（v1 已定）。
- **轨道表也走节点抽头**（`InvokeRealtimeTrackAmplitude`），不再读音源本身：
  `InvokeRealtimeAmplitude` 取的是来源信号（pre-fader / pre-pan），推子推到底表不动、
  声像拧到一边两根柱子还是一样高 —— 表就成了摆设。节点 id 是编译期的 `track-<i>`，
  所以按 `AudioGraph.Tracks` 的顺序找下标（顺序与编译器一致）。
- **左右两根柱子各自独立**：早先取 `max(L,R)` 驱动两根柱子，声像的左右偏置在表上完全看不见。

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

### 3.10 输出级限幅（防越界）

**问题**：混音是浮点求和，超过 ±1 很正常。设备端由驱动硬削（听感只是"响"），
但 16-bit WAV 的换算原本是 `(short)(sample * 32768)` —— **未检查转换**：`1.0 × 32768 = 32768` 回绕成
**−32768**，超过 1 的样本直接符号翻转。这就是导出里的"刺啦刺啦"。（24-bit 那条已经 clamp 过；
32-bit 是 IEEE float WAV，不裁。）

**做法**（`MixerEngine/PeakLimiter.cs`，一个独立的类）：

- **位置**：混音图 sink 之后、**电平表抽头之后**、设备 / WAV 之前。
  → 表上仍然看得到越界（符合"不影响动态条显示它越界"）；写出去的数据不越界。
- **算法**：无前视、逐样本。需要压到 `ceiling / |peak|` 时**瞬时压住**（同一帧内，不漏尖峰），
  之后按 150 ms 线性释放；立体声联动（同一帧 L/R 用同一个增益，声像不被压歪）。
- **参数固定**：ceiling = 0.999、释放 150 ms（v1 不暴露给界面；构造参数已留好）。
- **设备与导出同一条路**：两者都走 `Mixer.Render`，所以"听到的"和"文件里的"一致。
  单声道导出走旧路径时也显式调一次限幅器，避免两条路听感不一致。
- **编码器再补一道兜底夹紧**（16 / 24-bit 先 `Clamp(-1, 1)` 再换算）：限幅器在的时候它通常不生效，
  但它保证"任何路径写出的文件都不会回绕"。

**代价**：起振瞬间有谐波失真（无前视限幅器的通病），比符号翻转好得多。
限幅是整块渲染的最后一步，所以**不影响任何节点的表**。

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
| VST3 | 全平台 | OwnAudioVST3（**托管层源码降级 vendor 到 net8**，见 §5.4.1） | 可行，**已落地**：`InsertHost/Vendored/` + `Vst3Backend` / `Vst3Insert`。原生 dll 改名 `.tlhost` 防撞车。⚠️ 真插件验证未做（开发机无 64 位 VST3） |
| JSFX | 全平台 | 需要 EEL2 解释器 | **最大风险**：没有现成 .NET 实现。要么包 `ysfx`（C++ 解释器，P/Invoke），要么自己写（数月）。见 §12 Q4 |

- 三条路线共用一层**中间契约** `IInsertHost`：`Process(block)`、`LatencySamples`、`OpenEditor()`、
  `SaveState()` / `LoadState()`、`Parameters`。上层（图、UI、持久化）只认它，不认 VST2 / VST3 / JSFX。
- 扫描 / 枚举插件单独线程 + 缓存（一次几十毫秒到几秒），不阻塞 UI。
  - 「立即扫描」把 `Scan(progress)` 扔到 `Task.Run`：几秒到几十秒的目录遍历不能让窗口冻住
    （冻住连"关掉它"都点不动）。UI 线程只做三件事：置灰按钮、收进度、写结果。
  - **进度按目录报**（`InsertScanProgress`），不按文件：文件是几百上千的量级，每来一个就跨界回调
    既压满 UI 线程、进度条也只会疯抖。两个格式的累计偏移由宿主算（后端只报自己在自己那份清单里的第几条）。
    目录内部那一步（"正在检查哪个插件"）走 `InsertScanProgress.Detail`：它不推进进度条，
    只是让"一个大目录里逐个过插件"的那几秒看得出还活着。
  - **扫描要判断"是不是乐器"**（「添加效果」菜单不列乐器，见 §6.3），两个格式的代价完全不同：
    - **VST2**：`probeInstruments` = 过桥 `Load` → 读 `effFlagsIsSynth` / 声道数 / 真名 / 厂商 → `Unload`。
      VST2 没有静态元数据，问插件自己是唯一可靠的办法；而它跑在桥里，崩了只是桥崩。
      带 20 秒超时：缺依赖时 `LoadLibrary` 会弹一个没人能点的框永远不返回，超时就杀桥重来
      （管道里的回复已错位，留着会答错下一个问题）。
      ⚠️ 桥一次只做一件事：**正在播放时扫描，VST2 插入会有一阵阵直通**（每次探测占住桥，
      音频线程 `BlockWaitMs` 超时后放弃那一块）。这是"扫描"这个显式动作的代价，
      不会留下后遗症 —— 扫描结束音频自动恢复。
    - **VST3**：**不加载**，读二进制里的子类别标记（`Instrument|` / `Fx|`）。
      实测进程内 `LoadPlugin → Initialize` 会把整个宿主带走（DPF 插件的 assert 直接 abort），
      也有插件 20 秒不返回 —— 扫描不能有这个代价。标记漏掉的那一类（DPF 系，子类别运行时拼）
      由 `Create` 时的真实加载补记：用户点错一次之后，那个插件就不再出现（`sKnownInstruments`）。
- **扫描路径与「立即扫描」住在宿主的「设置」窗里**（一页 `EP-MIXER-16` 的补丁 tab，VST2 / VST3 各一份清单）：
  它是"这台机器上插件装在哪儿"的本机设置，跟界面语言、音频设备同类，散在「实验功能」菜单里没人找得到。
  这一页只经 `IHostServices.InsertPlugins` 读写，落盘在 `{DataFolder}/insert-plugins.json`
  （受 `TUNELAB_DATA_DIR` 重定向），**不进 `.tlpx`**。

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
  - 桥是两个独立 csproj：`VstBridge/`（x86）与 `VstBridge.X64/`（x64，源码同一份），
    **只在需要时由 VST2 加载器启动**；
  - 32 位插件 → 起 x86 桥；64 位插件 → 起 x64 桥（`PluginBitness` 读 PE 头决定）。
- **为什么 VST2 一律走桥**（而不是"64 位就在进程内"）：
  ① 一套代码路径，行为一致；② VST2 是遗留格式，崩溃概率高于 VST3，隔离收益大；
  ③ 主工程不必引用任何与位数相关的原生库 → **AnyCPU 这条不变量不被破坏**。
  - **实测依据（2026-10-20）**：46 个 64 位 VST2 里有 1 个（REAPER 的 ReaControlMIDI）
    在 `effSetChunk` 里直接 AccessViolation。进程内直载 = 整个 TuneLab 退出；
    过桥 = 只是那次调用失败。这条证据推翻了"64 位直载"的原方案。
- **共享内存**：环形缓冲；块大小 / 采样率在握手时协商。桥的往返延迟（约一个块）计入 PDC（§3.5）。
  断线 / 超时 = 该插入 bypass + 标红，**不静默静音**。
- **构建纪律**：桥是独立工程，**不在 `TuneLab.sln` 里**（与 `MixerEngine` 同策略：
  由发布脚本单独构）。部署布局固定为：

  ```
  <app>/bridge/win-x86/TuneLab.ExperimentalPatch.VstBridge.exe      （自包含：多数机器没有 x86 .NET 运行时）
  <app>/bridge/win-x64/TuneLab.ExperimentalPatch.VstBridge.x64.exe
  ```

  两个位数的运行时文件必须分目录——`hostfxr.dll` / `coreclr.dll` 同名不同架构，混放必炸。
  `VstBridgeClient.FindBridgeExecutable` 先找 `bridge/<rid>/`，再退回 exe 同级目录（开发期用）。
  **绝不给 `TuneLab.csproj` 加 `x86` 目标或 `PlatformTarget`**——那会把整个 AnyCPU 工程钉死在一个位数上。

#### 5.3.1 桥的生命周期（2026-10-20 落地）

- **只按需启动**：宿主侧唯一入口 `VstBridgeClient.EnsureStartedAsync()`，只有真的要加载 VST2 插件时才调。
- **宿主单方面决定生死**：宿主先 `CreateJobObject` + `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`，
  启动桥之后**由宿主自己** `AssignProcessToJobObject(job, child)`。
  - 作业句柄是**进程本地**的：把它的数值当命令行参数传给子进程，子进程拿到的只是一个
    不存在的句柄（`ERROR_INVALID_HANDLE`）。第一版就是这么写的，桥一起来就退出。
  - 宿主**退出 / 崩溃 / 被任务管理器杀掉** → 作业句柄被关闭 → 内核杀桥。不需要桥配合、也不需要宿主反应。
  - 反过来**不成立**：桥绝不创建"能杀宿主"的作业。同步是单向的——桥不能独活，但也不能反过来决定宿主何时死。
  - 已知窗口：`Process.Start` 与 `Assign` 之间宿主若崩，桥会成为孤儿（微秒级）。加入失败立即杀子进程。
- **正常关闭**：宿主 `KillBridge()`（`Process.Kill(entireProcessTree: true)` + 放作业句柄）。
- **诊断**：桥的 stderr 由客户端接管（最近 64 行）。启动/加载失败时把它拼进原因里，
  否则用户和我们都只看到一句 "bridge could not be started"。
- **命令管道要串行**：一条管道上同时有音频线程的 Process 与 UI 线程的改参数/存 state，
  "发一条 + 收一条"必须整体加锁（`mRoundTrip`）；否则回复会串台。
  音频线程用 `Wait(BlockWaitMs)`，拿不到锁就放弃这块（直通），绝不无限等。

#### 5.3.2 音频走共享内存，不走管道（2026-10-20 修订）

初版设计把音频也放命名管道里。**改了**，因为延迟不是问题、**阻塞才是**：

- 管道的 `Write` 会在对端没读完时挂住。桥被调度器摘走一会儿、插件在自己的线程里忙，
  宿主的**音频线程就跟着等**——等出来是一个听得见的爆音。
- 共享内存让等待变成"在本地内存上转圈"，并且**可超时**：`BlockWaitMs = 20` 拿不到结果就
  直通这一块、记日志、下一块照常。管道那条路上同样的等待不可控。

| 数据 | 通路 | 为什么 |
|---|---|---|
| 音频（每块的样本） | **共享内存**：`SharedAudioRing`，4 槽环形，SPSC | 零拷贝、可超时、不阻塞在管道上 |
| 指令（加载 / 取参 / 存取 state / 握手） | 命名管道 + JSON 帧 | 低频、"可失败"、要能带人类可读的原因 |

`SharedAudioRing` 的三个设计要点（都在 `VstBridge/BridgeSharedMemory.cs`，两端各编译一份）：

1. **SPSC，不用锁不用 CAS**。每链一个方向、一个生产者、一个消费者。环形容量取 4 而不是 1：
   多出来的容量今天不要钱，将来要"提前一块流水、把桥的调度延迟藏掉"时协议一行不改。
2. **序号推进 = 发布**（`Volatile.Write` 在写完数据之后）。x86/x64 的 TSO 保证数据先于序号可见。
3. **gate 用 ManualReset，不用 AutoReset**。AutoReset 会把"早到的那次 Set"吃掉却没告知任何等待者，
   而序号已经推进——下一次 `WaitOne` 永远等不到第二次通知，症状是偶发卡死。
   （真实踩过：`SharedAudioRingTests` 的 2 万块对撞测试在 block 4 挂住。）

#### 5.3.3 波形指针的提前偏移（2026-10-20 落地）

**问题**：挂上有延迟的插件后，图整体延迟 `MaxLatencySamples`（= X）。延迟插件的语义是
`out[t] = source[t − X]`，于是播放头走到 t 时听到的是 t−X 的内容 —— **音频比进度条慢 X**。

**做法**：读取指针往后拨 X。引擎在读源时请求 `position + X`：

```
out[t] = source[(t + X) − X] = source[t]
```

于是输出的**绝对时间坐标与显示 position 一致**（这是本功能的目的）。

- 偏移量 = `Snapshot.MaxLatencySamples`，随快照换；无插入时为 0，退化路径逐位不变。
- 超出 `EndTime` 的部分自然钳住（`AddData` 的 start/end clamp），视作输入全 0 → 静音。
- **导出**：`ExportMaster` 走同一个 `MixData`，所以也会吃到这个偏移。要让文件干净，
  导出前把 `BufferSize` 用同一张图渲染即可——或者用"去掉头部空白头"的方式，
  由调用方给 `startTime = X / SampleRate` 截掉。**没有**给导出单独一条无偏移路径：
  那会让"听到的"与"写下的"分叉，正是 §3.8 要杜绝的。

**方向说明**：`position + X`（读未来）而不是 `position − X`。后者会得到
`out[t] = source[t − 2X]`，延迟翻倍。推导见 §3.11。

#### 5.3.4 VST2 C ABI：三条踩出来的硬事实（2026-10-20 实测）

桥里的 `VstBridge/PluginHost.cs` 是手写 P/Invoke。第一版"看着对"的代码把插件全部
挡在门外（"the plugin refused to create its AEffect"），原因不是 SDK 复杂，是三处细节：

1. **AEffect 的字段偏移随指针宽度变化**。x86 里 `dispatcher` 在 +4、`process` 在 +8；
   x64 里是 +8 / +16，`numParams` 在 +44 而不是 +24。第一版把 x86 的数字写死，
   x64 下每个字段都读错 —— 读到的是**别的指针的半个字**，调用即堆损坏（0xC0000374）。
   现在由 `AEffectLayout.ForPointerSize(IntPtr.Size)` 按对齐规则算。
2. **opcode 编号是 ABI，不能凭记忆写**。`effClose` = 1、`effSetProgram` = 2，
   而 `effSetSampleRate` = 10、`effSetBlockSize` = 11、`effMainsChanged` = 12、
   `effGetChunk` = 23、`effSetChunk` = 24、`effGetEffectName` = 45、`effGetVendorString` = 47。
   第一版把"设置采样率"写成 1 —— 那是**关闭插件**。于是"打开 → 立刻关闭 → 继续拿它处理音频"，
   症状是随机堆损坏，看起来像 ABI 或调用约定的问题。
3. **Windows 上的调用约定是 `__cdecl`**（`VSTCALLBACK` 的定义），不是 stdcall。
   x64 上两者没区别，所以 x86 桥才会暴露这个问题。
   另外 `dispatcher` 的 `value` 参数与返回值是 `VstIntPtr`（指针宽），必须用 `IntPtr`：
   写成 `int` 在 x64 上只传了半个参数。

还有两条运行期事实：

- **`audioMasterVersion` 必须回 2400**。一律回 0 时，一半插件（Voxengo / Dragonfly / ZamTube…）
  在 `VSTPluginMain` 里直接返回 null，表现为"插件拒绝创建"。
  `audioMasterGetTime` 也要给一块真实的 `VstTimeInfo`（有插件不判空直接解引用）。
- **必须递满插件声明的声道数**。Voxengo 的插件声明 8 进 8 出；`processReplacing` 会按
  自己的 `numInputs/numOutputs` 索引 `float**`。只递 2 个指针 = 读数组外 → AccessViolation。
  桥的做法是永远按 `max(numInputs,1)` / `max(numOutputs,1)` 备缓冲，用不到的填静音，
  出来的结果再映射回立体声（单声道插件复制到左右）。

**验证方式**：`Y:\Insts\VSTEffects\VST2` 下 51 个 dll（46 × x64 + 5 × x86）逐个过桥：
加载 → 参数 → 8 块音频 → 旁通逐样本比对 → 卸载。结果 51/51 通过，旁通全部逐位一致；
`RoomReverb.dll` 被正确识别为"不是 VST2"（没有 `VSTPluginMain` 导出）。

### 5.4 隔离（VST3 那条特别要求）

"避免与其他引用该库的插件冲突"要分两层看：

- **托管层**：把 OwnAudioVST3 连同它的依赖装进独立的 `AssemblyLoadContext`
  （可回收、自己的依赖解析），与别的扩展各自一份 → 版本冲突解决。
- **原生层**：ALC **管不到**。如果这个库带一个原生 dll（VST3 host 的 native 部分），
  两个版本同时加载就会按文件名撞车。可选解法：① 原生 dll 改名带版本号；② 插入宿主放进**独立进程**。
- 建议：v1 做 ALC + 改名；**独立进程**留 v2（它同时是"插件崩了不带走应用"的正解）。
- 插件崩溃：v1 承认"在进程内，插件崩 = 应用崩"，写进 §11 风险，不做假承诺。

#### 5.4.1 VST3 的实际落地：源码降级 vendor + native 改名（2026-10-20）

调研结果：**所有可用的 VST3 NuGet 托管封包都要求 net9/net10**（`OwnAudioVst` ≥1.7.0 → net10.0；
`NAudio.Vst3` / `VST.NET2-Host` → net9/net10 + WindowsDesktop）。TuneLab 是 net8.0，装不上去，
也不该为一个效果器插件把宿主的 TFM 抬上去。

采用的办法：**上游托管层源码降级 vendor 进来，原生 dll 单独分发**。

```
ExperimentalPatch/InsertHost/
  ├── Vendored/          OwnAudioVST3 的托管层（MIT，降级到 net8.0，来历与升级步骤见 README.md）
  ├── Native/win-x64/    ownvst3.tlhost.dll
  ├── Vst3Backend.cs     装配（UI 线程）：扫描、加载、Initialize、拒绝乐器
  ├── Vst3Insert.cs      运行期（音频线程）：平面↔交织、参数、state、bypass
  └── VstBridgeClient.cs 桥客户端（JobObject + 共享内存环）
```

三个要点：

1. **降级零修补**。上游的 C# 在 net8 下原样编译通过——唯一要改的是原生库名。
2. **原生 dll 加 `.tlhost` 后缀**。两个原生库同名同时加载，Windows 按**文件名**解析，
   第二份会被静默忽略——症状是"两个插件忽然行为一样"这种极难排查的问题。
   加后缀让它们各自一份。代价是托管层的路径搜索与 DllImportResolver 都要认新名字
   （`Vst3Backend` 里装的 resolver）。`.so` / `.dylib` **不**加：soname / install name 与文件名强绑定，
   改了反而更难找（我们目前只在 Windows 分发桥，所以不影响任何人）。
3. **两层线程分工**。`IMixInsert.Process` 在音频线程：不分配、不加锁、不抛异常。
   而加载 / 读状态 / 写参数 / 开界面全在 UI 线程、全都会失败。塞进一个类 = 音频线程上跑 UI 代码。
   所以拆成 `Vst3Backend`（装配，允许失败）+ `Vst3Insert`（运行期）。

**尚未验证**：开发机上没有 64 位 VST3 插件（只有 32 位的 Chow 系列，64 位进程 `LoadPlugin`
会干净地返回 false）。所以"真插件出声 + 存读 + PDC 补偿"这条验收要等有插件时补做。
在那之前，`InsertChainTests` 用假后端覆盖了"链被接进图、旁通真的跳过、latency 真的补 PDC"这些
与后端无关的行为。

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

#### 5.7.1 块长：插件声明的必须 ≥ 引擎交出来的（2026-10-20 修复）

**症状**：挂上任意一个 VST / VST3 效果器，声音就带一层"音频线接触不良"的爆音。

**根因**：`InsertInstanceFactory.MaxBlockSize` 从来没被赋过值，停在默认 **512**；而 SDL 回调一次交
**1024** 帧（`AudioEngine.BufferSize`）。两个插件的 `Process` 都写的是
`n = Math.Min(count, mBlockSize)` —— 于是**每块只处理了前 512 帧，后 512 帧原样直通**。
每 ~10ms 一次硬接缝，听感正是"接触不良"。`SampleRate` 也一起停在 48000（44.1k 的工程里
插件按 48k 跑，音高与时值全错）。

**现在的约定**（三条，缺一条就会退回原样）：

1. `MixerGraphCompiler.Compile` 每次编译都按当前图刷 `SampleRate` / `MaxBlockSize`
   （块长 = `Mixer.DefaultMaxBlockSize`，即引擎块长上限）。
2. VST2 传给桥的块长按 `SharedAudioLayout.MaxBlockFrames` 夹一次：超过共享环一块能装的帧数，
   `TryPublish` 直接失败，那一块就白直通了（又一个"看着在跑其实没处理"的坑）。
3. 两个插入的 `Process` 都**按 ≤ 声明块长分段循环**，不再截断。声明值是主保险，这个循环是第二道：
   将来谁再把引擎块长调大，也不会只处理一半。

**NaN / ±Inf**：插件输出回到交织缓冲时逐个样本检查，非有限值就地清零。这类值一旦进了总线，
整条链都拦不住 —— 限幅器也不例外（`peak > ceiling` 对 NaN 恒为 false），
最终到声卡就是一串爆音或整段静音。代价是一帧两次比较，换来"一个坏插件只毁自己那一块"。

**回归**：`MixerEngineTests.Insert_SeesEveryFrameOfAnEngineSizedBlock` —— 钉住
"默认块长 ≥ 一个引擎块"与"混音台整块交给插入、不切不截断"。

#### 5.7.2 桥的音频路：所有等待都要有上限（2026-10-20）

音频线程上的等待必须有界，否则一个卡住的插件会把整条音频拖成断续爆音（SDL 缓冲只有 ~21ms）：

- `mRoundTrip` / 输入环 / 输出环：`BlockWaitMs = 20`，超时就放弃这一块（直通）。
- 写管道的 `mWriteLock` 也一样要有上限（UI 线程可能在写 MB 级的 state，不能让它把音频线程挂住）。
- **连续失败到阈值**（50 块 ≈ 1 秒）就把桥的音频路整个停掉，之后一律直通：桥卡死时
  "这个插件在你这台机器上不生效" 远好过 "整台机器都在爆音"。
- 读管道的**回执**仍是无上限的等（协议要求一问一答，超时就会串台）：这是唯一的例外，
  它的上限来自桥自己的处理时间 —— 插件本身卡死时，第 3 条会兜住。

#### 5.7.3 旁通是活值（2026-10-20 修复）

**症状**：FX 浮窗里按「旁通」，既听不出差别、按键也不亮 —— 看起来就是"没反应"。

**根因**：`IsBypassed` 只写进数据槽位，而混音图**不是旁通结构变化**（换快照的触发器只有
加删轨 / 加删 AUX / 改路由），所以新值要等到下一次结构变化才进链。更糟的是按钮读的
`insert.IsBypassed` 来自那个写前的适配器，按键的常亮态也回不来。

**现在的约定**：

1. `IMixInsert` 加 `SetBypassed(bool)`。旁通不是图结构，不需要换快照；它与推子/静音同一条
   纪律 —— **活值**，改完立即生效。
2. 引擎每块现读 `IsBypassed` 并**跳过**旁通的插入（`false` 时实现方自己走最快的直通路）。
   这条也让"旁通的插入一次都不会被调用"变成可测的：测试替身因此能断言"没进过链"。
3. 宿主侧 `InsertSlotAdapter.IsBypassed` 同时写三处：数据（undo / 落盘）、活实例（听见）、
   `Changed`（浮窗刷新按键与配色）。
4. `IsBypassed` 字段在 `Vst2Insert` / `Vst3Insert` 里是 `volatile` —— 写在 UI 线程、读在音频线程。

#### 5.7.4 「界面」键：插件编辑器怎么接（2026-10-20）

**症状**：「界面」键按下去没反应，没有插件的调整界面。

**根因**：`InsertSlotAdapter.HasEditor` 恒定 `false`、`OpenEditor()` 是空实现 ——
契约里有这两个成员，但宿主侧从没接过。

**现在的约定**（两条路，按格式分）：

- **VST3**（进程内加载）：宿主建一个 Avalonia Window，把它的平台句柄（`TryGetPlatformHandle()`）
  交给插件的 `CreateEditor`，插件在里面创建自己的子窗口。窗口尺寸先用 `GetEditorSize()` 报的
  首选值，用户可再拖。**关窗前必须 `CloseEditor`**：view 要早于宿主窗口销毁被摘掉，
  反序的话插件关窗时会碰已释放的组件（实测会崩）。`InsertInstanceFactory.Release` 与
  关工程都会先过 `InsertEditorHost.Close`。
- **VST2**（跑在桥进程里）：跨进程 `SetParent` 不可靠（菜单、焦点、拖放、DPI 全错），
  所以宿主只发一条 `OpenEditor` 消息，窗口由**桥**创建：`Vst2EditorWindow` 单起一条 STA 线程
  （VST2 的 `effEditOpen` / `effEditIdle` / `effEditClose` 必须在同一条线程上），
  建 Win32 窗 → `effEditOpen` → 消息循环 + 50 ms 定时器驱动 `effEditIdle`。
  关闭统一走 `PostMessage(WM_CLOSE)`：消息被插件的窗口子类吞掉时也只是"窗口没关掉"，
  不会把桥弄死。

补丁侧只经 `IMixerInsert.HasEditor` / `OpenEditor()` 这两件事，不知道底下分了几条路。
宿主侧向活实例要答案 —— 插件建失败 / 还没编图时按钮自然就是灰的。

#### 5.7.5 编辑器四连坑：名字不刷新 / 空窗 / 弹一次就没了 / 卡死（2026-10-20 修复）

四个症状连着报上来，根因各不相同，逐个记下来：

1. **刚加完插件只看到格式字样**：`FxPopupWindow.AddInsert` 里 `AddInsert(format)` 那次
   `InsertList.Add` 会响 `RoutingChanged`（浮窗订阅它），但那一刻槽位的名字**还是空的** ——
   `SetPlugin` 写在后面，而它只发自己那个 adapter 的 `Changed`（没人订阅）。
   所以浮窗要关掉再打开才刷新。修法：`AddInsert` 末尾显式 `Rebuild()`。
2. **窗口打开是空的**：VST3 有些插件把 GUI 挂在自己的消息队列上，不泵就不画。
   现在编辑器窗口有一个 20ms 的后台定时器调 `SafeDispatchMessage`（native 侧对不需要的
   插件是空操作）。VST2 那边的问题是"插件返回成功却什么也没建"——桥现在会数宿主窗口里的
   子窗口（`ChildWindows`），0 就记一条日志说清是依赖缺失，而不是留一个空窗让用户猜。
3. **第一个窗口关掉后就再也弹不出**：`InsertEditorHost` 的字典靠 `WindowClosed` 事件注销。
   第一版只写了一半（`Vst2EditorShell` 没有这个事件、`Vst3EditorWindow` 的实现被基类的
   `event EventHandler Closed` 撞名吞掉），于是关闭后条目留着，第二次点复用一个**已关闭**的
   `Window` —— Avalonia 的 `Window.Close()` 之后再 `Show()` 什么也不会发生。
   修法：接口成员改名 `WindowClosed`（避开撞名）+ 显式实现 + 两个实现都发。
4. **打开某个编辑器直接卡死整个窗口**：`CreateEditor` 调的是**插件自己的代码**，
   弹模态 / 跑自己的消息循环都会把我们的 UI 线程连同调音台挂住（VST2 侧同理：
   宿主等桥回执时 UI 线程不能呼吸）。现在两条路都有上限：
   VST3 丢到后台线程等（8s），VST2 等 3s；超时就把窗口留在"加载插件界面…"的提示态
   并记日志，**界面始终活着**。

#### 5.7.6 插件编辑器：窗口归插件自己（2026-10-21 实测，上一版结论已推翻）

**结论先说**：VST3 的编辑器窗口**交给插件/native 库自己建**。这不是妥协，是实测出来的
唯一可行路径。而发现它的过程值得记下来 —— 因为中间那次结论是错的，错因很有教育意义。

**走错的那一遍**：先按"宿主给窗口"实现（把 Avalonia 窗口的平台句柄交给 `CreateEditor`）。
现象是全部插件报"没有画出界面"。当时数我们窗口的子窗口，恒为 0，于是写下
"native 库的编辑器实现不工作" —— **这个结论是错的**。真正的原因是两个探测 bug 叠在一起：

1. `EnumChildWindows` 的回调在 .NET 宿主进程里**一个都不触发**（连数自己那个活窗口都是 0）。
   它同时污染了另一个测量（`EnumWindows` 从 .NET 调也是 0 个顶层窗口），
   害我误判"插件没建任何窗口"。
2. 用户观察："测试过程中看到一闪而过的 GUI，看上去正常" —— 一句话把整个判断掀翻。
   GUI 一直存在，只是**从来不挂进我们给的 HWND**。

**改用 `FindWindowEx` 遍历 + 直问 native 的 `IsEditorOpen`** 之后，实测：

| 交给插件的句柄 | CreateEditor | IsEditorOpen | GUI |
|---|---|---|---|
| 我们的 HWND | true | **true** | 插件自建顶层窗（不挂进我们的窗口） |
| `IntPtr.Zero` | true | **true** | 同样由插件自建 |

也就是说：这个 vendored 的 `ownvst3` **只支持"自己开一扇顶层窗"这一种模式**，
宿主给不给窗口都一样。那我们给的窗口除了永远空白、还要额外泵消息之外没有任何作用。

**现在的约定**：

- `Vst3Insert.OpenEditor` 一律按 `IntPtr.Zero` 传（签名保留，调用方不必改）。
- 宿主侧 `Vst3EditorShell` 不再建窗口，只做三件事：开（`OpenEditor`）、
  泵（20ms 定时器 `SafeDispatchMessage`，VSTGUI 那类不泵不画第一帧）、关（`CloseEditor`）。
- 判断"编辑器是否还开着"**只问 native 的 `IsEditorOpen`**，不再数我们的窗口。
  插件被用户点掉 ✕ 时它变 false，壳据此停泵并注销 —— 这条同时修了
  "第一个窗口关掉后再也弹不开"。

**代价（说清楚，免得将来有人以为是漏做）**：VST3 的编辑器是独立的一扇窗，不在混音台窗口里。
这是这个 native 库的能力边界。换来的是 GUI 真的显示、真的可交互。

**仍然无解的那一类**：`CreateEditor` 不返回。BC Chorus 4 是实例：bundle 里没有
`Skins/default.xml`（二进制里的明文有 `Could not load factory default skin. Please reinstall the
software.`，导入表里有 `GetOpenFileName`），于是它弹一个文件选择模态框，而那个框跑在
**调用 `CreateEditor` 的线程**的消息队列上，那条线程正卡在 `CreateEditor` 里 → 自锁。
换成 STA 同线程、换另一条线程跑消息循环、传 0 都试过，全死。那是插件的缺陷，重新装它。
宿主侧只做一件事：超时不无限冻结（见 5.7.4 的上限约定）。

**给下一个读这里的人**：`EnumChildWindows` / `EnumWindows` / `EnumThreadWindows` 在 .NET 进程里
要么整个不回调、要么直接把调用线程挂死（后者实测过）。**要数窗口就走 z 序链表**：
`GetTopWindow(0)` + `GetWindow(h, GW_HWNDNEXT)`，纯函数调用，实测可靠。
要判断编辑器状态就问插件自己的 `IsEditorOpen`。

#### 5.7.7 编辑器窗口置顶、重开、以及"界面键全是灰的"（2026-10-21）

三个现象，三个独立的根因：

1. **打开后藏在别的窗口下面**：插件自己建窗时不会主动置前。现在打开前后各取一次
   本进程顶层窗集合（z 序链表），差集里第一个可见的带标题窗口就是它，然后
   `SetWindowPos(HWND_TOPMOST)` + `SetForegroundWindow`。窗口可能比 `CreateEditor`
   晚几十毫秒才可见，所以泵消息的定时器每 20ms 重试一次，找到即置顶。
   再点一次「界面」也会重新置前（`mRaised` 复位）。

2. **关掉后再也点不开**：`Vst3Insert.mEditorOpen` 是我们这边的标记，插件把窗口关了它
   不会跟着变。于是下次 `OpenEditor` 看到标记为 true 就直接返回 true —— 什么也没开。
   现在先问一次 `IsEditorOpen`，为 false 就把标记清掉、重新建。

3. **「界面」键一开始全是灰的，加一个新插件后全变正常**：`HasEditor` 问的是**活实例**，
   而实例是编图时建的。装载工程的顺序是 `SetProject`（此时编了一次图，但插入还没装载）
   → `MixerPersistence.Apply`（把槽位塞进去）。插入不是"加轨/改路由"那类会自己触发
   重建的事件，所以装载完之后没有任何实例，全部灰。加一个新插件触发了结构变化，
   图重建、实例全建出来，于是"全变正常"。
   修法：`MixerPersistence.Apply` 末尾补一次 `AudioEngine.RebuildMixerGraph()`。

#### 5.7.8 VST2 编辑器：effEditGetRect 的 ptr 是 ERect**（2026-10-21）

**症状**：Marvel GEQ / AnSpec / reacomp 这些 VST2 插件「界面」键有（`effFlagsHasEditor`
为真），点下去却什么也不出来，日志只有一句 `the bridge did not answer`。

**根因**：`effEditGetRect` 的 `ptr` 参数是 **`ERect**`（指针的指针）**，插件会往宿主给的
地址里写它自己的 ERect 指针。我们传了 `IntPtr.Zero` 并拿返回值当指针 —— 插件于是往
**地址 0** 写，桥进程当场 `AccessViolationException`，管道断掉，宿主看到的就是
"bridge did not answer"。三个插件全崩，不是插件的问题。

**修法**：钉一块 `IntPtr` 当 ERect* 的接收槽，把它的地址当 `ptr` 传，再从槽里读回指针。

**顺带修的两处**（都是这条路上暴露出来的）：

- **卸载顺序**：`Vst2EditorWindow.Dispose` 只投递了 WM_CLOSE 就返回，宿主紧接着
  `effClose` + `FreeLibrary`，而编辑器线程还在跑插件代码 → 卸载时 AV。
  现在 Dispose 会等编辑器线程退出（上限 3 秒）。
- **关掉后重开**：用户点 ✕ 之后窗口与消息循环都没了，但桥里 `slot.Editor` 还指着它，
  `OpenEditor` 走"已开着 → BringToFront"分支打在一个已销毁的 HWND 上。
  现在先看 `IsAlive`，死了就 Dispose 掉重建。

#### 5.7.9 多平台：现在的边界与下一步（2026-10-21）

`ownvst3` 上游本身是跨平台的（JUCE 驱动，win/linux/osx 的 x64 与 arm64 都有构建），
但**仓库里只 vendor 了 `Native/win-x64` 一份**。所以今天的实际边界是：

- VST3 编辑器：Windows-x64 可用；
- VST2：桥是 Windows 可执行文件，非 Windows 上没有 VST2；
- 编辑器置顶：`GetTopWindow` / `SetWindowPos` 是 Win32，非 Windows 上自动跳过
  （`OperatingSystem.IsWindows()` 守卫），编辑器仍由插件自己显示。

为了让"以后加平台"只变成**放文件**，代码这边已经做了两件事：

1. `Vst3Backend.NativeFileName` 按平台取扩展名（`.dll` / `.so` / `.dylib`），
   `NativeRid` 按 `RuntimeInformation.ProcessArchitecture` 算；
2. 查找顺序：与 exe 同目录 → `native/<rid>/` → 环境变量 `TL_VST3_NATIVE`；
   `InsertHost.csproj` 按 `$(RuntimeIdentifier)`（缺省按构建机 OS）把
   `Native/<rid>/*` 拷进输出。

要真正跨平台，还差：各平台的原生库产物（需要上游 CI / fork）、VST2 桥的
非 Windows 等价物（或明确"VST2 仅 Windows"）、以及编辑器置顶的 X11/Cocoa 实现。

#### 5.7.10 「界面」键永远是灰的：Locator 不落盘，定位却只查默认目录（2026-10-21）

**症状**：ZamDynamicEQ / DragonflyHallReverb 这些插件插在工程里、「界面」键却一直是灰的，
日志写着 `the plugin 'X' is not installed any more (id vst3:X); passing through`。

**根因**：`Locator`（插件文件的真实路径）**按设计不落盘**（换机器就失效），所以重开工程时
槽位上只剩 `PluginId`，必须重新定位。而 `InsertInstanceFactory.FindPlugin` 只做一件事：
拿**工厂自己的目录清单**（默认值 = `Program Files\Common Files\VST3` 两处）重新枚举。
用户在设置里配的自定义目录（`Y:\Insts\VSTEffects\VST3`）从来没传给工厂 ——
于是那里的插件全部"找不到"，槽位退化成占位旁通，没有活实例，`HasEditor` 自然是 false。

**为什么"加一个新插件就全好了"**：那是另一条路（扫描缓存在菜单里），加插件时
`Locator` 是内存里现成的。

**修法**：`FindPlugin` **先查扫描缓存**（`InsertInstanceFactory.PluginCacheProvider`，
由宿主注入 `InsertPluginSettings.CachedPlugins`）—— 那是菜单用的同一份清单，带 Locator。
缓存里没有（从没扫过 / 被清）才退回按目录枚举。这样"用户看到的插件"与"能定位到的插件"
永远是同一份。

#### 5.7.11 DPF/NanoVG 插件渲染成白色块：环境没有 OpenGL 1.3（2026-10-21）

**症状**：ZamDynamicEQ / Dragonfly 系列（DPF 框架）编辑器能打开，但画面是白的 + 几块色块。

**实测**：插件的 stderr 里有

```
[dpf] assertion failure: "glActiveTexture != nullptr" in file src/NanoVG.cpp
[dpf] assertion failure: Failed to create NanoVG context, expect a black screen
```

`glActiveTexture` 是 OpenGL **1.3** 才有的函数。Windows 自带的软件渲染器只提供 **1.1**，
所以 `wglGetProcAddress("glActiveTexture")` 返回 null，NanoVG 建不出上下文。
这台机器的显示适配器是 `GameViewer Virtual Display Adapter` / `Microsoft Hyper-V 视频` ——
虚拟机/远程桌面，没有 GPU 驱动，GL 版本停在 1.1。**不是宿主或插件实现的 bug。**

**办法**（都是环境层面的）：

- 在有真实 GPU 驱动的机器上跑；或
- 在 `TuneLab.exe`（以及桥进程目录）旁放一份 Mesa3D 的软件 `opengl32.dll`（llvmpipe），
  它会提供 GL 4.x。注意 VST2 的窗口在桥进程里，那份目录也要放。

**宿主现在会自己说清楚这件事**：`InsertHost/OpenGlProbe.cs` 在第一次打开编辑器时建一个
隐藏窗口 + GL 上下文读 `GL_VERSION`，结果缓存。低于 1.3（或连上下文都建不出来）就写一条
warning：

```
insert: OpenGL 1.1.0 Microsoft Corporation GDI Generic — editors that need OpenGL 1.3+
(DPF/NanoVG, e.g. ZamDynamicEQ / Dragonfly) will render blank on this machine
```

这样"白窗"在日志里能直接对上原因，不必再从插件 stderr 里翻断言。

**两类日志怎么分**：

| 来源 | 能看到吗 | 内容 |
|---|---|---|
| 宿主自己 | 是（宿主日志） | 打开/关闭、超时、`IsEditorOpen`、GL 版本 |
| 插件断言（VST3，进程内） | **否** | 走进程 stderr，GUI 进程没有控制台，读不到 |
| 插件断言（VST2，桥进程） | 是 | 桥的 stderr 被客户端接管，失败时拼进 `insert:` 日志 |

所以 VST3 的插件级断言只能靠"宿主侧的环境探测 + 结果判断"间接归因；
VST2 那条路能直接把插件自己的话贴出来。

#### 5.7.12 「界面」键右键：插件 GUI / TuneLab 接管（2026-10-21）

插件 GUI 画不出来时（缺依赖、没有可用 OpenGL、干脆没做界面），参数并不是取不到 ——
VST3 的 IEditController、VST2 的 getParameter 一直都在。所以「界面」键做成两条路：

- **左键**：有插件 GUI 就开 GUI；没有就走参数面板（别留一个点了没反应的键）。
- **右键**：两项 ——「插件 GUI」与「TuneLab 接管（参数面板）」。

**参数面板**（`TuneLab/ExperimentalPatchInsertParameterEditor.cs`）由宿主自己画：
每个参数一行 = 名字 + 滑杆 + 数值（VST3 还多一个「↺ 回默认」）。思路与 JSFX 的
非 @gfx 渲染一致 —— 不碰插件的窗口，只读写它的参数。

- **控件**：与侧栏同款 —— `TuneLab.GUI.Components.Slider`（默认值区间高亮、双击回默认）
  与 `DraggableNumberBox`（拖动擦写、双击键入）。`TuneLab.GUI.csproj` 本来就对
  `TuneLab` 开了 `InternalsVisibleTo`，所以宿主直接用，不另画一套。
- 行高 30、滑杆 24：Avalonia 原生 Slider 压到 20 会把拇指和轨道裁掉（这是早先那版的坑）。
- 参数来源：VST3 `Parameters`（本地调用）；VST2 `GetParameters()`（过桥，一次往返）。
- 刷新：VST3 200ms、VST2 1.5s（过桥别太勤）。**正在拖的那一根不覆盖**（`Dragging`），
  轮询回填时置 `Applying` 以免把同一个值再写回插件（否则每秒几十次无意义往返）。
- 同一槽位只留一扇窗，再点就激活已有的。

菜单的可用性：`IMixerInsert.HasParameters`（有活实例即为真）。这样"插件没 GUI 但有参数"
的槽位也能用 —— 正是这个功能存在的意义。

#### 5.7.13 插件列表里每个插件出现两次：真装了两份（2026-10-21）

**不是扫描 bug**：`%APPDATA%\TuneLab\insert-plugins.json` 里 186 条记录、121 个唯一
PluginId，且 **Locator 没有重复** —— 也就是说同一份插件真的装在两个目录里
（例如 `C:\Program Files\Common Files\VST3\AnSpec.vst3` 与
`Y:\Insts\VSTEffects\VST3\09_Analysis_Tools\AnSpec.vst3\...`，VST2 同理）。
这不是软链接，是两份实体。

**为什么要排重**：`PluginId`（VST3 = bundle 名，VST2 = 文件名）**就是工程里持久化的标识**，
两条同 PluginId 的记录对工程来说是同一个东西。列两条既没有信息量，还会让人以为装了两套
不同的插件。

**做法**：按 `(格式, PluginId)` 去重，保留扫描顺序里的第一条（= 路径清单里靠前的那个）。
两处都排：`Scan` 落盘前，以及 `CachedPlugins` 读取时（老缓存不重扫也能立刻变干净）。

#### 5.7.16 通道位置调整（轨道头右键，2026-10-21）

右键通道条 → 上移 / 下移 / 置顶 / 置底 / 移动到…（子菜单列出同组其它通道）。
**只在所属组内移动**：轨道组（`Project.Tracks`）与 AUX 组（`Project.Auxes`）各自独立，
轨道"置底"不会跑到 AUX 后面。改的是**工程里的顺序**，所以主界面轨道头会跟着变。

实现：`IMixerModel.CanMoveChannel / MoveChannel / MoveChannelTo` →
`Project.RemoveTrackAt + InsertTrack`（AUX 用新加的 `RemoveAuxAt + InsertAux`）。
整段套 merge 作用域，一次移动 = 一步 undo。顺序变了节点下标也变，所以会重建混音图。

#### 5.7.17 OpenGL 兜底：硬件优先，虚拟机才用软件渲染（2026-10-21）

`runtimes/patcher/{x86,x64,arm64}/opengl32.dll`（Mesa3D llvmpipe）是给**没有硬件 OpenGL**
的机器准备的：DPF / NanoVG 的编辑器（ZamDynamicEQ、Dragonfly）在虚拟机上会白屏。

**判定必须在加载任何 GL 模块之前完成** —— Windows 的模块按**文件名唯一**，先加载谁就全进程
用谁；一旦建过 GL 上下文，再 LoadLibrary 哪份 Mesa 都没用。所以这里**不探测 GL 版本**，
只枚举显示适配器（`EnumDisplayDevices`）：

- 有真显卡（NVIDIA / AMD / Intel …）→ **什么都不做**，用硬件；
- 全是虚拟机 / 远程桌面 / 基础显示适配器（Hyper-V、Microsoft Basic Render、Virtual、
  GameViewer、Parsec…）→ 才 `SetDllDirectory(目录)` + 预加载 `opengl32.dll`。

**渲染后端也自动选**（Mesa 的 `GALLIUM_DRIVER`）：

- 有**硬件 D3D12**（`D3D12CreateDevice` 用默认适配器能建出设备）→ `d3d12`，
  OpenGL 转成 D3D12 跑在 GPU 上；
- 没有 → `llvmpipe`，纯 CPU 光栅化。

两者都在**加载 opengl32 之前**决定（Mesa 初始化时读这个环境变量）。判据用的是 D3D12 设备创建，
不碰 OpenGL —— 理由同上：碰了就没法换模块了。

生效时宿主日志里有一行 `insert: no hardware OpenGL detected; using the software OpenGL from …`；
正常机器不会有任何输出。VST2 的桥进程在自己的 `Main` 里做同一件事（插件在哪个进程，GL 就在
哪个进程里加载）。

#### 5.7.15 切工程后 AUX 接线失效 / 改接线像卡死（2026-10-21）

**症状**：打开另一个工程之后，轨道明明设成发往 AUX，声音却直接进 Master；再改一次
接线会长时间卡住（音频断续）。

**根因（三层，叠在一起）**：

1. `AudioGraph` 是**进程级**的（`AudioEngine` 的静态成员），而 AUX 的 id 每个工程都从
   `aux-1` 开始。`Project.Dispose()` 只 `Deactivate` 了轨道，**没有摘 AUX** ——
   于是切一次工程，图里就多留一组旧的 `aux-1/aux-2…`。
2. 新工程的 `aux-1` 与旧的撞 id，`MixGraphBuilder.AddNode` 抛
   `duplicate node id`，整张图编译失败 → `SetSnapshot(null)` → **退回旧混音路径**。
   旧路径没有 AUX 概念，所以"接线看起来设了、声音还是进 Master"。
3. `RebuildMixerGraph` 在 `mTrackLockObject` **里面**编译，而编图会加载插件
   （VST3 初始化、VST2 过桥，几十毫秒到几秒）。那个锁是音频回调每块都要拿的 ——
   持锁编译会把音频线程整段钉住，听感就是卡死/爆音。

**修法**：

- `Project.Dispose()` 同时把 AUX 从音频图里摘掉（与轨道同一条纪律）；
- 编译器遇到重复 id 只跳过并记日志，**绝不让整张图失败**（一张图建不出来比丢一条 AUX 严重）；
- `AudioGraph` 只在锁里拷一份结构快照，编译与换快照都在锁外。

#### 5.7.14 多平台：与 vendored 包装层对齐（2026-10-21）

`OwnVST3.cs` 里已经写好了三平台的文件名：Windows `ownvst3.tlhost.dll`、
Linux `libownvst3.so`、macOS `libownvst3.dylib`。`Vst3Backend.NativeFileName` 现在与它
**逐字一致**（之前 Linux/macOS 写成了 `ownvst3.tlhost.so`，永远找不到）。

查找顺序（都在 `AppContext.BaseDirectory` 下）：同目录 → `runtimes/<rid>/native/`
（NuGet 惯例）→ `native/<rid>/` → 环境变量 `TL_VST3_NATIVE`。

`InsertHost.csproj` 的 RID 选择也不再写死 x64：`$(RuntimeIdentifier)` → SDK 给的
`$(NETCoreSdkPortableRuntimeIdentifier)`（覆盖 win/linux/osx 的 x64 与 arm64）→ 平台兜底。
往 `Native/<rid>/` 里放对应平台的二进制即可，不需要改代码。

**顺带**：VST2 的窗口开在桥进程里，`SetForegroundWindow` 会被 Windows 静默拒绝
（只有前台进程或获它授权的进程才能抢前台），表现是"窗口开了但藏在宿主下面"。
宿主现在在发 `OpenEditor` 之前调 `AllowSetForegroundWindow(桥的 pid)`，
桥侧再把窗口设成 topmost 并前置。
要数窗口就用 `FindWindowEx` 遍历兄弟链；要判断编辑器状态就问插件自己的 API。
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
  底部 `+ 添加 send ▾`：**点开的是一个目标菜单**，由人挑（不是按 Master→AUX1→AUX2 的顺序替人塞一条）。
  - 菜单里只列真能加的：**已加过的、不可用的（Cable）、输出到自己、会成环的**都不出现
    （判据是 `IMixerChannel.CanSendTo`，与引擎的拒绝理由同源）。
  - 一条都不剩 → 显示禁用的 `（空）`，说明"没有可加的了"，而不是弹一个空菜单。
  - 加/删 send 会走 `RoutingChanged` 重建行；**改增益不重建**（否则拖动中的推子会被抽走）。
- 增益推子量程沿用 §4.3（−∞..+12）。
- 不是"确认 / 取消"式：改动即时生效（与调音台其它控件一致），undo 粒度 = 一次拖动。
- 关掉调音台窗口时，浮窗一起关。

### 6.3 插入效果浮窗

- 点 `FX N` → 同样风格的浮窗。每条插入一行：序号、名称 + 格式小标签、`旁通`、`界面`、`✕`。
  - `旁通` = **跳过这个效果器**（音频直通），槽位与顺序都不变；按下后该行变暗、按钮保持按下态。
  - `界面` = 打开插件自己的设置窗；插件没有界面（或 JSFX 没有 `@gfx`）时按钮置灰。
- 顺序 = 处理顺序（从上到下）。v1 只做"加到末尾"与"移除"；调整顺序留到 M7（▲▼ 或拖拽）。
- 底部 `+ 添加效果 ▾`：**三级菜单** —— ①格式（`VST3` / `VST2`）→ ②厂商 → ③插件名。
  同厂商的插件按名字排序，厂商之间也按名字排序；没有厂商的那批归到「（未知厂商）」。
  - **乐器不列**：插入点是"处理已经存在的音频"，乐器要 MIDI、要自己产声，插进来只会白占一条链。
    判据来自扫描（见 §5.2 的 `probeInstruments`）：VST2 过桥问 `effFlagsIsSynth`，
    VST3 读二进制里的类别标记。判不了的一律当效果器列出来（宁可漏判，不可误判 ——
    误判会让用户以为"插件没扫到"），真点进去时 `Create` 还有一道硬拒绝。
  - VST2 的真名与厂商来自**扫描时过桥加载**一次，所以 VST2 也是完整三级。
    VST3 不加载，只有 bundle 名（没有厂商）→ 全部归「（未知厂商）」，实际是两级。
  - 格式不可用 / 一个都没扫到时，那个格式退化成一条禁用的叶子，
    写明原因（`仅 Windows` / `尚未实现` / `没扫到效果器，去「设置 → 插件扫描」扫一次`）。
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
- 删除：AUX 条**右键菜单** `删除此 AUX`。
  **不做确认框**：补丁层没有对话框服务（`IHostServices` 里没有），而"删除 + 改指"整段套在一个
  merge 作用域里 → **一步 undo 就能全还原**。可撤销比一个确认框更实在，也不用为它新开一处宿主接缝。
- v1 只放调音台，不在编辑器轨道列表里重复开一个入口（两处 UI 容易两套状态）。

### 6.7 窗口生命周期

三条硬规则（都在 `MixerPatch` 里，`MixerPreview` harness 有 headless 冒烟检查）：

1. **只能有一扇**：再点菜单只把现有那扇带到前面（隐藏过的重新 `Show`，可见的 `Activate`），绝不新建。
   窗口被真正关掉（不是隐藏）时补丁会清掉引用，下次点才重建。
2. **归属主窗口**（owned window）：主窗口在前台时它不会被盖住；打开时贴在主窗口右侧，
   放不下就贴屏幕右缘并夹进工作区。
3. **随主窗口关闭**：主窗口一关，补丁立刻 `Detach` + `Close` 它 —— 否则"最后一个窗口"还在，
   `ShutdownMode.OnLastWindowClose` 不触发，进程不退出、音频继续跑。

### 6.8 改名 / 分组线 / 横向滚动 / pin

- **改名**：双击通道条的名字就地编辑（Enter 提交、Esc 取消、失焦提交）。写回 `Track.Name` /
  `AuxBus.Name`，一步 undo。空名字不接受（轨道头也靠名字认轨）。
  - **改 AUX 名要让别处跟着变**：这个名字不只出现在 AUX 自己那一条上，还出现在每条轨的 send 目标行、
    所有通道的 output 接线菜单、以及通道条上那行短名里。所以
    ① `IMixerOutput.Name` / `ShortName` 是**活读**的（委托，不是构造时拷下的字符串）；
    ② `AuxBus.Name.Modified` 触发 `ChannelsChanged`（整表重建 + 关掉浮窗，**不重建音频图**——
       改名不是结构变化）。少任何一半都会留下"改了 AUX1 的名字，别的条上还写着 AUX 1"。
- **分组线**：轨道组与 AUX 组之间一条 2px、`LINE` 85% 的亮线（两组都有内容时才出现）。
- **横向滚动**（轨道多时）：
  - 滚轮：窗口内滚轮 = 横向滚动（一格 60 DIP）；
  - 拖动：按住通道条**头部**（色条那一行）左右拖 = 横向平移。增量用**屏幕坐标**算
    （通道条自身会随滚动移动，用窗口内坐标会把"滚动"和"指针移动"叠在一起）。
- **pin**：通道条右键 → 固定到左侧 / 固定到右侧 / 取消固定。固定的条**不进滚动区**，
  分别堆叠在左右两侧，与滚动区之间各有一条同样的亮线；Master 不滚动，所以没有 pin。
  - `MixerPin` 存在 `Track.Pinned` / `AuxBus.Pinned`（`DataProperty`，一步 undo），
    并随工程落盘（载荷里的 `pin` 字段）。
  - pin 变化会改变**布局**，所以适配器发 `ChannelsChanged` 让窗口整表重建。

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
  - **限定条件**：比较的是"混音算术"，所以要在**限幅器关闭**（`LimiterEnabled = false`，引擎单测的默认）
    或信号不越界的情况下比。有削波时输出级限幅会介入（§3.10），那正是修复的目的。

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
| 9 | `EP-MIXER-6` | `Data/NativeTrackInfo.cs` + `TuneLabProjectCbor.cs` | 序列化路由 / 插入：顶层键 `epMixer`（**版本保持 1**）+ 同目录 sidecar，见 §9 | 中 |
| 10 | `EP-MIXER-7` | `TuneLab.csproj` | 三条 `ProjectReference`（`ExperimentalPatch` 已有，加 `MixerEngine` + `InsertHost`） | 低 |
| 13 | `EP-MIXER-12` | `TuneLab.csproj` + `ExperimentalPatchBootstrap.cs` + `TuneLab/ExperimentalPatchInsertSettings.cs`（新） | 插件扫描设置经 `IHostServices.InsertPlugins` 递出 | 零 / 低 |
| 14 | `EP-MIXER-13` | `InsertHost/VstBridgeClient.cs` + `VstBridge/*.cs`（均新文件） | 32 位 VST2 的桥进程（不在 sln，宿主按需启动） | 零 |
| 11 | `EP-MIXER-8` | `UI/MainWindow/Editor/Editor.cs` | 「实验功能」菜单（v1 已规划） | 低 |
| 12 | `EP-MIXER-9` | `ExperimentalPatchBootstrap.cs` + `TuneLab/ExperimentalPatchMixerAudio.cs`（新） | `CreateMixerModel()` 薄转发 + 快照编译器 | 零 / 低 |
| 15 | `EP-MIXER-16` | `UI/Settings/SettingsWindow.axaml.cs` + `ExperimentalPatchBootstrap.cs` + `ExperimentalPatch/Abstractions/SettingsTabContribution.cs`（新） | 「设置」窗尾部接上补丁贡献的 tab（插件扫描路径）。宿主 tab 列表原样不动，没有贡献时与上游逐项一致 | 低（一段 `foreach`） |

`EP-MIXER-*` 的逐条说明、断点症状与重接步骤见 `.agents/mixer-reconnect.md`。

## 9. 工程格式（tlpx）—— 必须与原版共存

**目标（已定）**：补丁保存的工程要能被**原版 TuneLab 正常打开**；更理想的是"原版打开 → 编辑 → 保存 →
回到补丁编辑器，VST 状态还在"。

### 9.0 当前状态：**两处都已落盘**（2026-10-20）

- **文件内**：`.tlpx` 顶层键 `epMixer`（紧凑 JSON 文本），宿主 `version` 仍为 1。
  → 工程自包含：只拷 `.tlpx` 也能带走路由。
- **同目录 sidecar**：`<工程文件>.epmixer.json`（缩进 JSON，可读可手改）。
  → 原版往返（打开 → 编辑 → 保存）之后，我们的数据仍在。
- **装载顺序**：文件内 `epMixer` → sidecar → 空。
- 实现：`TuneLab/ExperimentalPatchMixerPersistence.cs`（`Serialize` / `Apply` / `Save` / `Load`）；
  接驳点：`Editor` 的三处保存都写载荷、打开时按顺序装载，CBOR 读写各加一段
  （`NativeProjectFormat.cs` 的 `EpMixer` 字段 + `TuneLabProjectCbor.cs` 的读写分支）。
- 失败一律只记日志：载荷/sidecar 损坏不会挡住打开工程。
- **换工程**：补丁拿到的是 `MixerModelFacade`，它订阅 `ProjectDocument.ProjectHolder`，
  工程被替换时自己换适配器并发 `ChannelsChanged` —— 窗口因此跟着换，不用重开。

### 9.1 三条硬约束（已被测试钉住）

| 事实 | 出处 | 结论 |
|---|---|---|
| 原版遇到 `version > CURRENT_VERSION(1)` 直接抛 "Unsupported Version" | `TuneLabProjectCbor.cs:67` | **版本号永远保持 1**，绝不为补丁升版本 |
| 原版读取器遇到不认识的顶层键走 `default: SkipValue()` | 同上 `:86-88` | 可以加自己的顶层键，原版照样能打开 |
| 原版**保存**时从 DTO 重建整个文件 | `WriteProject` / `WriteTracks` | 我们加的键**会被丢掉**（`UpstreamRoundTrip_DropsUnknownKeys` 钉住了这条） |

回归测试：`tests/TuneLab.Tests/ProjectFormatCompatibilityTests.cs` 四条（未知键被跳过 / 版本 2 被拒 /
我们写出的版本是 1 / 原版往返丢键）。**动格式代码必须让这四条保持绿。**

### 9.2 一份数据，两处落盘

- **文件内**：顶层键 `epMixer`（原版跳过它）。其中 `branch` 是**我们自己的 schema 号**（现在 1），
  与宿主 `version` 解耦：宿主版本永远不动，我们靠 `branch` 演进。
  → 工程自包含；只拷 `.tlpx` 也能带着路由与插入。
- **同目录 sidecar**：`<工程文件名>.epmixer.json`。原版不认识它、也不会动它。
  → 原版往返（打开→保存）之后，我们的数据仍在。

加载顺序：**文件内 `epMixer` → sidecar → 空**。两者都在时以文件内为准（它是随工程一起被编辑的那份）。

### 9.3 往返之后怎么对上轨道

原版不保留我们加的任何 id（它只认识自己的字段），所以回来只能按**名字 + 序号**重新对：

1. 轨道数一致 → 按序号对；
2. 不一致 → 先按 (序号, 名字) 对，再按名字对；
3. 仍对不上的 → **丢该轨的路由**，记一条日志（宁可少一条路由，也不要把 A 的 send 安到 B 上）。

代价写在明处：**改名或重排轨道之后，那一条轨的路由可能丢**。AUX 本身是 `epMixer` 里的原生数据，
不依赖轨道匹配，所以 AUX 与它自己的 send 不受影响。

### 9.4 结构

```jsonc
"epMixer": {
  "branch": 1,
  "auxes": [ { "id": "aux-1", "name": "AUX 1", "gain": -2, "pan": 0, "mute": false, "solo": false,
               "output": { "kind": "master", "id": "master" },
               "sends":  [ { "kind": "master", "id": "master", "gain": -3 } ],
               "inserts":[ { "format": "vst3", "pluginId": "...", "bypass": false, "state": "<base64>" } ] } ],
  "tracks": [ { "index": 0, "name": "主唱",
                "output": { "kind": "aux", "id": "aux-1" },
                "sends":  [ { "kind": "aux", "id": "aux-1", "gain": -6 } ],
                "inserts":[ ... ] } ]
}
```

（磁盘上是 CBOR map，这里只是画结构。）

### 9.5 纪律

- **不动 SDK 的 `TrackInfo`**（冻结面）：路由与插入只走 `epMixer` 与 sidecar，通用格式（MIDI 等）导出时自然丢弃。
- sidecar 只在"文件内没有 `epMixer`"时被采用 —— 说明这个工程最后一次是被原版（或别的工具）保存的。
- sidecar 过期（用户回滚了工程文件）无法完全避免；靠 9.3 的名字 + 序号匹配把影响限制在单轨，并记日志。
- `pluginId`：VST3 用 `classId` + 插件名兜底；VST2 用 **`"vst2:" + 文件名`**
  （扫描阶段不加载插件，拿不到 `uniqueId`；重名插件会撞 id，这是有意的取舍，
  见 §9.4）；JSFX 用工程内相对路径。

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
| M4 | AUX + output + send：模型 / 图 / 两个 solo 域 / undo / **AUX+Master 电平表**（**已完成**）；落盘暂缓（§9.0） | 建两条 AUX、做 send，电平与听感符合预期；环路被拒；AUX solo 不影响轨道；播放时 AUX/Master 表跟着跳 |
| M5 | 混音器打磨：`+ AUX` / 删除 AUX（改指 Master）、output 菜单、空态、窗口几何记忆（**已完成**）、「实验功能」菜单入口（**已完成**）；`+AUX` 待 M4 | 菜单能开关窗口；加一条 AUX 立刻出现；删 AUX 后路由被改指并提示 |

### 阶段二：插入效果（后做）

| 里程碑 | 内容 | 验收 |
|---|---|---|
| M6 | `IInsertHost` + **VST3**（OwnAudioVST3，独立 ALC） | 挂一个 VST3：出声、存读、延迟被 PDC 补偿 |
| M7 | **VST2：一律过桥**（x86 + x64 两个桥，§5.3）。**已完成**：`PluginHost` 的 C ABI、`Vst2Backend`/`Vst2Insert`、位数判定、参数/状态、桥的多任务串行化 | 51 个真插件（46 × x64 + 5 × x86）全部出声、旁通逐位一致；插件崩只死桥 |
| M8 | FX 浮窗接线：旁通 / 界面 / 增删；插件扫描缓存；找不到插件的降级。**扫描路径设置已落在宿主设置窗的一页 tab（`EP-MIXER-16`）** | 与外观稿一致；插件缺失不丢链 |
| M9 | JSFX（§12 Q4：先 spike 再定） | — |

### 阶段三：以后

多设备输出（监听用，§4.4）、`daw_cable`（§4.5）、插入参数自动化、分轨批量导出。

## 11. 风险登记

| 风险 | 影响 | 应对 |
|---|---|---|
| 引擎重写引入实时毛刺 / 爆音 | 高 | 先做 M3（无 AUX 的退化图），用现有测试与听感做基线；回调里零分配 |
| PDC 算错 | 中 | 用"两条等长路径"与"一条带 1024 样本延迟插件"的用例验证相位 |
| VST 崩溃带走应用 | 高 | VST2 已经全部过桥（§5.3）；VST3 仍在进程内（vendored wrapper），后续可沿用同一套桥 |
| JSFX 没有现成解释器 | 高 | §12 Q4；先 spike（ysfx 能否 P/Invoke + @gfx + 许可） |
| VST2 许可与 SDK 合规 | 中 | 只做运行时加载、不分发 SDK；文档写明 |
| 工程格式兼容 | 中 | 版本 +1，老版本硬失败；native DTO 不进 SDK |
| 上游合并冲突 | 中 | §7 选项 A；改动集中在 3~5 个文件，尽量整段替换而非散点修改 |
| 范围失控 | 高 | v1 明确不含：多设备、pre-fader send、分轨批量导出、参数自动化（AUX 的改名与 pan 已做，见 §6.6 / §4.1） |

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













