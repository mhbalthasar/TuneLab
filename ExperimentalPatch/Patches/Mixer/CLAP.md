# CLAP 插入效果：设计记录

> 状态：**P-C1（扫描）与 P-C2（音频 / 参数 / 状态）已落地**。GUI（P-C3）未做，插上后显示参数面板。
> 上游约束见 [DESIGN.md](DESIGN.md) §5.2、桥的总体设计见 [VST3-BRIDGE.md](VST3-BRIDGE.md)。

## 1. 为什么是 CLAP，而且是现在

1. **它是纯 C ABI。** VST3 那条路要 vendor 一份 OwnAudioVST3 并为每个 RID 发一个 `.tlhost`
   原生库（C++ 接口没法从托管侧直接调）。CLAP 的入口是一个导出变量 `clap_entry`，里面三个
   函数指针，工厂与描述符都是 POD —— `P/Invoke` 就够，**三平台同一份代码，零额外二进制**。
2. **它是「Linux 上跑 Windows 插件」最便宜的入口。** yabridge 的代理就是一个**原生 Linux
   `.clap`**：宿主只要能跑原生 CLAP，就能加载 yabridge 包过的 Windows 插件。反过来，
   如果我们先做 VST3/VST2 的 Linux 加载，也一样能吃到 yabridge，但那两条要先解决
   "每平台一份 native 宿主库"。CLAP 先把整条链路（扫描 → 发声 → GUI）走通，代价最小。
3. **GUI 嵌入有明确答案。** `clap_plugin_gui` 支持 win32 / cocoa / **x11** 三种父窗口。
   VST3 只能让插件自己开顶层窗口（ownvst3 的托管层连 `NativeWindow` 都没带），
   VST2 的"桥建窗口再塞给插件"那套正好可以照搬到 X11。

## 2. 硬约束（与 VST3 一致的那几条）

- **插件代码只在桥里跑。** 宿主程序集（`ExperimentalPatch`）不得出现任何"加载插件"的代码：
  扫描、播放、状态、参数、GUI 全在 64 位桥里（`VstBridge/Clap*.cs`）。没有开关，没有回退。
- **扫哪 = 用户配的清单**（EP-MIXER-12）。空清单 = 什么都不扫，不擅自猜系统目录。
- **不发明插件没有的东西。** 插件报什么就显示什么；拿不到厂商就空着，不拿文件名冒充。

## 3. P-C1：扫描（已完成）

### 3.1 为什么扫描必须过桥，而且必须"加载"

VST3 的厂商/类别能从 `moduleinfo.json` 与二进制常量里读出来，所以宿主自己就能做一遍
"带目录的快扫"。CLAP 没有这种东西：`clap_plugin_descriptor` 的 `id / name / vendor /
features` 全是运行时的字符串。想知道就得 `dlopen` → `clap_entry.init(path)` →
`factory->get_plugin_descriptor(i)`。

而"加载坏插件"能把进程直接带走（VST3 那边真机复现过）。于是分工是：

| 步骤 | 谁做 | 为什么 |
|---|---|---|
| 目录遍历、`.clap` 文件枚举、去重 | **宿主** | 纯文件读取，不执行任何插件代码；"扫哪 = 用户清单"留在宿主 |
| 加载 → `init` → 枚举描述 → `deinit` → `Free` | **桥** | 执行插件代码；崩了只是桥崩 |
| 缓存 / 菜单 / 可插入性 | 宿主 | 与另外两种格式同一条数据通路 |

### 3.2 一个文件问一次（而不是一次问完）

第一版是"把整个文件清单塞进一条消息"。改成按文件分批的理由：

- **崩一个不该毁一轮。** 几十个 `.clap` 里有一个坏的，一次问完的结果是"整轮扫描失败"，
  用户重扫多少次都一样。分批后坏的只占它自己那一行（灰掉 + 原因）。
- **进度。** 桥在一条阻塞的请求里没法回报进度，分批后宿主天然能给"正在检查 xxx"。
- **卡死。** 单个文件的时限（20 s）可以远短于整轮扫描的时限（90 s）。

代价是每个文件一次管道往返 —— 与"加载一个插件"比起来可以忽略。

### 3.3 标识与定位

- `PluginId = "clap:" + descriptor.id`。`descriptor.id` 是 CLAP 规定的**反向域名唯一标识**
  （`org.surge-synth-team.surge-xt`），天生跨机器稳定 —— 比 VST3 那边"拿包名当 id"干净。
- 一个 `.clap` 可以声明多个插件（Cardinal 就有 `#fx` / `#synth`），所以定位需要两段：
  `Locator = "<文件路径>\n<pluginId>"`。分隔符用 `\n`：Windows 文件名不允许它，Linux 上
  路径里出现换行属于病态，而且解析只取第一个。
- 加载失败的占位条目：`PluginId = "clap:file:<文件名>"`（不带 `clap:` 反域名前缀，一眼看出
  不是真插件）。

### 3.4 乐器判定

`features` 里有 `instrument` **且没有** `audio-effect` 才算乐器（与 VST3 同一条口径：
宁可漏判 —— 合成器混进效果菜单，点了没用 —— 也不误判 —— 效果器从菜单消失，用户以为没扫到）。
实测：Surge XT / sforzando / Cardinal Synth 判成乐器，Surge XT Effects / Cardinal FX / 各家
效果器不是。

`Category` 存 `features` 的原样拼接（`audio-effect, reverb, stereo`），宿主这一层不解析它。

### 3.5 协议增量

全部是**加字段**，协议版本仍是 2（旧宿主不带新字段 → 落回旧语义）：

| 消息 | 新字段 | 缺省 |
|---|---|---|
| `ScanPluginsMessage` | `format`（`vst3` / `clap`）、`paths`（显式文件清单，目前只有 CLAP 用） | `vst3` / null |
| `PluginListMessage` | `format`（回显） | `vst3` |
| `ScannedPluginInfo` | `available`、`unavailableReason` | `true` / null |
| `HelloAckMessage.Formats` | 新增 `clap` 值（仅 64 位桥） | — |

宿主**核对回显的 `format`**：老桥不认识 `format` 会照着扫 VST3，那份结果如果直接当 CLAP
缓存下来就是灾难（`VstBridgeClient.LastProtocolError`）。

### 3.6 扫描结果的"可用"含义

桥加载成功 = `IsAvailable = true`，宿主不再额外加条件（P-C2 之后播放也在桥里，能加载就能插）。
加载失败的文件给一条 `IsAvailable = false` 的占位行，原因写在 `UnavailableReason` ——
比"凭空少一行"诚实。

P-C1 那一版这里曾固定为 false（"能扫不能插"）。翻成 true 时把 `CurrentCacheVersion`
加了一（4 → 5）：旧缓存里那批灰条目不会自己变亮。

## 4. 三平台事实

| 平台 | `.clap` 是什么 | 扫描 | 备注 |
|---|---|---|---|
| Windows | 单个文件（PE，扩展名故意用 `.clap`） | ✅ | `%CommonProgramFiles%\CLAP` |
| Linux | 单个文件（ELF，扩展名 `.clap`） | 代码已就位，待真机 | `/usr/lib/clap`、`/usr/local/lib/clap`、`~/.clap` |
| macOS | **目录包** `Foo.clap/Contents/MacOS/Foo` | 代码已就位，待真机 | 需要解析包内的二进制 |

32 位 CLAP **不在范围内**（`BridgeFormats()` 只让 64 位桥宣布 `clap`；x86 桥收到 CLAP
扫描会回一条"32-bit CLAP plugins are not supported"）。

## 5. 与 VST3 的差别（照抄之前先看这张表）

| 维度 | VST3 | CLAP |
|---|---|---|
| ABI | C++ 虚表 → 必须 vendor 原生宿主库 | 纯 C → `P/Invoke` 直接调 |
| 描述来源 | `moduleinfo.json` / 二进制标记 / 运行时 | **只有运行时** |
| 扫描 | 宿主做文件枚举，桥只扫标准位置 | 宿主枚举文件，桥**逐文件**加载问描述 |
| 插件 id | 包名 / 二进制名（将就） | `descriptor.id`（反向域名，天生稳定） |
| 一库多插件 | 罕见 | 常见（一个 `.clap` 多个 descriptor） |
| 编辑器 | 插件自建顶层窗口，宿主拿 HWND 置前 | `clap_plugin_gui`：可以把插件 GUI 嵌进**宿主给的**窗口 |

## 6. 阶段

### P-C1：扫描（已完成，见 §3）

### P-C2：音频 / 参数 / 状态（已完成）

桥侧 `ClapPluginHost : IBridgePluginHost`（与 `Vst3PluginHost` 同契约、同槽位表）。实现要点：

- **线程模型**：桥是单线程的（收一条消息办一件事），所以 `process` 与 `on_main_thread`
  在同一个 OS 线程上。`clap_host_thread_check` 的两个问题都答"是"（不提供这个扩展时，
  DPF 系插件会假设自己不在主线程上，把该做的事永远往后推）。谁哪天给桥加线程，
  这个前提与 `ClapLibrary` 的免锁假设要一起改。
- **宿主扩展**：只提供 `clap.thread-check`；其余一律 `get_extension → null`，由插件自己降级
  （CLAP 规范要求）。`request_restart` / `request_process` / `request_callback` 只记标记：
  restart 在下一次进槽位时做"停 → 撤 → 激活 → 开始"（失败就把这一行标成坏、直通），
  callback 在下一次进槽位时调 `on_main_thread`。
- **音频**：`clap_process` 的 `audio_inputs` / `audio_outputs` 指向**加载时一次性分配**的
  非托管缓冲（每端口一份 `float*[channels]`，每声道连续）。共享环给的平面直接拷进主输入端口；
  其余输入端口清零（CLAP 侧链端口不在 P-C2 范围内）。process 路径上不分配、不加锁。
- **事件**：入参事件只有一个来源 —— 宿主改参数。`SetParameter` 往预分配的事件数组里写一条
  `clap_event_param_value`（带 `get_info` 给的 cookie），下一次 `process` 通过 `in_events`
  交付，交付完就清空（插件没读等于丢了这条改动，比重复应用同一个值安全）。输出事件收下丢掉。
- **参数**：`clap_plugin_params` 的 `count` / `get_info` / `get_value`；`value_to_text` /
  `text_to_value` P-C2 不用（参数面板显示裸数值）。宿主停止播放时靠
  `FlushParameterChanges` 发一小块静音把事件冲进插件，避免"调完几秒后自动变回去"。
- **状态**：`clap_plugin_state` 的 `save` / `load`，用 `clap_ostream` / `clap_istream` 的反向
  回调写进 / 读出托管 `MemoryStream`（回调里 try/catch：异常绝不能跨 ABI 边界）。
- **延迟**：`clap_plugin_latency.get` → PDC；插件 `request_restart` 后重读。
- **库的生命周期**：`ClapLibrary` 按二进制路径做引用计数（`init` 每库每进程一次，
  `deinit` + 卸载要等最后一个实例）。这不只是省事 —— 没有计数的话，
  "插着 CLAP 插件时点立即扫描"会把活实例脚下的库 `deinit` 掉。

宿主侧新增 `ClapBridgeInsert`，与 `Vst3BridgeInsert` 同构（旁通 + 增益/湿干包装仍在宿主侧）。
它临时实现 `IVst3Insert`（那是"带参数面板 + 编辑器"的宿主契约，名字是 VST3 留下的。
改成与格式无关的名字是独立提交，不塞进 P-C2）。

**升级注意**：`CurrentCacheVersion` 4 → 5。P-C1 的缓存里 CLAP 条目全是
`IsAvailable = false`，不加版本号的话菜单会一直灰着。

### P-C3：GUI

- Windows：`clap_plugin_gui.create(CLAP_WINDOW_API_WIN32)` + `set_parent(宿主 HWND)` +
  `show()`。宿主的 HWND 来自现有的 `Vst2EditorHost`（桥里已有创建父窗口的那套）。
- Linux：`CLAP_WINDOW_API_X11` + `set_parent(XID)`。X11 的窗口 id 是 **X server 全局**的，
  所以"桥里嵌进宿主给的 XID"是可行的（yabridge 的 mini-WM 那套 hacks 是 Wine 特有的）。
- Wayland 没有嵌入，只能浮动窗口：在 Wayland 会话里退回"参数面板"（与现在 VST3 的
  降级一致）。
- macOS：`CLAP_WINDOW_API_COCOA`，需要 NSView 指针 —— 宿主窗口在 UI 进程，插件在桥进程，
  跨进程嵌入 NSView 不可行（Cocoa 不像 X11 有全局窗口 id）。macOS 上先只提供参数面板。

## 7. 已知限制 / 待办

- **编辑器（P-C3）未做**：插上后显示参数面板（`HasEditor = false`）。
- 真机（Linux）未验证。桥的传输层已经是跨平台的（UDS / mmap / `UnixProcess`），
  但 `tools/publish-bridges.ps1` 还没有 linux-* 目标（见 §8）。
- 扫描没有"快扫"：`ListPluginsDeep` 与 `ListPlugins` 是同一条路。等真机看耗时要不要缓存。
- `InsertInstanceFactory.FindPlugin` 对 CLAP **不**做目录回退枚举（每次编译图都要过桥加载
  几十个插件 = 每次改路由冻几秒），槽位里的 `Locator` 才是定位依据。
- 侧链（`CLAP_PORT_SIDECHAIN`）未接：多出来的输入端口一律清零。
- 参数自动化（宿主 → 插件的事件流）未接：P-C2 只有"用户拖一个参数"这一种事件。
- `value_to_text` / `text_to_value` 未用：参数面板显示裸数值，没有插件自己的单位 / 枚举名。

## 8. 发布：桥要按 RID 发

`tools/publish-bridges.ps1` 目前只发 `win-x64` / `win-x86`。CLAP 本身**不需要**额外 native，
但桥必须真的发布到目标平台，Linux 才有可执行文件可起。加 `linux-x64` / `linux-arm64` /
`osx-x64` / `osx-arm64` 时注意：VST3 的 `.tlhost` 只有 RID 匹配才可用，CLAP 不受影响。
