# 发布打包（六平台）

> 自动化在 `.github/workflows/build-packages.yml`；发版入口是 `v*` tag（`upload-release.yml`）。
> 非 tag 的推送只产出 Actions artifacts，不建 Release。

## 产物矩阵

> **版本号约定**：Release tag 用「基线版本 + 编译时间」：`v<基线版本>.<yyyyMMddHHmm>`，
> 例如 `v2.1.1.202611132043`。tag 去掉 `v` 就是本轮的完整版本号（资产名里的 `<版本>`）。
> 注意：四段大数字（`202611132043` 超过 Int32）**不是合法的 NuGet/MSBuild 版本**，
> 所以不要把 tag 版本传给 `Version` / `InformationalVersion` —— 二进制里仍报基线版本，
> tag 只决定 Release 与资产命名。非 tag 构建也用 csproj 里的基线版本命名 artifact。

| RID | 绿色版 | 安装包 |
|---|---|---|
| win-x64 | `TuneLab-win-x64-v<版本>.zip` | `TuneLab-Setup-win-x64-v<版本>.exe`（Inno Setup） |
| win-arm64 | `TuneLab-win-arm64-v<版本>.zip` | `TuneLab-Setup-win-arm64-v<版本>.exe`（Inno Setup） |
| osx-x64 | `TuneLab-osx-x64-v<版本>.tar.gz` | `TuneLab-osx-x64-v<版本>.dmg`（拖到 Applications） |
| osx-arm64 | `TuneLab-osx-arm64-v<版本>.tar.gz` | `TuneLab-osx-arm64-v<版本>.dmg` |
| linux-x64 | `TuneLab-linux-x64-v<版本>.tar.gz` | `TuneLab-linux-x64-v<版本>.deb` / `.rpm` |
| linux-arm64 | `TuneLab-linux-arm64-v<版本>.tar.gz` | `TuneLab-linux-arm64-v<版本>.deb` / `.rpm` |

另外每次发版会额外上传 `TuneLab-Setup-classic-win-x64-v<版本>.exe`：自研安装器保留，
但**自动更新只走 Inno 资产名**（服务端按 `TuneLab-Setup-{platform}-v{version}.exe` 匹配）。

## 包内容

- self-contained 发布（用户不需要装 .NET），含命令行 `TuneLab.Cli`、三座隔离桥
  （`bridge/<rid>/`）、ownvst3 与 ysfx 原生库；
- 文件关联：`.tlpx`（工程）/ `.tlx`（扩展包）——Windows 写 HKCU ProgID；macOS 走
  `CFBundleDocumentTypes`；Linux 走 MIME XML + `.desktop`；
- 快捷方式：Windows 开始菜单 + 可选桌面；macOS 由 dmg 拖入 Applications；Linux 由
  `.desktop` 提供。

## 升级 ysfx 原生库

`ysfx` 不是每次 CI 现编的：六平台的 `ysfx.tlhost.dll` / `libysfx.tlhost.so` / `.dylib`
提交在 `ExperimentalPatch/InsertHost/Native/<rid>/` 里，桥工程编译时自动拷到桥 exe
旁边（与 ownvst3 同一套机制）。

升级步骤：

1. 在 Actions 里手动跑 **Build ysfx natives**（`build-ysfx-natives.yml`）；
2. 下载六个 `ysfx-<rid>` artifact，覆盖进对应 `Native/<rid>/`；
3. 提交。包构建不需要其他改动。

## 已知边界

- **未签名**：Windows 会出 SmartScreen 提示；macOS dmg 首次打开需要右键 → 打开
  （或 `xattr -dr com.apple.quarantine`）。接证书是后续独立事项。
- **linux-arm64 的 SDL2**：NuGet 包没有 arm64 的 `libSDL2.so`，CI 从 Ubuntu 的
  `libsdl2-2.0-0` 取一份随包（放在 TuneLab 可执行文件旁边）。音频初始化失败时
  应用会退化成"无音频"而不是启动崩溃（`AudioEngine.Init` 的兜底）。
- **win-arm64 / osx-x64** 在 CI 上是交叉构建，没有对应架构的实机冒烟；
- **VST2** 只在 Windows 可用（x86 桥 + x64/arm64 桥）；macOS/Linux 没有 VST2 桥，这是
  既有设计（VST2 是 Windows 传统格式）。