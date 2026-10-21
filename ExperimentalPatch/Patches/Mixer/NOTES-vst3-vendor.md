# VST3 厂商：官方接口与"延迟扫描"可行性

## 结论先说

**有官方 API，而且是两个，都已经能用** —— 只是都要求"插件在进程内被 load 过"。
上游源码（OwnAudioVST3，即我们 vendored 的那个 native 库）写得很清楚：

| 入口 | 什么时候有厂商 | 代价 |
|---|---|---|
| `PluginInstance::getVendor()` → `VST3Plugin_GetVendor` | **load 之后**：`_plugin->fillInPluginDescription(desc)` 取 `desc.manufacturerName` | 要把插件 load 进进程 |
| `PluginScanner`（Fast 模式）→ `PluginDescriptorC.vendor` | **只对 AudioUnit 有**：AU 的厂商在系统注册表里，读文件即可 | VST3 走 `_describeVst3Bundle()`，那个函数只填 name / path / format，**不填 vendor** |
| `PluginScanner`（Full 模式） | **有**：每个 bundle 真 load 一遍，`_addDescription(desc)` 里取 `desc.manufacturerName` | 上游注释原话："loads every plugin — minutes" |
| `OwnPlugin_ResolveDescriptor(id)` | **有**：单个插件 load 一次并回填缓存条目 | 一次 load 的代价 |

VST3 的 `fillInPluginDescription` 的厂商来自 `IPluginFactory::getFactoryInfo()`（VST3 SDK 的
`PFactoryInfo.vendor`）—— 那是**官方 API**，只是必须先拿到 factory，而 factory 在
`LoadPlugin` 里才创建。所以"不 load 就拿厂商"在 VST3 里没有合法路径，这不是我们的取舍，
是 SDK 的结构。

---

## 实测：load 一个插件到底安不安全（2026-10-21）

拿这台机器上 66 个 bundle 做了逐load实验（只 `LoadPlugin`，不 `Initialize`），结论分两半。

### 好消息

- **能读到厂商**：Zam 全系 → `Damien Zammit`，BC 全系 → `Blue Cat Audio`，
  Cardinal 系 → `DISTRHO`，Voxengo 系 → `Voxengo`，chowdsp 系 → `chowdsp`。
  与二进制嗅探的结果一致，但这是 `fillInPluginDescription` 的正主口径。
- **单个插件 load 很快**：11 ms ~ 250 ms（Cardinal 约 590 ms，它内部要加载 100+ 模块）。
- **同一个插件连续 load 两次没事**：说明静态状态可以重复初始化。
- **不同厂商穿插 load 没事**：Chow → Voxengo → Zam → Blue Cat(EQ) 一路下来全过。

### 坏消息：一个确定的崩溃

**条件**：先后 load `BC Chorus 4 VST3(Mono)` 和 `BC Flanger 3 VST3(Mono)`（或 Phaser Mono）。
**现象**：测试宿主进程直接死亡，**没有任何 .NET 异常** —— 是 native abort。
**复现率**：3/3 次全崩，完全确定。

已知的边界（都实测过）：

| 组合 | 结果 |
|---|---|
| Chorus(Mono) 单独 ×2 | 过 |
| Flanger(Mono) 单独 ×2 | 过 |
| Chorus(Mono) → Flanger(Mono) | **崩** |
| Chorus(Mono) → Phaser(Mono) | **崩** |
| Chorus(Mono) → ChowPhaserMono | 过 |
| Chorus(Mono) → BC Triple EQ(Mono) | 过 |
| 逐个目录跑（01~06） | 全过 |
| 逐个目录跑（07 / 09） | **崩** |

也就是说：**崩的是 Blue Cat 的调制类三兄弟（Chorus/Flanger/Phaser 共用一套 JUCE 代码）之间的
两两组合**，跨厂商、跨 Blue Cat 内部不同产品线都没事。这正好解释了 `LooksLikeInstrument`
注释里那条"某个 DPF 插件 assert 直接 abort"—— 当时是混在 66 个里一起 load，无从定位。

native 库的 `loadPlugin` 有 `__try/__except(EXCEPTION_EXECUTE_HANDLER)` 包着
（注释说"catches access violations from misbehaving plugin DllMains"），**但抓不到这个** ——
能抓到的只有访问违例，抓不到 abort / 纯虚调用 / 堆损坏。而且 abort 常发生在析构或静态初始化里，
SEH 也覆盖不到。

### 由此得出的设计约束

1. **不能在 UI 线程 / 音频线程 load**（本来就确定）；
2. **不能在"扫描时全 load"**（分钟级 + 上面这个崩溃面）；
3. **"点开菜单时后台补一个"也不安全** —— 用户完全可能先点开一个装过 Chorus 的机器，
   再点开装过 Flanger 的机器，组合出来就崩。而且崩的是**整个宿主进程**，不是补数据失败。

**结论：`createPluginInstance` 这条路走不通。** 除非把 load 搬进独立进程
（VST2 已经有桥了，VST3 没有）—— 那是另一个量级的工程，不值得为"菜单分组好看一点"做。

---

## 但是有一条安全的路：native 的 Full scan（实测通过）

上面那个崩溃是我们自己反复调 `VST3Plugin_LoadPlugin` 触发的。native 的
`PluginScanner` **不走这条路** —— 源码里两处调用完全不同：

| | 调用 | 做什么 | 实测 |
|---|---|---|---|
| `PluginScanner::_fullScan` | `fmt->findAllTypesForFile(found, id)` | **只取描述**，不实例化 | **过** |
| `PluginInstance::loadPluginBody` | `_formatManager.createPluginInstance(...)` | **真实例化** | **崩** |

差别就在 `findAllTypesForFile` 不创建插件实例 —— 它只问 factory 要一份
`PluginDescription`（厂商正在其中：`desc.manufacturerName`），不碰插件的任何代码路径。
所以 Blue Cat 调制类那套会在实例化时炸的东西，在描述模式下根本不会执行到。

**实测确认**：对这台机器跑了一次 Full scan（`OwnPlugin_ScanStartMode(mask, SCAN_FULL)`
→ `PluginScanner.ScanAsync(PluginFormat.Vst3, ScanMode.Full)`），
**41 个条目全部拿到厂商，进程活着**，其中就包含上面那三个会崩的：

```
Blue Cat's Chorus 4 (Mono)     Blue Cat Audio
Blue Cat's Flanger 3 (Mono)    Blue Cat Audio
Blue Cat's Phaser 3 (Mono)     Blue Cat Audio
```

而且顺带解决了另外两件事：

- **Vendor 是官方口径**：`desc.manufacturerName`，与 VST2 的 `effGetVendorString` 同源，
  不再是二进制嗅探的猜测；
- **顺带拿到 version / category / 声道数**：`PluginDescriptor` 里全有，
  现在这些字段在我们的扫描结果里都是空的。

### 代价与形态

Full scan 会 load 每个插件**的描述**（不是实例），上游注释标的是 "minutes"。
这台机器上 41 个跑完在分钟级以内。所以它必须是：

- **设置页里一个独立的"完整扫描（较慢）"按钮**，用户显式触发；
- **不是**默认扫描路径（默认仍是现在的 Fast + 二进制嗅探，秒级）；
- 跑完把 vendor 写进 `insert-plugins.json`，之后默认扫描读缓存就能用。

这样"零风险零成本兜底"（二进制嗅探）和"完整准确"（Full scan）都有，
用户自己选要不要花那几分钟。

---

## 落地时的形态（三选一，按性价比）

| 方案 | 做法 | 评价 |
|---|---|---|
| **A. 完整扫描按钮** | 设置页加按钮 → `PluginScanner.ScanAsync(ScanMode.Full)` → vendor/version/category 写进缓存 | **推荐**。官方口径、顺带拿全元数据、实测安全。代价是分钟级，但用户 opt-in |
| B. 现在的二进制嗅探 | 已有，58/107 | 保留作默认路径的兜底 |
| C. 点开菜单时 load 补一个 | `ResolveDescriptor` | **否**。实测会崩，且崩的是宿主进程 |

### A 已落地（2026-10-21）

- `IInsertBackend.ListPluginsDeep()`（默认实现返回空 = 这个后端没有完整扫描）；
- `Vst3Backend.ListPluginsDeep()` 走 `PluginScanner.ScanAsync(ScanMode.Full)`，
  带 `IProgress<float>` + 当前条目名；
- `InsertPluginSettings.ScanDeep()`（`Abstractions.IInsertPluginSettings` 上的新成员，
  默认实现 = 当普通扫描），内部与 `Scan()` 共用 `ScanCore(deep, progress)`；
- 设置页「完整扫描」按钮，两个按钮扫描期间互斥（`mScanning` + 各自 IsEnabled）。

**实测**（这台机器）：89 个条目，**88 个有厂商**（普通扫描是 58/107）。
VST3 37 个全有；VST2 52 个里 51 个（本来就有，走桥问的）。
进程活着，会崩的那三个（Blue Cat Chorus/Flanger/Phaser Mono）全部安全通过。

缓存版本升到 4 —— 完整扫描写进来的 VST3 厂商与旧格式不同源，
旧缓存继续用只会让界面说"扫过了"还是没厂商。

### A 要动哪里

| 文件 | 改动 |
|---|---|
| `TuneLab/ExperimentalPatchInsertSettings.cs` | `Scan` 加一个 `deep` 参数；`CachedPlugin` 补 `Version` / `Category` / `InputChannels` / `OutputChannels`；`CacheVersion` 升到 4 |
| `ExperimentalPatch/InsertHost/Vst3Backend.cs` | 加 `ListPluginsDeep()`：走 `PluginScanner.ScanAsync(ScanMode.Full)`，把 `PluginDescriptor` 全字段搬进 `InsertPluginInfo` |
| `ExperimentalPatch/Patches/Mixer/InsertPluginPathsPage.cs` | 加"完整扫描（较慢）"按钮，跑的时候显示 native 的进度（`PluginScanner.Progress` / `CurrentItem`） |
| `ExperimentalPatch/Patches/Mixer/FxPopupWindow.cs` | 不用改 —— vendor 现在就有，菜单自动按它分组 |
