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

    /// <summary>扫一遍并更新缓存。慢（秒级）——只该由"立即扫描"这种显式动作触发。</summary>
    IReadOnlyList<InsertPluginEntry> Scan();

    /// <summary>读缓存（打开插件菜单用这个，不现扫）。</summary>
    IReadOnlyList<InsertPluginEntry> CachedPlugins { get; }

    // 上次扫描时间（本地时间）；null = 还没扫过。
    string? LastScanText { get; }
}

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

    // false = 这个插件当前不可用（典型：32 位插件而我们没有起桥）。
    // 界面上仍然列出来（用户能看到"装了但用不了"），但禁用 + 给原因。
    public bool IsAvailable { get; init; } = true;

    public string? UnavailableReason { get; init; }

    // 后端私有的定位信息（文件路径）。宿主扫描时填，创建插入时传给后端。
    public string Locator { get; init; } = string.Empty;
}
