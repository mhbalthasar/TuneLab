namespace TuneLab.ExperimentalPatch.Abstractions;

// 插入效果器的插件扫描设置（宿主侧实现在 TuneLab/ExperimentalPatchInsertSettings.cs）。
//
// 【为什么走契约而不是让补丁直接引用宿主】补丁层能看到宿主的全部就是 Abstractions。
// 直接 using 宿主的 internal 类 = 补丁被迫引用宿主程序集 = 这一层存在的意义归零
// （宿主一重构，补丁立刻编译失败，而那是本层最不该有的耦合）。
//
// 【返回什么】全是 BCL 与这里的类型。路径清单是"这台机器装了什么"的本机设置，
// 必须与工程内容分开（工程换机器打开时路径本来就会失效）。
public interface IInsertPluginSettings
{
    // 当前各格式的扫描路径。键 = 格式名（"VST3" / "VST2"），值与 InsertFormats 对应。
    IReadOnlyDictionary<string, IReadOnlyList<string>> Paths { get; }

    // 改一份路径清单。空列表 = 清空这份格式的路径。
    void SetPaths(string format, IReadOnlyList<string> paths);

    /// <summary>
    /// 扫一遍并更新缓存。慢（秒级）——只该由"立即扫描"这种显式动作触发，
    /// 而且**别在 UI 线程上直接调**：调用方把它扔到后台线程，用 progress 把进度画出来。
    /// </summary>
    IReadOnlyList<InsertPluginEntry> Scan(IProgress<InsertScanProgress>? progress = null);

    /// <summary>
    /// 完整扫描：多花一个量级的时间，把每个插件的厂商/版本问一遍（VST3 的正主口径）。
    /// </summary>
    /// <remarks>
    /// 普通扫描对 VST3 只读二进制里的类别标记，厂商多半是空的 —— 信息不在二进制里。
    /// 完整扫描走"问描述"（不是实例化插件），安全但慢，所以在设置页是独立的按钮，
    /// 由用户显式触发。默认实现 = 当作普通扫描（后端没有完整扫描能力时退回去）。
    /// </remarks>
    IReadOnlyList<InsertPluginEntry> ScanDeep(IProgress<InsertScanProgress>? progress = null)
        => Scan(progress);

    /// <summary>读缓存（打开插件菜单用这个，不现扫）。</summary>
    IReadOnlyList<InsertPluginEntry> CachedPlugins { get; }

    // 上次扫描时间（本地时间）；null = 还没扫过。
    string? LastScanText { get; }
}

// 扫描进度。宿主**按目录**推进（一个目录报一次），界面据此画进度条。
//
// 【为什么按目录而不是按文件】文件是"几百上千"的量级：每扫到一个就跨界回调一次，既压满 UI 线程，
// 进度条也只会疯抖 —— 人看不出"走到哪儿了"。目录是"几条到几十条"，刚好够用。
// DoneDirectories 是**已经走完**的数量，Directory 是**正在扫**的那条（两者不同步是故意的：
// 前者画进度条，后者让用户看见卡在哪个目录上）。
//
// Detail 是**目录内**正在做的那一步（当前是"正在检查哪个插件"）：扫描为了判断类型要逐个加载插件，
// 一个大目录里能停十几秒，只写目录名会让人以为界面死了。它不参与进度条 ——
// 分式仍按目录算，Detail 只是那行文字的尾巴。
public sealed record InsertScanProgress(int DoneDirectories, int TotalDirectories, string Directory, string? Detail = null);

// 一个被扫到的插件。宿主用它喂混音台的"添加效果"菜单。
public sealed class InsertPluginEntry
{
    // 格式名（"VST3" / "VST2"）。与 IMixerInsertFormat.Name 对应。
    public required string Format { get; init; }

    // 稳定标识（VST3 = classId，VST2 = uniqueId）。落盘靠它，路径会变、它不变。
    public required string PluginId { get; init; }

    public required string Name { get; init; }

    public string Vendor { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    // 乐器（VST3 Instrument / VST2 effFlagsIsSynth）。
    //
    // 混音台的插入槽里只该出现效果器：乐器插在轨道上不会去听 MIDI，只会把
    // 一整块静音/噪声塞进总线。所以「添加效果」菜单按它过滤掉乐器。
    // 扫描时探到才算数（探不动就留 false —— 宁可列出来，也别让能用的插件消失）。
    public bool IsInstrument { get; init; }

    // false = 这个插件当前不可用（典型：32 位插件而我们没有起桥）。
    // 界面上仍然列出来（用户能看到"装了但用不了"），但禁用 + 给原因。
    public bool IsAvailable { get; init; } = true;

    public string? UnavailableReason { get; init; }

    // 后端私有的定位信息（文件路径）。宿主扫描时填，创建插入时传给后端。
    public string Locator { get; init; } = string.Empty;
}
