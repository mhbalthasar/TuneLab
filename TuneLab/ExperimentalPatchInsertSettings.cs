using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.InsertHost;
using HostLog = TuneLab.Foundation.Log;

namespace TuneLab;

// 插入效果器的插件目录清单 + 扫描缓存（EP-MIXER-12）。
//
// 【为什么单独一个文件】它是补丁功能的数据层，宿主不该知道"VST 路径清单"存在。
// 放这里（`TuneLab/ExperimentalPatch*.cs`）合并冲突恒为零。
//
// 【存在哪】`{DataFolder}/insert-plugins.json`，受 TUNELAB_DATA_DIR 重定向
// （与 mixer-window.json 同一套规则，见 DESIGN.md §6.4）。
// 为什么**不进 .tlpx**：它是"这台机器上插件装在哪儿"的本机设置，
// 跟工程内容无关。把本机路径塞进工程 = 换机器打开就报错。
//
// 【契约在哪】它实现 Abstractions/IInsertPluginSettings。补丁只看见那个接口，
// 看不到这个类——依赖方向单向，宿主一重构补丁也不会编译失败。
internal sealed class InsertPluginSettings : IInsertPluginSettings
{
    public const string FileName = "insert-plugins.json";

    public static string SettingsPath => Path.Combine(DataFolder(), FileName);

    // 默认就扫这两个位置：VST3 的标准安装目录。用户自己加更多。
    public static IReadOnlyList<string> DefaultVst3Paths
    {
        get
        {
            var list = new List<string>();

            // 然后再加 VST3 的标准安装位置（Windows）。用户自己加的路从这里进来。
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files", "VST3"));
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Common Files", "VST3"));

            return list;
        }
    }

    public static IReadOnlyList<string> DefaultVst2Paths => OperatingSystem.IsWindows()
        ? new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VSTPlugins"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VSTPlugins"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST2"),
        }
        : Array.Empty<string>();

    static string DataFolder()
    {
        string? overrideFolder = Environment.GetEnvironmentVariable("TUNELAB_DATA_DIR");
        if (!string.IsNullOrEmpty(overrideFolder))
            return overrideFolder;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TuneLab");
    }

    // ── 契约面 ──

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Paths
    {
        get
        {
            var state = Load();
            return new Dictionary<string, IReadOnlyList<string>>
            {
                ["VST3"] = state.Vst3Paths ?? DefaultVst3Paths.ToList(),
                ["VST2"] = state.Vst2Paths ?? DefaultVst2Paths.ToList(),
            };
        }
    }

    public void SetPaths(string format, IReadOnlyList<string> paths)
    {
        var state = Load();

        if (format == "VST3")
            state.Vst3Paths = paths.ToList();
        else if (format == "VST2")
            state.Vst2Paths = paths.ToList();
        else
            return;

        Save(state);
    }

    public IReadOnlyList<InsertPluginEntry> CachedPlugins
    {
        get
        {
            var state = Load();

            // 旧缓存不可信：CacheVersion 之前的版本根本不记录"是不是乐器"，
            // 于是合成器会以 IsInstrument = false 混在列表里。宁可当空表、让界面提示
            // "去扫描一次"，也不要把一列合成器重新摆回 FX 菜单。
            if (state.CacheVersion < CurrentCacheVersion)
                return [];

            // 写缓存时已经不存乐器了；这里再滤一遍，兜住手工改过 / 旧格式的缓存。
            return Dedupe(state.Cache.Where(cached => !cached.IsInstrument).Select(ToEntry).ToList());
        }
    }

    public string? LastScanText
    {
        get
        {
            // 本进程刚扫过 → 用内存里那份；否则从落盘的缓存时间还原。
            // 早先只读内存：重启后明明有缓存，界面上却写"尚未扫描"。
            if (mLastScanText != null)
                return mLastScanText;

            var state = Load();

            // 旧格式的缓存不算"扫描过"：那批条目里分不出乐器，界面上说"扫过了"会误导。
            if (state.CacheVersion < CurrentCacheVersion || string.IsNullOrEmpty(state.LastScanUtc))
                return null;

            return DateTimeOffset.TryParse(state.LastScanUtc, out var scanned)
                ? scanned.ToLocalTime().ToString("G")
                : state.LastScanUtc;
        }
    }

    string? mLastScanText;

    public IReadOnlyList<InsertPluginEntry> Scan(IProgress<InsertScanProgress>? progress = null)
    {
        var state = Load();

        var entries = new List<InsertPluginEntry>();

        // 扫哪 = 用户配的清单（EP-MIXER-12）。空清单 = 什么都不扫，不擅自去扫系统目录。
        var vst3Paths = state.Vst3Paths ?? [];
        var vst2Paths = state.Vst2Paths ?? [];

        // 进度条跨两个格式累计：两个后端各自只会报"我在自己这份清单里走到第几条"，
        // 全局的偏移量只有这里知道。
        int total = Math.Max(vst3Paths.Count + vst2Paths.Count, 1);

        // 目录内的"正在检查 xxx"要知道当前是哪个目录、全局走到第几条 —— 后端报细节时只给插件名，
        // 这两个值由下面两个 lambda 边扫边更新。
        string currentDirectory = string.Empty;
        int currentDone = 0;

        entries.AddRange(sVst3Backend.ListPlugins(vst3Paths, (index, _, directory) =>
            {
                currentDirectory = directory;
                currentDone = index;
                progress?.Report(new InsertScanProgress(index, total, directory));
            },
            onDetail: name => progress?.Report(new InsertScanProgress(currentDone, total, currentDirectory, name)))
            .Select(p => new InsertPluginEntry
            {
                Format = p.Format,
                PluginId = p.PluginId,
                Name = p.Name,
                Vendor = p.Vendor,
                Category = p.Category,
                IsInstrument = p.IsInstrument,
                Locator = p.Locator,
            }));

        // VST2 一律过桥（Vst2Backend / VstBridge），x86 与 x64 都支持；
        // 桥的可执行文件缺失时 Create 会给原因，扫描这里只负责"列出来"。
        entries.AddRange(sVst2Backend.ListPlugins(vst2Paths, (index, _, directory) =>
            {
                currentDirectory = directory;
                currentDone = vst3Paths.Count + index;
                progress?.Report(new InsertScanProgress(vst3Paths.Count + index, total, directory));
            },
            onDetail: name => progress?.Report(new InsertScanProgress(currentDone, total, currentDirectory, name)))
            .Select(p => new InsertPluginEntry
            {
                Format = p.Format,
                PluginId = p.PluginId,
                Name = p.Name,
                Vendor = p.Vendor,
                Category = p.Category,
                IsInstrument = p.IsInstrument,
                Locator = p.Locator,
            }));

        // 同一台机器上"同一个插件装了两份"很常见（系统目录一份 + 自己的收藏目录一份）。
        // 它们的 PluginId 相同 —— 而 PluginId 正是工程里存的标识，重复条目只会让菜单出现
        // 两条一模一样的项，且选中哪一条都指回同一个 id。按 (格式, PluginId) 排重，
        // 保留扫描顺序里的第一条（= 用户路径清单里靠前的那个）。
        entries = Dedupe(entries);

        // 乐器不是插入效果：混音台的 FX 只挂效果器（乐器归宿主自己的轨道乐器系统管，
        // 插进混音台它也没有 MIDI 输入可用）。所以扫描结果里**直接不留** ——
        // 缓存、FX 菜单、设置页的计数都看不到合成器。
        entries.RemoveAll(entry => entry.IsInstrument);

        state.Cache = entries.Select(ToCached).ToList();
        state.CacheVersion = CurrentCacheVersion;
        state.LastScanUtc = DateTime.UtcNow.ToString("u");

        Save(state);

        mLastScanText = DateTime.Now.ToString("G");

        return entries;
    }

    // 按 (格式, PluginId) 去重。PluginId 是工程里持久化的标识，所以"同名同格式"的两条
    // 对工程来说根本就是同一个东西 —— 列两条既没用又会让人以为装了两套不同的插件。
    static List<InsertPluginEntry> Dedupe(List<InsertPluginEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<InsertPluginEntry>(entries.Count);

        foreach (var entry in entries)
        {
            if (seen.Add(entry.Format + "|" + entry.PluginId))
                unique.Add(entry);
        }

        return unique;
    }

    static readonly Vst3Backend sVst3Backend = new();
    static readonly Vst2Backend sVst2Backend = new();

    static InsertPluginEntry ToEntry(CachedPlugin cached) => new()
    {
        Format = cached.Format,
        PluginId = cached.PluginId,
        Name = cached.Name,
        Vendor = cached.Vendor,
        Category = cached.Category,
        IsInstrument = cached.IsInstrument,
        IsAvailable = cached.IsAvailable,
        UnavailableReason = cached.UnavailableReason,
        Locator = cached.Locator,
    };

    static CachedPlugin ToCached(InsertPluginEntry entry) => new()
    {
        Format = entry.Format,
        PluginId = entry.PluginId,
        Name = entry.Name,
        Vendor = entry.Vendor,
        Category = entry.Category,
        IsInstrument = entry.IsInstrument,
        IsAvailable = entry.IsAvailable,
        UnavailableReason = entry.UnavailableReason,
        Locator = entry.Locator,
    };

    // ── 落盘 ──

    public static State Load()
    {
        string path = SettingsPath;

        State state;

        if (!File.Exists(path))
        {
            state = new State();
        }
        else
        {
            try
            {
                state = JsonSerializer.Deserialize<State>(File.ReadAllText(path), JsonOptions) ?? new State();
            }
            catch (Exception ex)
            {
                // 读不出来就退回默认清单：设置坏了不该让混音台打不开。
                HostLog.Error("insert-plugins: failed to read settings, falling back to defaults: " + ex.Message);
                state = new State();
            }
        }

        // 默认值必须在**两条路径上都补齐**：早先只在"文件存在"那条补，于是首次运行（文件还不存在）
        // 返回的是 null 清单，Scan() 便一个目录都不扫 —— 界面上就是"明明装了插件却搜不到"。
        state.Vst3Paths ??= DefaultVst3Paths.ToList();
        state.Vst2Paths ??= DefaultVst2Paths.ToList();
        return state;
    }

    public static void Save(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            HostLog.Error("insert-plugins: failed to write settings: " + ex.Message);
        }
    }

    // 缓存格式版本。只有"扫描结果里有意义的那批字段"变了才加 1。
    // 2 = 开始区分乐器（并且不再把乐器写进缓存）；1 / 0 = 老格式，读的时候当空表。
    const int CurrentCacheVersion = 2;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public sealed class State
    {
        public int CacheVersion { get; set; }
        public List<string>? Vst3Paths { get; set; }
        public List<string>? Vst2Paths { get; set; }
        public List<CachedPlugin> Cache { get; set; } = [];
        public string? LastScanUtc { get; set; }
    }

    // public 而不是 private：State.Cache 是 public 属性（要交给 JSON 序列化器），
    // 而 C# 不允许 public 属性露出更封闭的类型。补丁看不到它（契约只给 InsertPluginEntry）。
    public sealed class CachedPlugin
    {
        public string Format { get; set; } = string.Empty;
        public string PluginId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Vendor { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;

        // 乐器（VST3 Instrument / VST2 effFlagsIsSynth）。菜单按它过滤，见 InsertPluginEntry.IsInstrument。
        public bool IsInstrument { get; set; }

        public bool IsAvailable { get; set; } = true;
        public string? UnavailableReason { get; set; }
        public string Locator { get; set; } = string.Empty;
    }
}




