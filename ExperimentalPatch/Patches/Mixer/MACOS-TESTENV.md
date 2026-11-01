# macOS 隔离机制验证：个人测试环境部署方法

> 2026-10-31 方案。目标是**在 macOS 上验证 VST3 隔离桥的内核机制**（无 GUI）：
> 存活管道 / 进程组 / 共享内存音频环 / 控制通道 UDS / 残留清扫，对应
> [VST3-BRIDGE.md](VST3-BRIDGE.md) §9.10 的验证清单在 Darwin 上跑一遍。
> 不改 P1–P5.3 的 Windows / Linux 路径。

## 0. 结论先行：docker-osx / alcatraz 在 172.31.5.154 上**不可用**

实测 172.31.5.154（`linuxdebgger.pmhost`，Anolis 8.10，.NET 8.0.424）：

| 项 | 实测值 | 影响 |
|---|---|---|
| `/dev/kvm` | **不存在** | docker-osx / alcatraz / `qemu -accel kvm` 全部起不来 |
| CPU 虚拟化标志 | 无 `vmx`/`svm`（CPU 型号被遮成 `Intel Core 2 Duo T7700`） | 同上 |
| 本机自身 | **是 KVM guest**（hypervisor vendor: KVM），内层无 kvm 模块 | 嵌套虚拟化在外层被关掉，**guest 内无法自救** |
| qemu / docker / podman / libvirt | 全部未装 | 需先装，但装了也没有 KVM |

sickcodes/docker-osx 与 alcatraz 都是 QEMU+**KVM** 的封装，官方 FAQ 明说：
“attempting to run somewhere that doesn't support nested virtualization → KVM is
unavailable so Docker-OSX will not work”。所以这条路要通，只能先在外层开嵌套虚拟化（见 §6）。

**剩下的可行形态只有两种半**：纯 TCG 软件模拟的 QEMU（慢、且只能 x86_64），
GitHub Actions 托管 macOS runner（真 Darwin、含 arm64），或在外层开嵌套后的 docker-osx。

## 1. 目标与边界

- **做**：宿主 ↔ 桥两个真进程之间，存活管道、进程组、共享内存环、UDS 控制通道、
  残留清扫的端到端机制在 Darwin 内核上的行为验证；跨进程握手（含共享段映射成功）。
- **不做**：VST3 编辑器 GUI（macOS Cocoa runloop 是另一项，见 §9.1）；真插件出声
  （可选 smoke；机制验证用无插件路径即可，桥不需要挂插件也能握手 / 映射共享段）。
- **要覆盖的架构**：`osx-x64` 与 `osx-arm64`。**arm64 只能真 Mac / CI**（见 §0：QEMU
  无法启动 Apple Silicon 版 macOS，Apple 的启动链绑定 Apple SoC）。

## 2. macOS 专属风险清单（要验的就是这些）

| # | 机制 | 代码位置 | macOS 上为什么可能不一样 | 验证用例 |
|---|---|---|---|---|
| 1 | 存活管道 EOF → 看门狗自杀 | `VstBridge/BridgeProgram.cs` 看门狗；`VstBridgeClient` 的 `mLiveness`（`AnonymousPipeServerStream` + `GetClientHandleAsString()` 手递 fd） | Darwin 上 fork/exec 的 **fd 继承语义**：读端要真被桥继承、写端**绝不能**被桥继承（继承了 EOF 永不触发） | `BridgeDiesWhenTheLivenessPipeCloses` |
| 2 | 进程组 | `InsertHost/UnixProcess.cs`（`DllImport("libc")`） | `libc` → `libSystem`；`setpgid(0,0)` / `kill(-pgid,SIGKILL)` 语义 | `BridgeRunsInItsOwnProcessGroup`、`KillBridgeRemovesTheProcess` |
| 3 | 共享内存音频环 | `VstBridge/BridgeSharedMemory.cs` | Unix 用**文件承载**的 `MemoryMappedFile.CreateFromFile` + `AcquirePointer`；`Path.GetTempPath()` 在 macOS 是 `$TMPDIR`（`/var/folders/…`） | `SharedAudioRingTests`（已平台无关）+ 新增跨进程握手（桥成功 `Open` 两段） |
| 4 | 控制通道 UDS | `NamedPipeServerStream`；`InsertHost/BridgeResidue.cs` 的 `CoreFxPipe_` 路径 | socket 落在 `$TMPDIR`；`bind`/`unlink` 行为 | `BridgeCanRestartAfterBeingKilled` |
| 5 | 残留清扫 | `BridgeResidue` + `UnixProcess.IsAlive`（`kill(pid,0)`） | Darwin `kill(pid,0)` 的 `EPERM` 语义 | `ResidueSweepRemovesFilesFromDeadHosts` |
| 6 | RID / apphost | `VstBridgeClient.BridgeRid` / `FindBridgeExecutable` | 已支持 `osx-x64`/`osx-arm64`；self-contained apphost 的可执行位、Gatekeeper 隔离属性 | `FindBridgeExecutable` 单测 + 真机启动 |
| 7 | ARM64 内存序 | `BridgeSharedMemory` 的 `Volatile` + 裸指针 SPSC | 设计稿 §9.10 第 5 条一直卡在“待 ARM64 机器” | arm64 runner 跑 `SharedAudioRingTests` |

> 注意 2 与 5 的**测试侧**：`tests/TuneLab.Tests/BridgeIsolationTests.cs` 现在硬编码
> `OperatingSystem.IsLinux()` 且读 `/proc`，macOS 上整套“静默跳过”。这是**必须补的**第一处
> 代码改动（见 §4.2）。

## 3. 三条路线对比

| 路线 | 真 Darwin | 架构 | 速度 | 需要什么 | 适用 |
|---|---|---|---|---|---|
| **A. GitHub Actions macOS runner**（推荐） | ✅ | **arm64 + x64** | 分钟级 | 一个 workflow | 常规回归、arm64 内存序、CI 门禁 |
| **B. 自托管 QEMU+OpenCore（纯 TCG）** | ✅ | **仅 x86_64** | 安装 6–24h+，运行很慢 | qemu、macOS 恢复镜像、VNC 隧道 | 必须离线 / 自持的可交互盒子 |
| **C. 外层开嵌套虚拟化 + docker-osx** | ✅ | 仅 x86_64 | 快 | 改外层 hypervisor + guest 重启 | 你控制 `pmhost` 且要 KVM 级体验 |

- 路线 A 是唯一能覆盖 **arm64** 的（QEMU 无法启 Apple Silicon macOS）。
- 路线 B 与 C 都只有 x86_64；C 比 B 快得多，但门槛在外层。
- 三者都不需要 GUI：测试全在命令行里跑。

## 4. 路线 A（推荐）：GitHub Actions macOS runner

### 4.1 为什么它算“个人测试环境”

- 触发由你控：`workflow_dispatch` 手动跑，或 push 到分支跑；日志 / 产物归你。
- 已经用得上：`build-artifacts.yml` 现有矩阵里就有 `osx-arm64`（`macos-latest`），
  本方案只是把 **bridge 发布 + 隔离测试** 接进去。
- 免费额度对个人 / 公开仓库足够；`macos-15-intel` 是 GitHub 目前唯一的免费 x86_64 标签
  （macos-13 已于 2025-12 退役；`macos-14-large` 需 Team/Enterprise；`macos-15-intel`
  可用到 2027-08）。

### 4.2 需要落地的仓库改动

**（a）把 `BridgeIsolationTests` 去 `/proc` 化、放开到 macOS**

- 平台闸门：`PlatformOk => (IsLinux() || IsMacOS()) && Is64BitProcess`。
- `BridgeRid`：Linux → `linux-x64`/`linux-arm64`；macOS → `osx-x64`/`osx-arm64`。
- `ProcessGroupOf(pid)`：Linux 走 `/proc/<pid>/stat`（保留）；macOS 走 `UnixProcess` 新加的
  `getpgid(pid)` P/Invoke（`UnixProcess` 里补一个 `GetProcessGroup(int pid)`）。
- `IsGone(pid)`：改调 `UnixProcess.IsAlive(pid)`（`kill(pid,0)`），两条平台通用；
  Linux 的僵尸态可保留 `/proc` 分支（macOS 上没有 `/proc`，由宿主 `Process` 统一收割）。

**（b）新增一个跨进程握手 / 共享段测试（补上“shm + 进程间通信”这一条）**

现有用例只验进程生命周期，没验“桥真的把共享段映射上了”。新增一条：

1. `VstBridgeClient.ForBitness(64)` → `EnsureStartedBlocking()` 必须为 true ——
   这一步内部要求**桥在自己的进程里成功 `Open` 了 `.in`/`.out` 两个段**，否则桥退出、
   握手失败。握手成功即证明文件承载共享内存在 Darwin 上跨进程可用。
2. 断言段文件存在且大小 == `SharedAudioLayout.SectionBytes`。
3. `KillBridge()` 后断言段文件与 UDS socket 都消失（`BridgeResidue` 正常路径清理）。

> 这一条不加载任何插件，纯两进程 + shm + UDS，正中你的“shm + 进程间通信”要求。

**（c）workflow 骨架**（`.github/workflows/isolation-macos.yml`）

```yaml
name: Isolation (macOS)
on:
  workflow_dispatch:
  push:
    branches: [ master ]
    paths: [ "ExperimentalPatch/**", "tests/TuneLab.Tests/**", "tools/publish-bridges.ps1" ]

jobs:
  isolation:
    strategy:
      fail-fast: false
      matrix:
        include:
          - { os: macos-latest,    rid: osx-arm64 }   # Apple silicon
          - { os: macos-15-intel,  rid: osx-x64 }     # Intel（可用到 2027-08）
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 8.0.x }

      - name: Build
        run: dotnet build TuneLab.sln -c Debug

      - name: Publish the bridge into the test output
        shell: pwsh
        run: ./tools/publish-bridges.ps1 -Configuration Debug -Runtime ${{ matrix.rid }} `
               -AppOutput "tests/TuneLab.Tests/bin/Debug/net8.0"

      - name: Ensure the bridge apphost is executable
        run: chmod +x tests/TuneLab.Tests/bin/Debug/net8.0/bridge/${{ matrix.rid }}/TuneLab.ExperimentalPatch.VstBridge.x64

      - name: Isolation + ring tests
        run: dotnet test tests/TuneLab.Tests/TuneLab.Tests.csproj -c Debug --no-build `
               --filter "FullyQualifiedName~BridgeIsolationTests|FullyQualifiedName~SharedAudioRingTests"
```

**（d）本地等价跑法**（有 Mac 时就是这三步，与 Linux 的 `~/p51-run.sh` 同构）

```bash
dotnet build TuneLab.sln -c Debug
pwsh tools/publish-bridges.ps1 -Configuration Debug -Runtime osx-arm64 \
     -AppOutput tests/TuneLab.Tests/bin/Debug/net8.0
dotnet test tests/TuneLab.Tests/TuneLab.Tests.csproj -c Debug --no-build \
     --filter "FullyQualifiedName~BridgeIsolationTests|FullyQualifiedName~SharedAudioRingTests"
```

### 4.3 验收

- `osx-arm64` 与 `osx-x64` 两个 job 的 7 条用例全绿（§2 清单 1–7）。
- 产物里带 `bridge/osx-<arch>/`（`build-artifacts.yml` 已有该步骤，不重复）。
- 打上 VST3-BRIDGE.md §9.10 第 5 条（ARM64 内存序）——这是 arm64 runner 的额外收益。

## 5. 路线 B：自托管 QEMU + OpenCore（纯 TCG，仅 x86_64）

### 5.1 前提与代价

- **无 KVM → `-accel tcg`**：安装 macOS 需 6–24h+，日常运行明显卡；只当“能跑通机制”的盒子。
- 只能 x86_64；永远补不上 arm64。
- macOS 安装**需要一块屏幕**（VNC 从 Windows 通过 SSH 隧道看），装完可退成几乎纯命令行。

### 5.2 部署步骤（172.31.5.154）

```bash
# 1) 装 QEMU（Anolis 8 仓库 / EPEL）
sudo dnf install -y qemu-kvm qemu-img          # 提供 qemu-system-x86_64（TCG 也能用）

# 2) 取 OSX-KVM（kholia/OSX-KVM）：OpenCore 引导 + 脚本，注意它**不**依赖 docker
git clone https://github.com/kholia/OSX-KVM ~/OSX-KVM && cd ~/OSX-KVM

# 3) 取 macOS 恢复镜像（BaseSystem），产出 BaseSystem.img
./fetch-macOS-v2.py                            # 选一个 x86_64 可用的版本

# 4) 造盘
qemu-img create -f qcow2 ~/mac_hdd.qcow2 80G

# 5) 引导安装（TCG；无窗口 -> 仅 VNC 到 localhost，从 Windows 建隧道看画面）
#    注意：不要照抄下面这行！它是示意。真实可用命令行见 §5.4(d)，
#    关键点是 -cpu 必须带 +pclmulqdq、-smp 必须 >= 4，否则会 panic / GUI 死锁。
qemu-system-x86_64 -machine q35,accel=tcg \
  -cpu Penryn,+ssse3,+sse4.2,+popcnt,+aes,+pclmulqdq,+xsave,+xsaveopt \
  -smp 4,cores=2,sockets=1 -m 6G \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/edk2/ovmf/OVMF_CODE.cc.fd \
  -drive if=pflash,format=raw,file=~/macos-vm/OVMF_VARS_sys.fd \
  -drive id=OpenCoreBoot,if=none,snapshot=on,format=qcow2,file=~/OSX-KVM/OpenCore/OpenCore.qcow2 \
  -drive id=BaseSystem,file=BaseSystem.img,if=none,format=raw \
  -drive id=MacHDD,file=~/mac_hdd.qcow2,if=none,format=qcow2 \
  -device virtio-net-pci,netdev=net0 -netdev user,id=net0,hostfwd=tcp::2222-:22 \
  -serial file:~/macos-vm/serial.log -vnc 127.0.0.1:1 -display none
```

- Windows 侧看画面：`ssh -L 5901:127.0.0.1:5901 cafs@172.31.5.154`，VNC 连 `localhost:5901`。
- 装完后在 guest 里开 SSH（系统设置 → 通用 → 共享 → 远程登录），之后：
  `ssh -p 2222 <macuser>@127.0.0.1`（同样先建隧道）。

### 5.3 guest 内准备与跑测

```bash
# .NET 8（osx-x64 版）
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
export PATH="$HOME/.dotnet:$PATH"

# 灌入仓库（从 Windows 直接 scp 到 172.31.5.154，再从 Linux 侧转进 guest；或 guest 直连 git）
# 然后与 §4.2(d) 完全相同的三步：build → publish bridge(osx-x64) → dotnet test --filter ...
```

- 关键：`chmod +x tests/TuneLab.Tests/bin/Debug/net8.0/bridge/osx-x64/TuneLab.ExperimentalPatch.VstBridge.x64`。
  经 scp/zip 传输会丢可执行位；也不要带 Gatekeeper 的 `com.apple.quarantine` 属性。
- 只跑 `BridgeIsolationTests|SharedAudioRingTests`；真插件出声属于可选 smoke，机制验证不需要。

### 5.4 实测踩坑（TCG 引导，2026-10-31 于 172.31.5.154）

这一段是**打通过程的真实记录**，重启环境时按此复现即可。

**（a）恢复镜像不能直接用 DMG。** `fetch-macOS-v2.py` 拿到的 `BaseSystem.dmg` 是压缩
UDIF（文件尾有 `koly` 魔数），`qemu-img` 不认 dmg 格式。先 `dmg2img` 转成 raw GPT 镜像
（`~/dmg2img/dmg2img BaseSystem.dmg BaseSystem.img`，产出 ~2.1 GB），OpenCore 选择器里
才会出现 “macOS Base System”。

**（b）看不到 panic → 用串口日志，不要靠屏幕。** 键盘 `sendkey` 可用，但屏幕（`-vga std`）
在进入图形前是 800×600 文本控制台，panic 有 ~55 行会滚出屏幕；而 `screendump` 高速连拍
不可靠（QEMU 把异步 BH 合并，连续帧字节完全相同）。正解是让内核往串口吐日志：

- OpenCore 的 `boot-args` 加 `serial=1`（`-v` `keepsyms=1` `debug=0x100` 一并保留，看全回溯）。
- QEMU 命令行加 `-serial file:/home/cafs/macos-vm/serial.log`。
- 之后 panic / 启动全过程都能完整 `tail` 出来，屏幕只用来确认 GUI 是否起来。
- 改 `boot-args` 的方法：`qemu-img convert OpenCore.qcow2 → raw`，用
  `mcopy -i oc.raw@@1048576 ::/EFI/OC/config.plist` 读出、改、再写回（FAT32 ESP 在
  1 MiB 偏移处），最后 `qemu-img convert raw → qcow2` 覆盖 `OpenCore.qcow2`。

**（c）致命坑：`Penryn` CPU 缺 PCLMULQDQ → corecrypto 早期 panic。**
症状是启动 ~6–8 s 后规律 panic，帧顶在
`com.apple.kec.corecrypto : _gcm_init + 0x8e`，`Kernel trap … type 6=invalid opcode`，
回溯里 `fipspost_post_aes_gcm → ccgcm_one_shot → ccmode_gcm_init → gcm_init`。
根因：AES-GCM 的 `_gcm_init` 用 **PCLMULQDQ（carry-less multiply）**，这是 **Westmere**
才有的指令；`Penryn`（Core 2）虽然 SSE4.1 齐全，但**不宣告** `pclmulqdq`，QEMU 便不
模拟它 → 非法指令。修法是把它显式打开：

```
-cpu Penryn,+ssse3,+sse4.2,+popcnt,+aes,+pclmulqdq,+xsave,+xsaveopt
```

加上后串口日志立刻出现
`FIPSPOST_USER … PASSED: fipspost_post_aes_gcm`，panic 消失。**这条是纯 TCG 跑 macOS 的
必踩项，务必保留 `+pclmulqdq`。**

**（d）可用引导命令行（`~/macos-vm/boot_sys.sh` 的等价物）：**

```bash
/usr/libexec/qemu-kvm -accel tcg,thread=multi -m 6144 -smp 1 -machine q35 \
  -cpu Penryn,+ssse3,+sse4.2,+popcnt,+aes,+pclmulqdq,+xsave,+xsaveopt \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/edk2/ovmf/OVMF_CODE.cc.fd \
  -drive if=pflash,format=raw,file=~/macos-vm/OVMF_VARS_sys.fd \
  -device qemu-xhci -device usb-kbd -device usb-tablet \
  -device ich9-ahci,id=sata \
  -drive id=OpenCoreBoot,if=none,snapshot=on,format=qcow2,file=~/OSX-KVM/OpenCore/OpenCore.qcow2 \
  -device ide-hd,bus=sata.2,drive=OpenCoreBoot \
  -drive id=InstallMedia,if=none,file=~/OSX-KVM/BaseSystem.img,format=raw \
  -device ide-hd,bus=sata.3,drive=InstallMedia \
  -drive id=MacHDD,if=none,file=~/macos-vm/mac_hdd.qcow2,format=qcow2 \
  -device ide-hd,bus=sata.4,drive=MacHDD \
  -netdev user,id=net0,hostfwd=tcp::2222-:22 -device virtio-net-pci,netdev=net0 \
  -monitor unix:~/macos-vm/monitor.sock,server,nowait \
  -serial file:~/macos-vm/serial.log -vga std -display none -vnc 0.0.0.0:1
```

要点：用**系统** OVMF（`/usr/share/edk2/ovmf/OVMF_CODE.cc.fd` + `OVMF_VARS.fd` 的一份拷贝），
不要用 OSX-KVM 自带的 OVMF（自带的会卡在固件死循环）；OpenCore 盘开 `snapshot=on` 避免
写回污染；本机 qemu-kvm 缺 `isa-applesmc`，由 OpenCore 的 VirtualSMC 顶替。

**（e）OpenCore 选择器**：entry 0 = `EFI`（默认，进 UEFI Shell），entry 1 =
`macOS Base System`。用 monitor `sendkey right` + `sendkey ret` 选第 1 项。varstore
（`OVMF_VARS_sys.fd`）被写坏导致选择器消失时，从 `/usr/share/edk2/ovmf/OVMF_VARS.fd`
重新拷一份即可复位。

**（f）致命坑 2：`-smp 1` 会让 GUI 死锁在语言选择界面。** 单核 guest 能一路 boot 到
`recoveryosd: Aqua session established` 并 `Launching "…/Language Chooser.app/…"`，
但之后就**卡死**：屏幕停在灰底+光标 30 分钟不动、串口不再输出、QEMU 稳定跑满 1 核，
`info registers` 显示 RIP 在 **CPL=3**（用户态死循环）。改成 OSX-KVM 的默认值
**`-smp 4,cores=2,sockets=1`** 后（`thread=multi` 会给每个 vCPU 一个宿主线程，宿主有
6 vCPU 时真正并行），QEMU 立刻升到 ~230% CPU，Apple logo + 进度条正常画出来，
随后完整渲染出「macOS 工具」恢复窗口。**这条与 `+pclmulqdq` 一样是必踩项。**

> 旁证：本机 QEMU 6.2 的 TCG **不支持 AVX**（`-cpu …,+avx` 会警告
> `TCG doesn't support requested feature: …avx`），所以只能靠加核提速，不能再加指令集。

**（g）TCG 时间线（6 vCPU 宿主，guest 4 线程 6 GB）**：从选中启动项到「macOS 工具」窗口
约 **18–20 分钟**；串口依次出现 `recoveryosd`、`Aqua session established`、
`Launching "…/launchprogresswindow"`（画 Apple logo+进度条）、
`Launching "…/Language Chooser.app/…"`，最后 `recoveryos_agent: Running Aqua specific tasks.`
并落在恢复工具窗口。分辨率在 GUI 起来时从 800×600 切到 1024×768。串口里
`recoveryosd: … failed to mount imgsrc device, no device info` 是**无害**的，不影响 GUI。

**（h）GUI 部分必须人肉操作。** 恢复工具窗口里的 Disk Utility → 抹掉 `mac_hdd` →
Reinstall macOS Big Sur，都要鼠标，`sendkey` 驱动不了；这段交给用户通过 VNC 做
（见 §5.2 隧道命令）。串口（`-serial file:`）保留着，恢复环境里看不到的系统级信息仍可从
`~/macos-vm/serial.log` 读。

**（i）NVRAM 会被写坏。** 反复 `pkill -9` QEMU 可能让 `OVMF_VARS_sys.fd` 的启动项丢失，
后果是 OVMF 直接掉进自带 UEFI Shell（串口里看到 `BdsDxe: loading Boot0008 "EFI Internal
Shell"`）。稳妥做法是**每次启动前**把 pristine varstore 拷回去：
`cp -f /usr/share/edk2/ovmf/OVMF_VARS.fd ~/macos-vm/OVMF_VARS_sys.fd`。

## 6. 路线 C：外层开嵌套虚拟化后上 docker-osx（可选，需改 `pmhost`）

只有在你控制外层 hypervisor 时可行，且**只带来 x86_64**：

1. 外层（Proxmox / KVM 宿主）：
   ```bash
   echo 1 | sudo tee /sys/module/kvm_intel/parameters/nested
   # 并把该 guest 的 CPU 类型设为 host / host-passthrough（否则 vmx 仍被遮）
   sudo modprobe -r kvm_intel && sudo modprobe kvm_intel nested=1   # 需要时
   ```
2. 重启 172.31.5.154，确认 `/dev/kvm` 存在、`/proc/cpuinfo` 里出现 `vmx`。
3. 装 docker + `docker run --device /dev/kvm … sickcodes/docker-osx:auto`（或 alcatraz）。
   注意本机只有 15G 内存、`/home` 有 373G：内存偏紧，给 macOS 的 `-m` 别超过 ~10G。
4. 之后与 §5.3 相同。

> 若外层无法改，路线 C 直接排除，回到 A（推荐）或 B。

## 7. 与 VST3-BRIDGE.md §9 的衔接

- 本方案就是 §9.11 里 **P5.4（macOS，存活管道版，无 PDEATHSIG）** 的测试环境部分。
- 落地后建议在 §9.10 表格旁补一列“macOS（osx-arm64 / osx-x64）”，并更新 §9.11 的 P5.4 状态。
- 仍未做、且本方案**不覆盖**的：macOS 编辑器 GUI 泵（§9.1 明确另立一项）、
  A2 `prctl`（Linux-only，与 macOS 无关）、Unix gate（eventfd/自管道，可选优化）。
