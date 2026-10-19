# 多轨混音台（Mixer）— 设计稿

> 状态：**设计稿，待拍板**，代码尚未开始。§7 列了需要你决定的取舍。
> 参考对象：Adobe Audition 的多轨混音台 —— 参考的是**信息层级与排布**，不是照抄像素。

## 0. 一句话

一个独立的调音台窗口：每轨一条通道条（竖推子、pan、M/S、双通道电平表、dB 读数），右侧一条 Master。
**不新增任何 DSP** —— 它是对**已有**混音状态的一个新视图，写回的也是同一批 `Track` 数据属性。

## 1. 事实基础

| 事实 | 出处 |
|---|---|
| 混音单位是 **Track**（扁平一维，顺序 = 显示序 = 混音序）；part 挂在 track 上、时间上可重叠（重叠即相加） | `TuneLab/Data/Project.cs`、`Data/Track.cs`、`Data/Part.cs` |
| 每轨已有的混音状态：`Gain`(dB, 0=unity) / `Pan`(−1..1) / `IsMute` / `IsSolo` / `Color` / `Name`；**已序列化、已可撤销、已暴露给脚本** | `Data/ITrack.cs:18-25`、`Data/Track.cs:19-27`、SDK `TrackInfo`、`Scripting/ScriptHandles.cs:830-938` |
| **混音本体就是 `AudioGraph`**：`AddData` 逐轨 dB→线性 + 线性 pan 后累加；`MixData` 做 mute/solo 门控 | `Audio/AudioGraph.cs:48-110` |
| 每轨电平：播放中由 `AudioEngine.ProgressChanged` 驱动，取 64 sample 窗峰值转 dB（左右各一）；**被静音或未 solo 时返回 null** | `Audio/AudioEngine.cs:208-240`，消费方 `UI/.../TrackHead.cs:463-474` |
| 轨道头已有 gain 滑杆（−24..+6，到底 = −∞）、pan 滑杆、M/S、电平表 | `UI/.../TrackHead.cs:44-55` |
| Master 音量 `AudioEngine.MasterGain`（dB），**只在播放回调里应用，导出路径不用它** | `Audio/AudioEngine.cs:460-493` |
| 菜单装配点（File / Edit / Scripts / Help） | `UI/MainWindow/Editor/Editor.cs:1687-1812` |
| 宿主 `internal`：`Track` / `IProject` / `AudioGraph` / `StereoAmplitudeViewer` / `GainSlider` / `PanSlider` —— **补丁全都碰不到** | `ExperimentalPatch/.agents/AGENTS.md` §2 硬规矩 |

**没有的东西**（决定了 v1 的边界）：

- 没有 bus / send / 分组 / 轨道级插入效果（效果只挂 `MidiPart`）。
- 没有 Master 的电平查询（只有逐轨的）。
- 没有片段级 pan/mute/solo；`AudioPart` 没有任何电平控制。
- `AsRefer` 只是个被序列化的标志，音频链里**没有任何消费方**。
- 没有 limiter / 削波保护：求和可以超过 ±1.0。

**两个上游自身的不一致**（本补丁不改，见 §3.3）：

1. pan 律是线性的：`L=v(1−p)`、`R=v(1+p)` → 硬 pan 时一侧放大到 2×（+6 dB），而不是另一侧衰减；居中是 0 dB 而非 −3 dB。
2. `ExportTrack` 走 `AddData`（**忽略** mute/solo），`ExportMaster` 走 `MixData`（**遵循**）——导单轨与导总输出的语义不一致。

## 2. 为什么值得做

`TrackHead` 已经有 gain/pan/M/S/电平，所以要说清楚增量在哪：

1. **一览与对比**：轨道头是竖排一列、每个只有 12px 高的横滑杆，且随内容区垂直滚动。调平衡要的是"所有轨并排、相对关系一眼可见"。
2. **精度**：12px 滑杆调不出精细电平；调音台给全长推子 + dB 刻度 + 数值读数 + 精细拖动 + 双击复位。
3. **Master 段**：如今 `MasterGain` 没有像样的 UI，总输出这条链在界面上是隐形的。
4. **将来的位置**：insert / send / 分组迟早要做，届时有地方放。

## 3. 范围

### 3.1 v1（本次要做）

- 独立窗口，每轨一条通道条 + 右侧 Master 条，横向滚动，可调高度。
- 通道条内容：色块 + 轨名 + 轨号、pan、M/S、竖推子 + dB 刻度、双通道电平表、dB 数值读数（可编辑）。
- Master 条：推子（写 `MasterGain`）+ dB 读数 + 双通道电平表。
- 双向同步：控件的每次改动写回 `Track.Gain/Pan/IsMute/IsSolo`，编辑器里的改动也实时反映到调音台。
- 选中同步：编辑器里选中的轨在调音台高亮。
- 轨道增删/改名/改色/换序实时反映。
- 窗口几何记忆；可选"总在最前"。

### 3.2 以后（v2+，各自独立评估）

- 片段级子通道（展开一条轨看到它的各 part；`MidiPart.Gain` 已存在，`AudioPart` 需要先补一个电平字段）。
- 轨道级插入效果槽（**需要先有轨道级效果**，宿主里不存在）。
- send / 分组 / bus（宿主里完全不存在，属于音频链改造）。
- Master 电平表前置条件见 §6。
- 电平表的 peak-hold / clip 指示（需要更长的观测窗口，见 §4.5）。

### 3.3 明确不做（避免画出假控件）

- 不做 input/output 路由下拉、insert 槽、send 旋钮 —— 宿主没有这些概念，画出来就是骗人。
- 不改 pan 律、不加 limiter、不改导出语义 —— 那些会改变**已有工程的听感**，不属于补丁该做的范畴。发现的问题按 §1 记下来，将来单独提上游。

## 4. 界面设计

### 4.1 总排布

一条横排：轨道数那么多条通道条 + 末尾一条 Master（Master 比普通条宽、且**不随水平滚动**、钉在右侧）。

```
┌───────────────────────────────────────────────────────────────────────────────┐
│ 混音台                                            [置顶] [全部复位] [≡]        │ ← 自绘标题栏，可拖
├─────────┬─────────┬─────────┬─────────┬───────────────────────────────────────┤
│ ① 主唱   │ ② 和声  │ ③ 伴奏  │ ④ 音频  │              MASTER                   │
│ [色块]   │ [色块]  │ [色块]  │ [色块]  │  ┌──────────┐   ┌──────┐  ┌──────┐    │
│ ●(pan)   │ ●       │ ●       │ ●       │  │          │   │ ▍▍   │  │ ▍▍   │    │
│ [M][S]   │ [M][S]  │ [M][S]  │ [M][S]  │  │   推子   │   │ L    │  │ R    │    │
│ ┌─────┐  │  ...    │  ...    │  ...    │  │          │   └──────┘  └──────┘    │
│ │推子 │▍▍│         │         │         │  │          │                        │
│ │     │▍▍│         │         │         │  └──────────┘                        │
│ └─────┘  │         │         │         │   0.0 dB   [M]                       │
│ -3.0 dB  │         │         │         │                                       │
└─────────┴─────────┴─────────┴─────────┴───────────────────────────────────────┘
  ↑ 可视区横向滚动（轨道多时）                ↑ Master 固定在这边
```

- 宽度 = 通道数 × 76 + Master 124 + 边距；**超出则水平滚动**（与 Audition 一致：轨道多了要能滚）。
- 高度可调（默认 560，最小 380）；通道条内部按比例伸缩，推子占到最大份额。
- 每条的宽度固定 76，让"推子行程"在轨多时也不缩水。

### 4.2 通道条（自上而下）

| 段 | 内容 | 数据 |
|---|---|---|
| A 头 | 轨号（与轨道头序号一致，便于对照）+ 色块条 | `Track.Color`（空则取主题默认色） |
| B 名 | 轨名（v1 **只读**；改名已有入口，这里不重复造） | `Track.Name` |
| C pan | 旋钮：拖拽旋转，双击回中，右键菜单（居中/极左/极右） | `Track.Pan` |
| D M/S | 两个方形开关，配色沿用轨道头语义（M 青、S 橙） | `Track.IsMute` / `IsSolo` |
| E 推子 | 竖推子 + 左侧 dB 刻度（+6 / 0 / −12 / −24 / −∞），0 dB 处一条加粗刻度 | `Track.Gain` |
| F 电平 | 双通道电平表（L/R），贴在推子右侧 | 见 §4.5 |
| G 读数 | dB 数值，可双击编辑；双击空处回 0 dB | `Track.Gain` |

**为什么 pan 用旋钮**：Audition 是旋钮，且通道条是**窄而高**的，水平滑杆在 76px 宽度里行程太短；旋钮在小面积里手感更好，也一眼能看出"居中/偏离"。

**为什么推子要竖的**：这是调音台存在的理由 —— 竖直方向的长行程才能做精细电平，横向排布才能一眼比出相对高低。

### 4.3 Master 条

- 推子写 `AudioEngine.MasterGain`（dB，范围同轨道：−24..+6；`MasterGain` 是 `double`，不是 DataProperty → **不进 undo、不序列化**，这点要在 UI 上如实处理：不做"复位"以外的承诺）。
- 双通道电平表见 §4.5 / §6（需要一个新的宿主查询）。
- Master 不显示 solo；`M`（静音总输出）v1 **不做** —— `MasterGain` 只能"调小"，做出静音按钮等于引入一个宿主没有的状态。

### 4.4 推子与刻度

- 范围 −24..+6 dB，与轨道头一致（`TrackHead.cs:44`），保证同一工程两处读数一致。
- **刻度映射**：dB → 位置的映射用分段线性（+6..0 占 40% 行程，0..−12 占 35%，−12..−24 占 25%），
  这样常用区间（0 dB 附近）行程更长 —— 与真实调音台的刻度一致，也让"−3 dB 和 +1 dB"这种操作做得出来。
- 最低处是 **−∞ 档**：位置到底 = `Track.Gain.Value <= -24` → 显示 `−∞`。
  与轨道头的既有约定一致（`TrackHead.cs:45` 的 `value <= MinValue ? -Infinity : value`），**不引入第二套语义**。
- 交互：拖动、滚轮（1 dB/格）、Shift+滚轮（0.1 dB）、双击回 0 dB、右键菜单（0 dB / −∞ / 复制值到同组？—— v1 只留前两项）。

### 4.5 电平表

- 数据源：**复用**每轨现有的 64 sample 窗峰值（`AudioEngine.InvokeRealtimeAmplitude`）。
- 关键行为（沿用上游既有语义，不另造）：
  - **只在播放时更新**；停止时表归零。
  - 被静音、或在有 solo 且本轨未 solo 时，查询返回 null → **表变黑**。这符合"你听到的就是你看到的"。
- 每帧把新峰值与当前显示值取 max 后缓慢回落（audition 那类"快起慢落"），但**不做 peak-hold 长条与 clip 保持**：
  那需要跨帧累积状态与一个"超限"标志，而宿主只给单帧峰值；v1 先不做，避免表说谎。
- 刻度：+6 / 0 / −6 / −12 / −24 / −48 dB，0 dB 以上一段用警示色（沿用主题里 S 的橙）—— 但**不叫"削波"**，
  因为求和本来就可能超 1.0 且没有 limiter，这里是"电平偏高"提示，不是 clip 判定。

### 4.6 交互与同步

- 写回一律经**已有的** `Track` 数据属性 → 自动获得撤销、序列化、脚本/编辑器双向同步。
- **拖拽必须只产生一步 undo**（实现时必须先确认 `DataProperty` 的提交粒度：轨道头的滑杆绑的是同一个属性，
  照它的提交方式办；若它是"每次写入一步"，则在拖动期间抑制提交、松手提交一次）。
- 编辑器里选中/取消选中轨道 → 调音台对应条高亮（`Track.IsSelected`，瞬态状态、不进 undo）。
- 轨道增删/换序/改名/改色 → 调音台实时重建或增量更新。
- 关闭窗口 = 隐藏，不销毁；下次打开恢复位置与高度（写进补丁自己的配置文件，落在 `IHostServices.DataFolder` 下）。
- 没有工程打开时：窗口照常打开，显示空面板 + 一句提示（不做"禁用菜单项"那种额外状态）。

### 4.7 视觉

- 调色板**镜像**宿主主题（现在 `SplashTheme` 里已经有一份），见 §5.6 的合并计划。
- 通道条背景用 `DARK`，条与条之间 1px `LINE` 分隔；底槽 `BACK`；推子槽 `ITEM`；推子帽 `LIGHT_WHITE`；
  高亮/选中用 `HIGH_LIGHT`。与轨道头同色系，放在一起不像外来窗口。
- 字体：11/12px，与轨道头一致；dB 读数用等宽（避免数字跳动时抖）。

## 5. 架构

### 5.1 单一真相源

```
   ┌──────────────── 唯一真相源 ────────────────┐
   │ Track.Gain / Pan / IsMute / IsSolo / Color │  ← 已有：已序列化、已可撤销、脚本可见
   └───────▲───────────────────────────┬────────┘
           │ 写回（改属性）             │ 读 + 订阅 Modified
   ┌───────┴─────────┐      ┌──────────▼──────────┐
   │ 轨道头/编辑器/脚本 │      │ 调音台窗口（本补丁） │
   └─────────────────┘      └─────────────────────┘
```

纪律：**调音台不持有任何独立状态**。不缓存 gain、不自己算总和、关闭时不"写回"。
任何"第二份真相"（把推子位置存在补丁里）都会在编辑器改动时立刻不同步。

### 5.2 补丁契约

契约里**不出现宿主类型**（只有 `string`/`double`/`bool`/事件）——补丁层唯一的存在理由就是这条。

```csharp
// ExperimentalPatch/Abstractions/Mixer/IMixerModel.cs
public interface IMixerModel
{
    IReadOnlyList<IMixerChannel> Channels { get; }   // 按工程顺序
    IMixerChannel Master { get; }                    // 主输出
    event Action? ChannelsChanged;                   // 增删 / 换序 / 换工程
    event Action? MetersUpdated;                     // 播放中每帧一次
}

public interface IMixerChannel
{
    string Name { get; }
    string ColorHex { get; }        // 空 = 主题默认色
    bool IsSelected { get; }

    double GainDb { get; set; }     // 写回 Track.Gain
    double Pan { get; set; }        // 写回 Track.Pan
    bool IsMute { get; set; }
    bool IsSolo { get; set; }

    (double LeftDb, double RightDb)? Peak { get; }   // null = 无信号 / 被静音 / 未 solo

    event Action? StateChanged;     // 别处改了这条轨 → 刷新该条
}
```

- **不发明 id**：按序给出通道对象，轨增删换序时宿主重建列表并发 `ChannelsChanged`，调音台整表重建。
  轨数在人类尺度上很小，重建比增量 diff 更不容易错。
- **Master 也是 `IMixerChannel`**（`IsMute`/`IsSolo` 恒 false、`Name` 恒 "Master"），
  这样条的渲染只有一条代码路径。
- **电平用"一个事件 + 拉取"**：`MetersUpdated` 每帧发一次，调音台收到后遍历所有条读 `Peak` 一次。
  比每通道各发一个事件少几十次派发，且天然同帧一致。
- **mute/solo 的"事实"在哪**：`IsMute` setter 写 `Track.IsMute`；真正决定听得见听不见的是
  `AudioGraph.MixData` 的 solo 判据——两者同源（适配器直接读写 `Track` 属性），不需要补丁去猜。

### 5.3 宿主适配器住在哪（**纪律问题，需要你拍板**）

把宿主 `internal` 的 `Track` / `Project` 翻成上面的契约，**必须有宿主侧代码**。而 `AGENTS.md` §5
现在写的是"宿主已有文件只允许改那六处"。所以要么改纪律，要么把适配器硬塞进 `ExperimentalPatchBootstrap.cs`。三个选项：

| 选项 | 做法 | 评价 |
|---|---|---|
| **A（推荐）** | 纪律加一条：允许**新增** `TuneLab/ExperimentalPatch*.cs` 适配器文件（一个功能一个） | 上游不可能有同名文件 → **零冲突**；缝文件保持薄；一眼能看出哪些文件属于补丁层 |
| B | 全塞进 `ExperimentalPatchBootstrap.cs` | 违背"缝要尽量只增不改、逻辑写在补丁里"的精神，那个文件会迅速膨胀 |
| C | 走脚本子系统当数据通道 | 绕远、要跑 JS、还要给脚本补 API，得不偿失 |

选项 A 的代价只是：`ExperimentalPatch/` 里会有个文件住在宿主目录下（命名前缀已标明归属）。

### 5.4 入口（怎么打开调音台）

需要一个命令入口。可选的落点：

1. **顶部菜单新增一项**（`Editor.CreateMenu()` 里加一个 `混音台`，约 8 行）—— 推荐。
2. 加到已有的 `File` / `Edit` 菜单里 —— 语义不合（它既不是文件操作也不是编辑操作）。
3. 侧边栏加一页 —— 需要动 `SideBarTab` 枚举 + provider 注册，比加一个菜单项**更侵入**。

顺带这会引出一个**通用**能力：宿主从补丁收集"实验功能"命令并渲染成一个菜单。
以后每个补丁功能都能复用这一个接缝，不用各加一处菜单代码。建议把它做成"一个顶层菜单 + 若干命令"，
而不是"为混音台加一个硬编码菜单项"。

快捷键：v1 不做（需要接到宿主的 action 注册表，属另一个接缝）；菜单项够用。

### 5.5 生命周期与刷新

- 窗口在 `app.ready` 之后按需创建（第一次点菜单时），之后关闭只是隐藏。
- `ChannelsChanged` ← 宿主订阅 `Project` 的轨道增删/换序；`StateChanged` ← 订阅 `Track` 各字段的 `Modified`。
- `MetersUpdated` ← 宿主订阅 `AudioEngine.ProgressChanged`（与轨道头同一条数据源）。
- 无工程时：`Channels` 为空 + `Master` 仍可用（`MasterGain` 是引擎级的）。

### 5.6 补丁侧要自己实现的控件

宿主那套 `GainSlider` / `PanSlider` / `StereoAmplitudeViewer` 都是 `TuneLab.GUI` 的 `internal`，
补丁用不了（这正是 `SplashTheme` 存在的理由）。所以要在补丁里实现：

| 控件 | 说明 |
|---|---|
| `Fader` | 竖推子：分段线性刻度（§4.4）、拖动/滚轮/双击复位、−∞ 档 |
| `PanKnob` | 旋钮：拖拽旋转、双击回中 |
| `StereoMeter` | 双通道、快起慢落、只在播放中有值 |
| `LevelReadout` | dB 数值，可编辑、等宽字体 |

**顺带做一件清理**：把 `SplashTheme` 提升为补丁层共用的 `ExperimentalPatch/UI/Palette.cs`
（闪屏与混音台共用一份宿主主题镜像），避免第二份调色板副本各漂各的。

## 6. 需要的宿主改动清单

| # | 位置 | 改动 | 为什么 | 冲突风险 |
|---|---|---|---|---|
| 1 | `ExperimentalPatch/Abstractions/` + `IAnchorTable` + `AnchorHub` + `PatchHost` | 新增贡献型锚点 `app.mixer`（`ProvideMixerModel(Func<IMixerModel>)` / `CreateMixerModel()`） | 宿主向补丁提供混音数据，是两条线唯一的接缝 | 低（补丁层自己的文件） |
| 2 | `TuneLab/ExperimentalPatchBootstrap.cs` | 加一个薄转发 `CreateMixerModel()`（同 `CreateStartupScreen` 的写法） | 宿主唯一入口保持统一 | 低（缝文件内新增，不改已有行） |
| 3 | `TuneLab/ExperimentalPatchMixer.cs`（**新文件**） | 适配器：`IMixerModel`/`IMixerChannel` 的宿主实现，读写 `Project.Tracks` / `Track.*` / `AudioEngine` | 宿主 `internal` 类型只有宿主侧能碰 | **零**（上游没有这个文件） |
| 4 | `TuneLab/UI/MainWindow/Editor/Editor.cs` `CreateMenu()` | 加一个顶层菜单 + 从补丁收集命令（约 8~15 行） | 需要用户能打开调音台 | 低（上游偶尔改菜单，冲突好解） |
| 5 | `TuneLab/Audio/AudioEngine.cs`（**可选**） | 加一个 Master 电平查询（约 12 行，复用现有 `InvokeRealtimeAmplitude` 的写法） | 宿主缺"主输出电平"这个能力，Master 表没法算 | 低但**非零** —— 这是唯一一处往上游已有文件里加代码 |

第 5 项可以不做（v1 的 Master 条就只给推子和读数、不给表）。做了，Master 才是完整的调音台。

## 7. 需要你拍板的取舍

**决策记录（2026-10-19，均已拍板，按推荐执行）**：通道单位 = **轨**；形态 = **独立工具窗口**；
**加** Master 电平查询（接受一处上游已有文件的代码新增）；适配器 = **允许新增 `TuneLab/ExperimentalPatch*.cs`**；
入口 = **通用「实验功能」菜单**（命令从补丁收集）。Q6/Q7/Q8 亦按推荐：推子范围沿用 −24..+6 与 −∞ 档、
静音/未 solo 时电平表变黑、两个上游不一致只记文档不上报（要提 issue 另说）。

| # | 问题 | 我的推荐 |
|---|---|---|
| Q1 | 通道条的单位：**轨** 还是 片段/part？ | **轨**。`AudioGraph` 的混音单位就是轨；片段子通道留 v2（`MidiPart.Gain` 已有，`AudioPart` 还缺电平字段） |
| Q2 | 形态：**独立工具窗口** 还是 侧边栏页？ | **独立窗口**。侧边栏要动 `SideBarTab` 枚举 + provider，更侵入；且调音台与编排区并列看才顺 |
| Q3 | v1 要不要 Master 电平表（= 上面第 5 项）？ | **要**。不做的话主输出这条链在界面上仍是隐形的 |
| Q4 | 宿主适配器住哪：A/B/C（§5.3）？ | **A**：允许新增 `TuneLab/ExperimentalPatch*.cs`，上游零冲突 |
| Q5 | 入口：新建"实验功能"菜单（通用命令收集）还是硬编码一项？ | **通用命令收集**。一次投入，以后每个补丁功能都省一处宿主改动 |
| Q6 | 推子范围沿用轨道头的 −24..+6 dB、到底= −∞？ | **沿用**。同一工程里两处读数必须一致 |
| Q7 | 静音/未 solo 时电平表变黑（沿用上游语义），还是做成 pre-mute 常亮？ | **沿用**。做成 pre-mute 要给宿主新增查询，且"听不到却还在跳"更容易误导 |
| Q8 | 把 §1 那两个上游不一致（线性 pan 律、`ExportTrack` 忽略 mute/solo）**记进文档**就够，还是顺便提 upstream issue？ | **先记文档**。它们会改变已有工程听感/导出结果，不该由补丁悄悄改 |

## 8. 里程碑（每步都能单独跑起来看）

| 里程碑 | 内容 | 验收 |
|---|---|---|
| M1 | 抽出共用 `Palette`；定义契约；加 `app.mixer` 锚点；写宿主适配器 | 用一个 headless harness 打印出所有轨的 gain/pan/mute/solo/选中 |
| M2 | 窗口骨架 + 通道条（色块/名/号/pan/M/S/推子/读数），双向写回 | 调音台拖推子 → 编辑器跟着变，反过来也成立；**一次拖动 = 一步 undo** |
| M3 | 电平表（逐轨）+ Master 条 | 播放时表随声跳，静音时变黑；停止归零 |
| M4 | 菜单入口 + 窗口几何记忆 + 轨道增删实时刷新 | 菜单能开关窗口；加一条轨，调音台立刻多一条 |
| M5 | 打磨：刻度细分、双击复位、右键菜单、置顶、无工程空态、文案中英 | 手感与轨道头一致 |

