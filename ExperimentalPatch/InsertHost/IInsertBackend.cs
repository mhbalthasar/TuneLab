using System;
using TuneLab.ExperimentalPatch.MixerEngine;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 一个"插入效果后端"：负责把某个格式、某个 id 的插件变成一个 IMixInsert。
//
// 【为什么是两层】IMixInsert 是**音频线程**的契约（Process 在那里跑，不能分配/加锁/抛异常）。
// 而一个 VST3 插件的加载、读状态、写参数、开界面，全都在 UI 线程、全都会分配、全都会失败。
// 直接把这两件事塞进一个类 = 音频线程上跑 UI 代码，那是实时毛刺和崩溃的正路。
//
// 所以这里拆成"创建期"（后端，UI 线程，可以失败、可以分配）与"运行期"（实例，音频线程）。
// 一个后端可以创建出多个实例（同一个插件插在两条轨上是很常见的用法）。
public interface IInsertBackend
{
    // 显示用的格式名（`VST3`）。菜单与浮窗都读它。
    string FormatName { get; }

    // 这个后端现在能不能创建实例（原生库缺失 / 平台不对 → false，界面上禁用）。
    bool IsAvailable { get; }

    // 不可用时给一句原因，直接显示在菜单里（"原生 VST3 宿主缺失" / "仅 Windows"）。
    string? UnavailableReason { get; }

    // 本格式下当前可用的插件（扫描结果）。
    //
    // directories 非空 = 只扫这些目录（用户在设置里配的清单）。
    // 为空 / null = 扫后端自己的默认位置。
    // 慢，但只在打开菜单 / 扫描时调；宿主负责缓存（见 InsertPluginCatalog）。
    //
    // onDirectory：**每进入一个目录之前**回调一次（参数 = 本次调用内的序号、本次的目录数、目录本身）。
    // 只为给"立即扫描"画进度条：跨格式的累计由宿主自己算（它知道两个格式各有多少条）。
    //
    // probeInstruments：要不要判断"这个插件是不是乐器"（扫描时 = true，「添加效果」菜单据此藏掉乐器）。
    // 怎么判由各后端自己定：VST2 过桥加载一次（VST2 没有静态元数据，问插件自己是唯一可靠的办法）；
    // VST3 读二进制里的类别标记，**不加载**（进程内加载会把宿主带走，见 Vst3Backend 的注释）。
    // 纯粹为了找 Locator 的调用方（例如按 pluginId 反查路径）传 false，别为它白跑一遍。
    //
    // onDetail：探测过程中的"正在检查 xxx"（参数是插件显示名）。不参与进度条，
    // 只为了让界面在"一个大目录里逐个过插件"的那几秒里看得出还活着。
    IReadOnlyList<InsertPluginInfo> ListPlugins(IReadOnlyList<string>? directories = null,
        Action<int, int, string>? onDirectory = null, bool probeInstruments = true, Action<string>? onDetail = null);

    // 完整扫描：把每个插件的**描述**问一遍（厂商 / 版本 / 类别 / 声道数），比 ListPlugins 慢一个量级。
    //
    // 为什么需要它：ListPlugins 是"快扫"—— VST3 那边只读二进制的类别标记，
    // 厂商多半是空的（信息不在二进制里，见 Vst3Backend.ListFromDirectories 的注释）。
    // 完整扫描让插件的 factory 报一遍自己的元数据，是官方口径。
    //
    // 【安全边界】实现必须走"问描述"，不能"实例化插件"。VST3 那边实例化会 abort 进程
    // （实测：Blue Cat 的 Chorus/Flanger/Phaser 两两先后 load，3/3 必崩），
    // 而问描述不会。详见 Patches/Mixer/NOTES-vst3-vendor.md。
    //
    // 默认实现 = 返回空（表示"这个后端没有完整扫描"），宿主据此提示用户。
    IReadOnlyList<InsertPluginInfo> ListPluginsDeep(IProgress<float>? progress = null, Action<string>? onDetail = null)
        => [];

    // 创建一个挂上图的实例。失败返回 null（并把原因写进 reason）——绝不是抛异常，
    // 调用方（宿主）据此把 UI 上的那个槽位标红 + 旁通（DESIGN.md §5.6）。
    IMixInsert? Create(InsertPluginInfo plugin, InsertSettings settings, out string? reason);
}

// 一个可选的插件（菜单里列出来的那一行）。
public sealed class InsertPluginInfo
{
    public required string Format { get; init; }

    // 插件自己的稳定标识：VST3 = classId，VST2 = uniqueId，JSFX = 工程内相对路径。
    // 落盘靠它把"上次挂的是哪个插件"找回来（DESIGN.md §9.4 的 pluginId）。
    public required string PluginId { get; init; }

    public required string Name { get; init; }
    public string Vendor { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;

    // 乐器（VST3 Instrument / VST2 effFlagsIsSynth）。见 InsertPluginEntry.IsInstrument：
    // 混合台的插入槽只收效果器，扫描时探到乐器就标这里，菜单据此过滤。
    public bool IsInstrument { get; init; }

    // 后端私有的定位信息：VST3 的 bundle 路径（LoadPlugin 要的）。
    // 不进落盘载荷：工程可能换机器，路径会变；按 PluginId 重新扫。
    public string Locator { get; init; } = string.Empty;
}

// 创建插入时要的东西。都是"装配参数"，不是音频参数。
public sealed class InsertSettings
{
    // false = 旁通（音频直通）。槽位保留。
    public bool Bypass { get; init; }

    // 宿主给的采样率 / 最大块长。插件 Initialize 要。
    public double SampleRate { get; init; }

    public int MaxBlockSize { get; init; }

    // [VST3-BRIDGE] 把 VST3 放进桥进程（隔离崩溃 / 挂起）。默认 false = 进程内。
    //
    // 这是**本机设置**（宿主从 insert-plugins.json 读进来）：默认关，P3 才会在
    // Windows x64 上默认打开。打开后若 x64 桥不可用，Vst3Backend 回退进程内
    // 并把原因记进 IsolationFallbackReason（宿主据此记日志 / 标示"未隔离"）。
    public bool IsolateVst3 { get; init; }
}
