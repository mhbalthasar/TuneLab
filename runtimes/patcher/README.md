# OpenGL 兜底（Mesa3D 软件渲染）

插件界面里有一类（DPF / NanoVG，例如 ZamDynamicEQ、Dragonfly 系列）必须走 **OpenGL 1.3+**。
Windows 自带的软件渲染器只到 **1.1**，所以在虚拟机 / 远程桌面上它们的编辑器会画成白色块，
日志里是：

```
[dpf] assertion failure: "glActiveTexture != nullptr"
[dpf] assertion failure: Failed to create NanoVG context, expect a black screen
```

把一份 **Mesa3D（llvmpipe）** 构建的 `opengl32.dll` 放到本目录对应的位数子目录里，
TuneLab 会在加载任何插件**之前**把它装进进程，插件的 OpenGL 调用就会走到软件渲染。

```
runtimes/patcher/
  x86/opengl32.dll      # 32 位宿主 / 32 位 VST2 桥
  x64/opengl32.dll      # 64 位宿主 / 64 位 VST2 桥
  arm64/opengl32.dll    # Windows on ARM
```

要点：

- **硬件优先**：启动时先看显示适配器。真显卡（NVIDIA / AMD / Intel）直接用硬件 OpenGL，**不会**装这份软件实现；只有虚拟机 / 远程桌面 / 基础显示驱动这类没有硬件 GL 的机器才会用。
- **有文件才生效**。机器没有硬件 GL 又没放这份 dll 时，插件界面仍会是白窗（日志里会有 GL 警告）。
- 位数必须与**进程**一致：32 位 VST2 插件跑在 x86 桥里，所以 x86 目录也要放一份。
- 目录按 `RuntimeInformation.ProcessArchitecture` 选，不是按插件位数选。
- **渲染后端也是自动选的**：有硬件 D3D12 就设 `GALLIUM_DRIVER=d3d12`（OpenGL 走 DX，GPU 加速）；没有才用 `llvmpipe`（纯 CPU）。
- Mesa 的 `opengl32.dll` 通常还需要同目录下的 `libgallium_wgl.dll` / `dxil.dll` 等伴生文件，
  一起放进来即可（同一个目录会被加进 DLL 搜索路径）。

实现见 `ExperimentalPatch/VstBridge/OpenGlPatcher.cs`：`SetDllDirectory(本目录)` +
预加载 `opengl32.dll`（Windows 的模块按文件名唯一，先加载的那份就是全进程用的那份）。


