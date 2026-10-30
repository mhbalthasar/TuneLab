# 2.8 侧链输入 · 设计稿（已实现）

> 状态：**已实现**（2026-10-27）。实现结论已并入 `DESIGN.md` §5.7.32，本文保留"为什么这么定"。
> 路线图条目：`ROADMAP.md` §2.8。

## 0. 一句话结论

侧链真正缺的不是"再画一个菜单"，而是**插件的第二个输入**。查过三个后端之后结论与原来的
假设相反：

- **VST2 反而好做**：VST2 的侧链就是"插件声明多于 2 个输入"，而我们的桥已经能传多声道，
  只差把共享环从 2 声道加宽到 4、把第 3/4 条平面填成侧链信号。本机实测 ReaComp / ReaGate /
  TDR Molotok 报 4 进，TDR Nova / ZamCompX2 / ZamGateX2 报 3 进 —— 都是能直接用的。
- **VST3 反而做不了**：随包分发的原生宿主 `ownvst3.tlhost.dll` 只有 36 个导出，
  `AudioBufferC` 是单一输入总线，**没有任何总线查询/激活/侧链接口**。要支持标准 VST3
  侧链总线，必须 fork 上游 C++/JUCE 并重新构建原生库。
- 所以这一项按 **VST2 先行、VST3 按"插件是否声明了多于 2 个输入"顺带支持** 来设计，
  不为 VST3 单独开 fork。

## 1. 现状：缺的是哪一环

### 1.1 引擎

`IMixInsert.Process(Span<float> buffer, int offset, int count)` 只有一个就地缓冲；
`Mixer.RenderChunk` 在推子/声像之后、分发之前按顺序调用它（`Mixer.cs` 264–271 行）。
`MixNode` 只有 `Inserts` 与 `Outputs`，没有第二个输入。

### 1.2 VST2（桥进程）

`VstBridge.PluginHost.Process` 已经按 `slot.InPlanes.Length` 逐条填平面，
`BridgeAudioFrames` 也是按 `channels` 泛化的 —— **桥的逻辑本来就是多声道**。
唯一的硬限制是 `SharedAudioLayout.MaxChannels = 2`（共享环槽位大小由它算），
以及 `Vst2Insert` 把 `mChannels` 写死成 `SharedAudioLayout.MaxChannels`、
用 `ch % 2` 把主信号复制到多出来的平面上。

### 1.3 VST3（进程内原生宿主）

`Vendored/OwnVST3Delegates.cs` 列出的导出里没有任何总线相关项；`AudioBufferC` 是
`{ inputs, outputs, numChannels, numSamples }`。`Vst3Insert.ProcessChunk` 同样把所有平面
填成同一个立体声（`ch % 2`）。即使插件声明了 4 个输入，侧链听到的也是主信号。

### 1.4 模型 / 界面 / 持久化

`IMixerInsert` 没有侧链字段；FX 浮窗每行只有 ▲▼ / 旁通 / 界面；`InsertDto` 没有源通道。
这些是最后一步，不是难点。

## 2. 关键事实：VST2 的"多输入"就是侧链

VST2 没有侧链标准，但有一个**既成惯例**：需要外部键的插件声明 `numInputs > 2`，
第 3 条起就是侧链输入（REAPER 等宿主就是这么喂的）。这不需要任何私有扩展。

本机探针（`VstBridgeClient.TryLoadAsync` 读 `LoadPluginResultMessage.InputChannels`）：

| 插件 | 位数 | 输入 | 输出 | 侧链通道 |
|---|---|---|---|---|
| ReaComp | 64 | 4 | 2 | 2（立体声） |
| ReaGate | 64 | 4 | 2 | 2 |
| TDR Molotok | 32 | 4 | 2 | 2 |
| TDR Nova | 32 | 3 | 2 | 1（单声道） |
| ZamCompX2 | 64 | 3 | 2 | 1 |
| ZamGateX2 | 64 | 3 | 2 | 1 |
| TDR Kotelnikov | 32 | 2 | 2 | 无 |

这张表说明两件事：

1. 侧链能力**可以自动判定**：`InputChannels − 2`，不需要插件白名单。
2. 本机就有能验证的插件（ReaComp 4 进、TDR Nova 3 进），不用等用户装 Pro-C。

**但"多于 2 个输入"不等于一定是侧链**（也可能是 L/C/R 之类）。所以界面措辞必须是
"侧链输入 · 插件声明 4 进"，而不是替用户断言"这个插件有侧链"。用户按下去之前
看到的是事实，不是猜测。

## 3. 方案取舍

### 方案 A：插件侧链（本文采用）

把第二条输入送进插件自己的第 3/4 个输入口。VST2 走桥，VST3 只在插件报 `>2` 输入时走同一套。

- 优点：真正的侧链 —— 用插件自己的阈值/比率/释放，听得出来是那个插件在工作。
- 代价：共享环加宽；VST2 实例要多填两条平面；VST3 只能覆盖"把键放在主输入总线"的插件。
- 风险：中等偏低。不动原生库，不改插件加载路径。

### 方案 B：引擎内建 ducking 插入（备选）

在 `MixerEngine` 里写一个真正的 DSP 插入（阈值/比率/attack/release/depth），侧链源走同一套路由。

- 优点：不依赖任何插件，全平台可用，纯 C# 可单测；路由/PDC/持久化这部分与 A 完全一样。
- 代价：只解决"躲"这一件事，不能给第三方插件喂键。
- 适合：如果 A 的共享环改动风险不可接受，B 是能立刻交付价值的退路。

### 方案 C：先把界面做出来、后端置灰（否决）

两个后端都不支持时，这个功能等于不存在，还多了一排永远点不动的控件。
违反"不做假控件"的既有纪律。

### 方案 D：fork ownvst3 加侧链总线（暂缓）

要给 VST3 加标准 `kSidechain` 总线支持，需要：

- 在 `OwnVST3Juce` 里加 `VST3Plugin_GetAudioBusCount` / `GetAudioBusInfo` /
  `SetBusActive` / `ProcessAudioEx`（两个 `AudioBufferC`）；
- 重新构建 win-x64 原生库并替换 `Native/win-x64/ownvst3.tlhost.dll`；
- 维护一个上游 fork，每次升级按 `Vendored/README.md` 的步骤重打补丁。

这不是"再写点 C#"，是一个独立的原生交付物。**本轮不做**，等有明确的第三方侧链插件需求再议。

## 4. 设计

### 4.1 能力判定

| 后端 | 有侧链的条件 | 键通道数 |
|---|---|---|
| VST2 | `InputChannels > 2` | `InputChannels − 2` |
| VST3 | `ActualInputChannels > 2`（实验性，见 §8 Q4） | `ActualInputChannels − 2` |

判定值来自**已经建好的实例**，不落盘、不进扫描缓存：

- `Vst2Insert.Create` 已经拿到 `loaded.InputChannels`（现在只用来判"0 进 0 出"，没存下来）；
  `Vst3Insert` 也知道 `ActualInputChannels`。两个实例各存一份；
- `IMixInsert` 加 `int SidechainChannels => 0;`（DIM），两个 VST 实例按 `max(0, 输入数 − 2)` 覆盖；
- 模型侧 `IMixerInsert.SidechainChannels` 通过工厂向实例现问（实例没建好时返回 0）。

这样不需要改扫描缓存、不需要强制用户重扫，也不会出现"缓存说 4 进、实际插件已经换了"的漂移。
代价是工程刚打开、图还没编译的那一瞬间侧链按钮是灰的 —— 编译完成后 `Rebuild()` 就会亮。

### 4.2 路由模型

侧链是**插入级的**，不是通道级的：同一个通道上的两个插件可以吃不同的键。

- 源：任意 `Track` 或 `AUX` 通道。**Master / Device 不能当源**（Master 是所有路径的终点，
  拿它当键必然成环）。
- 目标：某个插入槽位。一个插入最多一个源。
- 引用按 **id 字符串** 存（与 `Output` / `Send` 同一个 id 空间），不存对象引用 ——
  对象引用会被 undo / 重建图换掉（VCA 那一版的教训）。

`MixGraphBuilder` 新增：

```csharp
public void ConnectSidechain(NodeDraft from, NodeDraft to, int insertIndex);
```

它做三件事：

1. 把 `from → to` 加进**排序依赖**（indegree），保证源节点在这一块里先渲染；
2. 把这条边记进 `to.SidechainEdges`，**不**记进 `to.Edges`（不参与混音求和）；
3. 建图时算出每条侧链边的延迟（见 §4.5）。

`MixNode` 新增两侧的视图：`SidechainOutputs`（源侧：写到哪块缓冲、延迟多少）与
`SidechainInputs`（目标侧：读哪块缓冲、喂给第几个插入）。两者由同一份 `SidechainEdge`
列表派生，每条边有一个 `BufferIndex`；`Runtime` 按边数预分配 `maxBlockSize * 2` 的缓冲，
每块开头与节点缓冲一起清零。

### 4.3 取信号点

**推子后、插入链后、分发前** —— 也就是源通道"离开通道时"的那份信号，与它的输出边读的是同一个
`target`。理由：

- 与专业 DAW 的 send 位置一致（send 在插入之后），用户听到的源通道是什么，键就是什么；
- 源通道静音 / 被 solo 压掉时 `hasSignal = false`，侧链缓冲保持静音 —— "听不到的通道不会去压别人"，
  这是符合直觉的；
- 不需要额外的取信号遍：就在现有的 `Outputs` 循环里，把侧链边当成"写到另一块缓冲的输出边"。

代价：源通道自己的压缩器会改变键（后级处理影响前级）。这是 send 语义的固有结果，不是 bug；
要"裸信号"的用户可以把源通道的插入旁通。

**不提供 pre-fader 选项**（v1）。现有 `MixEdge.IsPreFader` 是为 send 做的，侧链再叠一层会让
"这个推子到底影响什么"变得难解释。等有人明确需要再加。

### 4.4 引擎：第二条输入

`IMixInsert` 加一个带默认实现的重载：

```csharp
void Process(Span<float> buffer, int offset, int count, ReadOnlySpan<float> sidechain)
    => Process(buffer, offset, count);
```

默认实现 = 忽略键。七个**不包装别人**的实现（`GainInsert` / `FailingInsert` / `BypassedInsert` /
`RecordingInsert` / `Doubler` / `Vst2Insert` / `Vst3Insert`）一个都不用改就能编译。

**但包装器必须转发**：`WetDryInsert` 与 `TrimInsert` 都要重写这个重载并把 `sidechain`
原样传给 `mInner`。漏掉这一条的症状很隐蔽 —— 只有"mix < 1"或"trim ≠ 0"的插入会丢键，
默认值下一切正常。测试要专门盯这一条。

`Vst2Insert` 的实现：

- 无侧链源、或 `SidechainChannels == 0` → 走原来的 2 声道路径，**逐位不变**；
- 有侧链源 → `mChannels = InputChannels`，平面 0/1 填主信号，2/3 填键
  （1 条键通道时填 `(L + R) * 0.5` 的单声道下混，2 条时填 L/R）；
- 桥侧 `PluginHost.Process` 已经会按 `slot.InPlanes.Length` 逐条拷贝，**逻辑不用改**。

`Vst3Insert` 同构：`ActualInputChannels > 2` 时平面 2/3 填键。没有原生改动，
能不能用完全取决于插件有没有把键放在主输入总线上 —— 所以标为实验性。

### 4.5 PDC

侧链必须与主信号在**插入的输入点**对齐。记：

- `own[i]` = 节点 i 的插入链延迟之和
- `entrance[i]` = 从音源到 i 的**入口**的实际延迟（轨道源 = 0；AUX = 入边延迟之和）
- `output[i]` = `entrance[i] + own[i]`
- `down[j]` = 从 j 的入口到 sink 的最长延迟（现有，**只算音频边**）
- `before(D, k)` = D 的插入链里第 k 个之前的延迟之和
- `maxLatency` = 全图最长（现有）

侧链边 S→D（插在第 k 个）的延迟：

```
delay = entrance[D] + before(D, k) − output[S]
```

（小于 0 时钳到 0。）

推导：键在 S 的**出口**取，已经带了 `output[S]`；D 的第 k 个插入输入点在
`entrance[D] + before(D, k)`。两者相减就是键要补的延迟。注意**不是**
`maxLatency − own[S] − down[D] + before`：那个公式只在 D 位于最长路径上时成立
（此时 `maxLatency − down[D]` 才等于 D 的入口延迟）。D 不在最长路径上时它会多推
`maxLatency − down[D]`，键比主信号晚一截。

主混音的 PDC 也改成同一个前向传播（`entrance[]` / `output[]`）：AUX 总线带上游延迟时
不再被多推一次（旧公式用 `own[i]`，把"上游"漏掉了）。

**侧链边不参与 `entrance[]` / `output[]` / `down[]` / `maxLatency` 的计算。** 这是关键：
加一条侧链**不会改变主混音的延迟补偿**，因此同一个工程"有侧链 / 没侧链"的可听结果
只差插件本身的作用。

副作用：排序依赖会改变拓扑序，主混音的浮点求和顺序可能变（听不出来）。§7.2 的逐位要求
只针对**退化图**（没有侧链），这条不冲突。

### 4.6 环路与非法源

- 源 == 目标通道 → 拒绝（自环）。
- 目标通道（经音频边或已有侧链边）能走回源 → 拒绝。复用 `WouldCreateCycle`，
  但**必须把已有侧链边也算进邻接表**，否则两个插入可以互相侧链，建图时才炸。
- Master / Device 作源 → 拒绝。
- 源通道被删除 → 清掉所有指向它的 `SidechainSourceId`（同一个 merge 作用域，
  然后 `RaiseRoutingChanged`）。
  - AUX 的 id 单调、不复用，删除时由 `RemoveAux` 在既有 merge 作用域里清引用；
  - Track 的 id 在实现时改成了**稳定序号**（`MixerTrackIds`，见 §4.10），删除 / 换序
    都不会让引用指到另一条轨。轨道删除**不**强制清引用：悬空引用由编译器跳过，
    而 undo 恢复的是同一个 Track 对象（同一个序号），引用会自动复活。

### 4.7 模型与界面

`IMixerInsert` 新增：

```csharp
int InputChannels { get; }             // 插件声明的输入总数（界面措辞要如实）
int SidechainChannels { get; }         // 0 / 1 / 2
string? SidechainSourceId { get; }     // null = 没有
void SetSidechainSource(string? id);   // 结构变化 → 重建图
```

`IMixerChannel` 新增 `bool CanSidechainFrom(IMixerChannel source)`（自环 / 成环 / 类型判定
都在这里，界面只负责问）。

能力来自**已经建好的实例**，不是扫描缓存：缓存说 4 进、插件换了就会喂错。代价是工程刚打开、
图还没编译的那一瞬间 `SidechainChannels == 0` —— 这时不显示侧链图标（编译完再打开浮窗就正常）。

界面只在 **FX 浮窗**里加，通道条不动（已经够密）：

- 每行在「旁通」与「界面」之间加一个侧链图标 ⛓（22×22）；
- `SidechainChannels == 0` → **整列压成 0 宽、按钮隐藏**（不是置灰：2 进插件占大多数，
  每行多一个灰键只是噪音）；悬浮提示「侧链输入」；
- 点开是一个菜单：`无` / 分隔线 / `轨道` 组 / `AUX` 组；组标题是不可点的灰字；
  当前源打勾；成环的源不列出；一个候选都没有时显示灰字"没有可用的侧链源"；
- 选中后插件名后面显示 `← 人声`（高亮色）；源通道被删掉后显示 `（已删除）`，
  但引用保留（undo 恢复同一条轨时会自动复活）。

### 4.8 持久化

`InsertDto` 加一个字段：

```csharp
public string? SidechainId { get; set; }   // 源通道 id；null = 没有
```

`TrackDto` 再加一个字段：

```csharp
public int MixerId { get; set; }           // 轨道的稳定序号；0 = 老工程，懒分配
```

老工程读进来是 null / 0 = 没有侧链、序号懒分配，行为不变。写盘 / 读盘各一条往返测试。

### 4.9 退化图不变

没有任何侧链的工程：

- 建图时 `SidechainBufferCount == 0`，`Runtime` 不分配键缓冲；
- `RenderChunk` 里走原来的 3 参数 `Process` 调用（不是"传一个空 span"）；
- `Vst2Insert` 仍然只发 2 条平面（`channels = 2`），桥的行为与旧版一致。

所以 `DESIGN §7.2` 的"逐样本 bit 相同"仍然成立，`MixerSidechainTests` 有专门的断言。

### 4.10 轨道稳定序号（实现时的偏离）

设计稿原本写的是 `SidechainId = "track:N"`，N 是**列表下标**，靠"删通道时清引用"兜底。
实现时发现这不够：

- 删掉第 0 条轨，后面每条轨的 id 都会往前挪一格 —— 引用会**静默指到另一条轨**上，
  比悬空更糟（悬空至少会被跳过）；
- 删了再建，新轨复用旧下标 —— 旧引用"复活"。

所以新增 `TuneLab/ExperimentalPatchMixerTrackIds.cs`：按 Track **对象身份**分配单调递增的
序号（`MixerTrackIds.OrdinalOf`），模型侧通道 id 变成 `"track:" + 稳定序号`。序号跟着对象走：

- 会话内：重排 / 删除 / undo 都不改变已有轨的序号（undo 恢复的是同一个对象）；
- 落盘：序号写进 `TrackDto.MixerId`，装载时按匹配到的轨 `Assign` 回去；
- 新轨：拿一个新号，**绝不复用**（复用的窗口正是"删了又建"）。

引擎节点 id 仍然是 `"track-" + 编译期下标`（电平查询依赖它，绝不能改）；编译器在
`nodes` 里为同一条轨登记**两个键**：`"track-N"`（引擎 / 电平）与 `"track:序号"`（侧链引用）。

静态表不清理：条目是小对象，进程生命周期内保留，同时保证序号绝不复用。

## 5. 实施拆解

四步，每步都能独立编译、独立验收。**四步均已落地**（2026-10-27），偏离点见 §4.10。

**第一步 · 桥与实例（无界面）**

1. `SharedAudioLayout.MaxChannels` 2 → 4；`BridgeProtocol.ProtocolVersion` 1 → 2，
   Hello 握手校验版本（旧桥必须被拒绝，不能"能连上但按 2 声道解读 4 声道布局"）。
2. `Vst2Insert` 保存 `InputChannels`；无侧链时行为不变；有侧链时填平面 2/3。
3. `IMixInsert` 加带 DIM 的四参数重载；`WetDryInsert` / `TrimInsert` 转发。
4. 单测：`WetDryInsert` / `TrimInsert` 把键原样传给内层（用一个记录键的假插入）。

**第二步 · 引擎路由与 PDC**

5. `MixGraphBuilder.ConnectSidechain` + 排序依赖 + 侧链延迟公式。
6. `MixNode.SidechainOutputs` / `SidechainBufferForInsert`、`Runtime.SidechainBuffers` /
   `SidechainDelayLines`、`RenderChunk` 的写入与调用。
7. 单测：对齐（§4.5 的例子）、成环拒绝、退化图逐位不变。

**第三步 · 模型与界面**

8. `IMixerInsert` / `IMixerChannel` 的新成员；`MixerModelAdapter` 接上工厂与 id 解析；
   `SetSidechainSource` 走"结构变化"（`RaiseRoutingChanged`），不是活值。
9. `FxPopupWindow` 的侧链图标 ⛓ 与菜单。
10. 删 AUX 时清引用（`RemoveAux` 的既有 merge 作用域内）；轨道改用稳定序号（§4.10）。

**第四步 · 持久化与收尾**

11. `InsertDto.SidechainId` / `TrackDto.MixerId` + 往返测试。
12. `ROADMAP.md` 打 ✅、`DESIGN.md` 新增 §5.7.32、`ExperimentalPatch/.agents/mixer-reconnect.md`
    登记 `EP-MIXER-27`。

## 6. 验收清单

自动化覆盖（`MixerSidechainTests` / `MixerRoutingTests` / `InsertModelTests` / `SharedAudioRingTests`）：

- [x] 没有任何侧链的工程：逐位断言仍然通过（`GraphWithoutSidechainNeverAllocatesKeyBuffers`
      + 既有的 `MixerAudioSeamTests`）。
- [x] 2 进插件：侧链图标不出现（列宽压 0）。
- [x] 把源通道设成目标通道的下游：`CanSidechainFrom` 返回 false，菜单里不出现。
- [x] 删掉源 AUX：目标插入的侧链自动清空；删轨道：引用保留但编译器跳过（稳定序号）。
- [x] 存盘再打开：`SidechainId` 与 `TrackDto.MixerId` 往返（`SidechainSource_SurvivesSaveAndLoad`）。
- [x] 侧链 + 湿/干 mix < 1：仍然有侧链（包装器转发测试，`TrimInsert` 同）。
- [x] 加侧链不改变主混音的 `MaxLatencySamples`，也不改变主混音样本（侧链源静音时逐位相同）。

需要真插件 / 手动听感（开发机上没有对应的 64 位插件时无法自动跑）：

- [ ] ReaComp 4 进：播放 A + B，A 响时 B 的增益衰减跟着动；把 A 静音后 B 不再被压；
      取消侧链后 B 也不再被压。
- [ ] TDR Nova 3 进：单声道键能驱动动态 EQ（`(L+R)*0.5` 下混）。
- [ ] 停止播放再开始：键不滞后一块（没有"上一块"实现）。

## 7. 风险与坑

| 风险 | 后果 | 处置 |
|---|---|---|
| 宿主与桥的 `MaxChannels` 不一致 | 按 2 声道解读 4 声道布局 → 爆音/错位 | 协议版本升到 2，握手拒绝旧桥；桥必须重新 `publish-bridges.ps1` |
| 包装器忘了转发键 | 只有 mix<1 / trim≠0 时丢侧链，极难发现 | 专门单测；`WetDryInsert` / `TrimInsert` 两个都要 |
| 4 进插件不是侧链（如 L/C/R） | 用户开了侧链后声音变了 | 界面措辞是"插件声明 N 进"，不替用户断言 |
| Track id 曾是列表下标 | 删轨 / 换序后引用指错，删了再建会复用 | 改成稳定序号（§4.10），序号落盘在 `TrackDto.MixerId` |
| 侧链边进了 `down[]` | 加侧链会改变主混音延迟 | 侧链边只进排序，不进 PDC 汇总（`MixerSidechainTests` 钉住） |
| 音频线程分配 | 爆音 | 侧链缓冲在建图时一次分好 |
| VST3 `ActualInputChannels` 语义未验证 | 可能永远报 2，或报了但不是侧链 | 标实验性；VST2 是主路径 |

## 8. 待定问题（已全部拍板）

1. **取信号点**：推子后 / 插入后（`RenderChunk` 在插入链之后写键缓冲）。不做 pre-fader 选项。
2. **单声道键的下混**：`(L + R) * 0.5`（只取 L 会丢右声道能量）。
3. **Master 插入能不能吃侧链**：能（Master 也是节点）；但 Master 自己不能当源。
4. **VST3 要不要跟着开**：`ActualInputChannels > 2` 时顺带支持，标实验性；标准
   `kSidechain` 总线仍然不做（vendored 原生宿主没有总线接口，不 fork）。
5. **要不要侧链增益**：不做 —— 插件自己有阈值，多一个增益就多一个藏电平错误的地方。
6. **界面位置**：只在 FX 浮窗；通道条不加常驻指示。
