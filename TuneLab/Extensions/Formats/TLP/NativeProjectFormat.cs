using System.Collections.Generic;
using System.IO;
using TuneLab.SDK;

namespace TuneLab.Extensions.Formats.TLP;

// 宿主内部的 native .tlp 附带元数据（editor/export）。这些字段是 app 私有状态，不属通用 musical 交换契约（ProjectInfo），
// 故不经 SDK 公共面携带；仅 native 格式（TuneLabProject / TuneLabProjectCbor）在宿主内部路径保真持久化。
internal class EditorInfo
{
    public double PlayheadPos { get; set; } = 0;
    // 参数面板布局（自上而下）。null = 文件里没有这一项（旧工程、.tlp）→ 打开得到默认单面板。
    public List<ParameterPanelInfo>? ParameterPanels { get; set; }
    public int FocusedParameterPanel { get; set; } = 0;
}

// 一块参数面板的持久形态：想要的高度（逻辑像素）、收起、激活参数、可见参数、点亮的回显轨。
internal class ParameterPanelInfo
{
    public double Height { get; set; }
    public bool Collapsed { get; set; }
    // null = 默认（Volume）。
    public ParameterRefInfo? Active { get; set; }
    public List<ParameterRefInfo> Visible { get; set; } = new();
    public List<ParameterRefInfo> VisibleSynthesized { get; set; } = new();
}

// 面板里"哪一条参数"的持久形态。运行时的路由键带的是 effect 的链内下标，不能直接存：布局是整个工程共用的、
// 跨 part 的，故 effect 参数另记实例 id 与类型，打开时先按 id、再按「位置 + 类型」认回（见 ParameterPanelLayoutStore）。
internal class ParameterRefInfo
{
    public string Source { get; set; } = string.Empty;   // voice | effect | noteLane | phonemeLane
    public string Id { get; set; } = string.Empty;
    // 仅 effect 来源：实例 id、链内位置、类型。
    public string? EffectId { get; set; }
    public int EffectIndex { get; set; } = -1;
    public string? EffectType { get; set; }
}

internal class ExportConfigInfo
{
    public string ExportPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Format { get; set; } = "wav"; // wav | mp3 | flac | ogg
    public int SampleRate { get; set; } = 44100;
    public int BitDepth { get; set; } = 16;   // 无损格式(wav/flac)位深
    public int Bitrate { get; set; } = 320;   // 有损格式(mp3/ogg)目标码率 kbps
    public bool MasterExportEnabled { get; set; } = true;
    public int MasterExportChannels { get; set; } = 2;
    // [EP-MIXER-33] 分轨导出是否带轨道效果器。默认 false：老工程没有这个键时保持原来的纯音源导出。
    public bool IncludeEffects { get; set; } = false;
}

// native 工程文件的宿主内部完整载荷：组合纯 musical 的 ProjectInfo + 两段宿主私有元数据（editor/export）。
// 只在宿主内部路径（open/save 编排、native 序列化器）流转，不经 SDK 公共面。
internal sealed class NativeProjectFile
{
    public ProjectInfo Project { get; set; } = new();
    public EditorInfo Editor { get; set; } = new();
    public ExportConfigInfo Export { get; set; } = new();

    // [EP-MIXER-11] 补丁层的混音路由载荷（紧凑 JSON）。
    // 放在这里而不是 SDK 的 ProjectInfo 里：它不进冻结的插件 ABI；而且原版读取器对不认识的
    // 顶层键走 SkipValue，所以原版照样能打开带这个键的工程（DESIGN.md §9）。
    public string? EpMixer { get; set; }

    // [EP-MIXER-32] 音频文件的基准目录（崩溃恢复用）。自动保存写在 %APPDATA% 下，相对音频引用
    // （..Insts\...）的默认基准是自动保存文件所在目录；这里记下原工程目录，
    // 恢复时若默认位置找不到文件，再按这个目录试一次。
    // 老文件没有这个键 = null，行为不变。
    public string? BaseHref { get; set; }
}

// native 工程格式的宿主内部契约：在通用 musical(ProjectInfo) 之外，额外进出宿主私有的 editor/export 元数据（打包进 NativeProjectFile）。
// 只有 native(.tlp/.tlpx) 序列化器实现；通用 IImport/ExportFormat 仍是纯 musical。
internal interface INativeProjectFormat
{
    NativeProjectFile DeserializeNative(Stream input);
    void SerializeNative(Stream output, NativeProjectFile file);
}
