# Vendored — OwnAudioVST3 的降级副本

## 这是什么

[ModernMube/OwnAudioVST3](https://github.com/ModernMube/OwnAudioVST3) 的**托管层**（`OwnVST3Host/`），
从上游 `main`（提交 `fbd478cba62c0f377294b5ed1be94983b394d6a3`）复制并降到 **net8.0**。
我们随包分发它的 `OwnAudioVst` NuGet 1.7.0 里的原生 dll（见 `Native/`）。

上游许可是 MIT，`LICENSE.txt` 一并带在 `Native/` 旁边。

## 为什么整份搬进来，而不是装 NuGet 包

`OwnAudioVst` 的托管层只发布 **net10.0**。TuneLab 是 net8.0，装不上去。
宿主持有的 TFM 不能为一个效果器插件升上去（那会拖累整个应用的部署面）。

原生库不受影响：`ownvst3.dll` 是纯 C ABI，跟 .NET 版本无关。

## 降级时改了什么

| 改动 | 位置 | 为什么 |
|---|---|---|
| `<TargetFramework>net10.0</TargetFramework>` → `net8.0` | 我们的 `InsertHost.csproj`（不在本目录） | 降级的本体 |
| 原生库名加 `.tlhost` 后缀 | `OwnVST3.cs` 的 `WindowsLibraryName` | 见下 |

**没有别的改动。** 上游的 C# 代码在 net8 下原样编译通过（无兼容性修补）。

## `.tlhost` 后缀（重要）

原生库以 `ownvst3.tlhost.dll` 的名字分发，而不是上游的 `ownvst3.dll`。

原因（DESIGN.md §5.4 的"ALC + 改名"方案）：将来可能有别的扩展也带一份
VST3 宿主。两个原生库同名同时加载，Windows 按**文件名**解析 —— 第二份会
被静默忽略，用第一份的代码。结果是"两个插件忽然行为一样"这种极难排查的问题。

加一个后缀就让它们各自一份。代价：托管层里所有按原名加载的地方都要改
（`WindowsLibraryName` 常量），以及它的 DllImportResolver 要认得新名字
（`Vst3Backend` 里装的 resolver）。

`.so` / `.dylib` **不**加后缀：Linux 的 soname、macOS 的 install name 都与
文件名强绑定，改了反而更难被加载器找到。TuneLab 目前只在 Windows 上分发
这个桥，所以这条不影响任何人。

## 升级步骤（上游发了新版怎么做）

1. `git clone` 上游，看 `OwnVST3Host/` 的 diff。
2. 把 `*.cs` 覆盖到本目录，然后：
   - 确认 `OwnVST3.cs` 的 `WindowsLibraryName` **还是** `ownvst3.tlhost.dll`。
   - 确认原生 dll 的导出表没变（`VST3Plugin_Create` / `_ProcessAudio` / `_Destroy` 等）。
     上游 `OwnVST3Juce/src/ownvst3.def` 就是导出清单，对着看。
3. 换 `Native/win-x64/ownvst3.tlhost.dll`（从新版 NuGet 包里取，**改名**后再放）。
4. 跑 `tests/TuneLab.Tests` 的 `InsertChainTests` —— 它不用真插件也能验证
   "原生库缺失时优雅降级"这条路径。
5. 真实插件验证：随便找一个 64 位 VST3，`VstNet8Probe` 式的冒烟（加载 → Initialize →
   处理一块 → 存取 state）。**机器上没有 64 位 VST3 时这一步做不了**，只能靠第 4 步。

## 已知的边界

- 上游的**编辑器窗口**（`NativeWindow/`、`VstEditorController`）没有 vendor 进来：
  我们只要音频通路。将来要开插件的原生设置窗，再把那一份按同样方式降级。
- 上游的插件扫描器（`PluginDiscovery.cs`）带的 native 侧较新；我们的 1.7.0 有它。
  换原生 dll 时若扫描器 API 变了，`Vst3Backend.ListPlugins` 的 try/catch 会兜住
  （扫不出来只当"没有插件"，不让菜单挂掉）。
- 32 位 VST3 插件由 `VstBridge`（x86 独立进程）负责，**不走**这份托管层。
