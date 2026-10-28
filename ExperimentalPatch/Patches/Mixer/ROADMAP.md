# 混音台 · 与专业 DAW 的差距与任务清单

> 目的：**避免重复分析**。每次评估过的结论都落在这里，下次直接看表。
> 状态：`✅ 已完成` / `🔜 下一步` / `⏳ 排队` / `🚫 明确不做`。
>
> 评估基准：Pro Tools / Cubase / REAPER / Logic 这一档的**混音**功能（不含编曲、MIDI 编辑）。

## 0. 怎么用这张表

- 新需求先查这里：是"已做"（别重复做）、"排队"（按顺序）、还是"不做"（说明理由再议）。
- 一项做完：把状态改成 ✅，并在 `DESIGN.md` 对应小节写清**为什么这么做**（表里只记结论）。
- 判断优先级的口径：**对混音结果的影响 × 实现风险**。纯测量的活（响度表）风险最低，可以并行。

## 1. 已完成

| 项 | 说明 | 落点 |
|---|---|---|
| ✅ 插入链（VST2 / VST3） | 旁通、插件界面、TuneLab 参数面板、状态随工程保存 | DESIGN §5.5–5.7 |
| ✅ 插入顺序 | FX 行 ▲▼，一次移动 = 一步 undo | DESIGN §5.7.19 |
| ✅ Master 插入链 | 总轨效果，槽位在 `Project.MasterInserts`，随工程落盘 | DESIGN §5.7.18 |
| ✅ 通道顺序 | 右键：上移/下移/置顶/置底/移动到…（**插入语义**，非交换） | DESIGN §5.7.16 |
| ✅ 窗口次序 + F3 | 浮窗压在混音台上、混音台压在编辑器上；F3 开关 | DESIGN §5.7.21 |
| ✅ 推子人机 | Shift 精细（0.1 dB）、双击回 unity | 控件内 |
| ✅ 电平表 | L/R 峰值 + 1 秒峰值保持 + `Reset()` | 控件内 |
| ✅ PDC | 插入延迟进图，跨路径补偿 | DESIGN §3.5 |
| ✅ 无插件 GUI 时的参数面板 | 宿主自绘滑杆，插件界面挂了也能调 | DESIGN §5.7.12 |
| ✅ 硬件无 GL 时不炸 | DPF/NanoVG 界面在没有 GL 1.3 的机器上改用参数面板 | DESIGN §5.7.11 |

## 2. 队列（按执行顺序）

### ✅ 通道输入级：Trim / Ø / HPF（已完成，DESIGN §5.7.22）

**为什么排第一**：每个混音都用；只动"源混进通道缓冲 → 推子"这一段，不碰路由结构，风险低。

- `double TrimDb`（-24 … +24，默认 0）
- `bool PhaseInvert`（默认 false）
- `double HpfHz`（20 … 2000，默认关闭；二阶 12 dB/oct，采样率变化时重算系数）
- 位置：`Source.AddData(...)` 之后、`ApplyGain` / 插入链之前
- 数据挂 `Track` / `AuxBus`（Master 不需要 Trim/HPF，只有 Ø 可选 —— 待定）
- 落盘：epMixer payload
- 单测：Trim 增益、Ø 取反、HPF 直流衰减 + 44.1k/48k 系数切换

### ✅ 2.2 Send pre/post（以及 send pan / mute）（已完成，DESIGN §5.7.26）

`Send` 原来只有 `Target + Gain`，且**固定 post-fader**。现在四个字段全齐：
`Target` / `Gain` / `IsPreFader` / `Pan` / `IsMute`。

- `IsPreFader` = true 时 send 在推子之前取信号，不受通道推子影响
- `Pan` = send 自己的声向（与通道条那个无关），同一路送两个 AUX 可以一左一右
- `IsMute` = 这条 send 自己哑掉，不影响同通道的其他 send
- 引擎：`MixEdge.IsPreFader` + `MixNode.HasPreFaderOutput`。有 pre-fader 边时
  `Source.AddData` 改用 unity 读、推子留到 tap 之后才上（否则取不回推子前的信号）；
  没有 pre-fader 边时走原折叠路径，**退化图与旧版逐位相同**
- 测试：`MixerSendStageTests`（6 条）钉住 pre/post 的推子关系、send 声向、静音、
  直达不受影响

### ✅ 2.3 响度表（LUFS / True Peak）+ 表头点击复位（已完成，DESIGN §5.7.27）

纯测量，**零音频风险**，可与 2.1/2.2 并行。K 加权 + 400ms 块 + 门限。

### ✅ 2.4 插入的 wet/dry + 插件增益 + 延迟显示（已完成，DESIGN §5.7.28）

在插入包装层做（dry 副本 + 混合），不动引擎结构。

### ✅ 2.5 Pan law 选项 / 等功率 pan（已完成，DESIGN §5.7.29）

现在是 0 dB 定律（`left = Vol*(1-pan)`），没有 -3 / -4.5 可选。

### ✅ 2.6 Solo-safe / 独占 solo（已完成，DESIGN §5.7.30）

小改动，纯模型 + 界面。

### 🚫 2.7 Group 总线 / VCA（已放弃）

**Group 不需要做**：AUX 本来就是一条总线 —— 多条轨把 output 指过去，它有自己的推子 /
声像 / 静音 / solo / 插入，再汇进 Master。所以"子编组"今天就能用 AUX 做，
ROADMAP 原来那句"需要新的路由模型"是错的。

**VCA 做过一版，已按决定撤回**（`revert` 掉 `f57da757`）。技术上是可行的
（控制组不进音频图，只乘成员的 Volume），但维护成本与收益不成比例：
`VcaBus` 要一路穿透 Track / AuxBus / Project / 持久化 / 模型适配器 / 通道条，
而它解决的是"一个推子管几条通道"——用 AUX 编组也能达到近似效果。
**不做**，也不再排队。

### ✅ 2.8 侧链输入（已实现）

设计见 [SIDECHAIN.md](SIDECHAIN.md)，实现结论在 `DESIGN.md` §5.7.32。结论与原来的假设**相反**：

- **VST2 好做**：侧链就是"插件声明多于 2 个输入"，桥本来就能传多声道，只差把共享环
  从 2 声道加宽到 4、把第 3/4 条平面填成键。本机实测 ReaComp / ReaGate / TDR Molotok
  报 4 进，TDR Nova / ZamCompX2 / ZamGateX2 报 3 进。
- **VST3 只做实验性支持**：随包的原生宿主 `ownvst3.tlhost.dll` 没有总线查询/激活/侧链接口，
  标准 `kSidechain` 总线要 fork 上游 C++/JUCE 并重建原生库 —— 本轮不做。插件自己声明
  `>2` 个输入时顺带把第 3/4 条平面当键喂进去。

落地内容：共享环 4 声道 + 桥协议 v2（Hello 校验版本，旧桥会被拒绝）、每插入一条侧链边、
推子后/插入后取信号、单声道键 `(L+R)*0.5` 下混、PDC 对齐（侧链边不参与主混音延迟）、
FX 浮窗的 `SC` 键与源菜单、`InsertDto.SidechainId` 落盘、轨道稳定序号
（`TrackDto.MixerId`，换序/删轨不会让引用指错）。

**注意**：桥必须用 `tools/publish-bridges.ps1` 重新发布（协议 v2 与 4 声道布局）。

## 3. 明确不做（🚫）

| 项 | 理由 |
|---|---|
| **自动化（automation）** | 推子/声像/静音/独奏/send/插件参数都不做曲线。只能静态混音。工作量最大，暂缓 |
| 拖拽排序 | 插入链用 ▲▼，通道用右键菜单，已覆盖需求 |
| 多硬件输出 / 多通道设备 | v1 只有一个 Device，Cable 是禁用占位 |
| 冻结 / 离线渲染（Freeze / Bounce in place） | — |
| 环绕 / 沉浸声 | — |

## 4. 已知但不算"缺口"的设计取舍

- **AUX 兼作子编组**：把多条轨的 output 指过去，它就有自己的推子/声像/静音/solo/插入；
  但它不是"打包控制"—— solo 域仍然独立，没有 Group 的联动语义（见 §2.7）。
- **推子是 dB 刻度、线性映射**：没有推子曲线选项（DAW 里也常被关掉）。
- **只有一个 Master**：没有 monitor / cue 分离。

## 5. 收尾与后续（2026-10-28）

§2 的编号队列已走完（2.1–2.6、2.8 ✅，2.7 🚫）。下面**不是排期**，只是把评估过的
结论落在这里，免得下次重新分析。

### 5.1 发布收尾（🔜 建议先做）

- 2.8 侧链手工冒烟：VST2 为主（ReaComp / ReaGate / TDR），确认 3 进 / 4 进、单声道键
  `(L + R) * 0.5`、PDC、删轨 / 换序后引用不指错。
- 桥重新发布：协议 v2 + 4 声道布局（`tools/publish-bridges.ps1`）；旧桥会在 Hello 被拒。
- 全量 `TuneLab.sln` build + `TuneLab.Tests` / `TuneLab.Hosting.Compat.Legacy.Tests`。
- 真机回归：老工程打开 / 播放 / 导出与旧版一致；Master 插入、send、AUX、PDC。
- VST3 真插件验证：DESIGN 里仍标"开发机无 64 位 VST3"。要承诺 VST3 就必须补。

### 5.2 后续候选（未排期，按建议优先级）

| 优先 | 项 | 一句话 |
|---|---|---|
| 1 | VST3 崩溃隔离 | 把 VST3 也走桥；现在插件崩 = 应用崩。收益最高，工作量最大 |
| 2 | JSFX（M9） | 先 spike `ysfx` 的 P/Invoke、`@gfx`、许可 |
| 3 | 多设备输出 / 监听耳返 | v2 用途；每设备一个 sink + 漂移补偿 |
| 4 | 插入参数自动化 | 与正规 DAW 差距最大；要单独设计一轮 |
| 5 | VST3 标准 `kSidechain` 总线 | 要 fork vendored 原生宿主，重建 `ownvst3.tlhost.dll` |
| 6 | `daw_cable` | 现在是禁用占位 |
| — | Master Ø | 唯一还写"待定"的小项；只加相位反相 |
| — | 混音后的分轨导出 | `ExportTrack` 现在不跑混音链（见 DESIGN §3.8） |

