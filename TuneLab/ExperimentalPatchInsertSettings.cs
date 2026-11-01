using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    // [VST3-BRIDGE] 隔离开关（本机设置）。读写都落进 insert-plugins.json。
    //
    // 【默认开（P5.5）】**所有平台一律开**：没有显式设置过就隔离。不做平台特例、也不跟"本机
    // 桥在不在"走 —— 跨平台一致的含义就是同一条规则。桥不在 / 32 位宿主时由 Vst3Backend 的
    // 回退路径接管（如实记原因 + 写日志），设置页状态行会把"现在其实在进程内"写出来。
    public bool IsolateVst3 => Load().IsolateVst3 ?? true;

    public void SetIsolateVst3(bool value)
    {
        var state = Load();
        state.IsolateVst3 = value;
        state.SettingsVersion = CurrentSettingsVersion;
        Save(state);

        // 改了开关 = 换了一套配置：上一次回退的原因不再代表"现在会怎么跑"。
        // 打开时清掉（等下一次真实加载重新判定），关掉时也清（免得下次打开先亮一条旧账）。
        NoteIsolationFallback(null);
    }

    public bool IsolateVst3Available => IsolationAvailableProbe();

    public string? IsolateVst3UnavailableReason =>
        IsolateVst3Available ? null : Vst3BridgeInsert.IsolationUnavailableReason;

    public string? IsolateVst3FallbackReason
    {
        get
        {
            lock (sFallbackLock)
                return sIsolationFallbackReason;
        }
    }

    // "本机能不能隔离"的探针。默认问 InsertHost（64 位 + 支持的平台 + 桥 exe 就位）。
    // 测试可替换：默认值本身要能脱离"这台机器上有没有桥"来断言。
    internal static Func<bool> IsolationAvailableProbe { get; set; } =
        () => Vst3BridgeInsert.IsolationAvailable;

    static string? sIsolationFallbackReason;
    static readonly object sFallbackLock = new();

    // 建实例时发现"要求隔离却回了进程内" → 记一笔给设置页显示；真的隔离成功 → 清掉。
    // 只放内存：桥是能修好的东西（重装 / 重启后自愈），不该把一次失败永久写进设置。
    public static void NoteIsolationFallback(string? reason)
    {
        lock (sFallbackLock)
            sIsolationFallbackReason = string.IsNullOrEmpty(reason) ? null : reason;
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
        => ScanCore(deep: false, progress);

    /// <summary>
    /// 完整扫描：VST3 走 native 的 Full 模式，把每个插件的厂商/版本问一遍（慢，但准）。
    /// </summary>
    /// <remarks>
    /// 为什么普通扫描的 VST3 多半没有厂商：那条路只读二进制里的类别标记，
    /// 而厂商不在二进制里（信息本来就在插件的 factory 上，要 load 之后才问得到）。
    /// 完整扫描走"问描述"而不是"实例化"，所以安全 —— 实测实例化会 abort 进程，
    /// 问描述不会。详见 Patches/Mixer/NOTES-vst3-vendor.md。
    /// </remarks>
    public IReadOnlyList<InsertPluginEntry> ScanDeep(IProgress<InsertScanProgress>? progress = null)
        => ScanCore(deep: true, progress);

    IReadOnlyList<InsertPluginEntry> ScanCore(bool deep, IProgress<InsertScanProgress>? progress)
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

        List<InsertPluginInfo> vst3;

        if (deep)
        {
            // 完整扫描：native Full 模式自带进度（0..1 + 当前条目名），没有"目录"概念，
            // 所以借 currentDirectory 显示正在问哪个插件。
            var deepProgress = new Progress<float>(fraction =>
                progress?.Report(new InsertScanProgress(currentDone, total, currentDirectory)));
            var deepDetail = new Action<string>(name =>
            {
                currentDirectory = name;
                progress?.Report(new InsertScanProgress(currentDone, total, currentDirectory, name));
            });

            var fromScanner = sVst3Backend.ListPluginsDeep(deepProgress, deepDetail).ToList();

            // 完整扫描 = native 问描述（厂商/版本准），但它**只认标准 VST3 目录**。
            // 以前这里直接把它当全部结果，于是用户配的自定义目录（EP-MIXER-12）整片消失 ——
            // "点一下完整扫描，一半插件从菜单里没了"。这里把配置目录补回来：
            // 同一个 bundle 以 native 那份为准（它带厂商），其余的用目录扫描的结果顶上。
            var fromDirectories = sVst3Backend.ListPlugins(vst3Paths, (index, _, directory) =>
                {
                    currentDirectory = directory;
                    currentDone = index;
                    progress?.Report(new InsertScanProgress(index, total, directory));
                },
                onDetail: name => progress?.Report(new InsertScanProgress(currentDone, total, currentDirectory, name)));

            vst3 = MergeVst3Scan(fromScanner, fromDirectories);
        }
        else
        {
            vst3 = sVst3Backend.ListPlugins(vst3Paths, (index, _, directory) =>
                {
                    currentDirectory = directory;
                    currentDone = index;
                    progress?.Report(new InsertScanProgress(index, total, directory));
                },
                onDetail: name => progress?.Report(new InsertScanProgress(currentDone, total, currentDirectory, name)))
            .ToList();
        }

        entries.AddRange(vst3.Select(p => new InsertPluginEntry
        {
            Format = p.Format,
            PluginId = p.PluginId,
            Name = p.Name,
            Vendor = p.Vendor,
            Category = p.Category,
            IsInstrument = p.IsInstrument,
            IsAvailable = p.IsAvailable,
            UnavailableReason = p.UnavailableReason,
            Locator = p.Locator,
        }));

        // VST2 一律过桥（Vst2Backend / VstBridge），x86 与 x64 都支持；
        // 桥的可执行文件缺失时 Create 会给原因，扫描这里只负责"列出来"。
        //
        // 完整扫描模式下 VST2 照样要扫 —— deep 只改 VST3 那条路（它才有"厂商是空的"问题），
        // VST2 一直是从桥问真插件，厂商早就有了。
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
                IsAvailable = p.IsAvailable,
                UnavailableReason = p.UnavailableReason,
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

    // 两个 locator 是不是同一个 VST3 bundle。native 扫描器与自己的目录枚举给的路径
    // 可能不同形（一个给包目录、一个给包内二进制），所以按"bundle 根"比：
    // 路径里第一段以 .vst3 结尾的组件为止。单文件 bundle 没有 \Contents，就比整条路径。
    static bool SameBundle(string a, string b)
        => string.Equals(BundleRoot(a), BundleRoot(b), StringComparison.OrdinalIgnoreCase);

    static string BundleRoot(string locator)
    {
        string path = locator.Replace('/', '\\');

        int at = path.IndexOf(".vst3\\", StringComparison.OrdinalIgnoreCase);

        return at < 0 ? path : path[..(at + ".vst3".Length)];
    }

    // 完整扫描的 VST3 合并：native 那份（标准位置，带厂商）+ 配置目录那份
    // （自带 moduleinfo 厂商）。同一个 bundle 只留一条，优先 native ——
    // 它问过插件的 factory，厂商/类别比读文件猜的准。
    internal static List<InsertPluginInfo> MergeVst3Scan(IReadOnlyList<InsertPluginInfo> fromScanner,
        IReadOnlyList<InsertPluginInfo> fromDirectories)
    {
        var merged = new List<InsertPluginInfo>(fromScanner.Count + fromDirectories.Count);
        merged.AddRange(fromScanner);

        foreach (var plugin in fromDirectories)
        {
            if (fromScanner.Any(native => SameBundle(native.Locator, plugin.Locator)))
                continue;

            merged.Add(plugin);
        }

        return merged;
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

        // 迁移：旧版本里的 IsolateVst3 = false 不是用户的选择（那一版默认就是关，
        // 任何一次扫描 / 保存都会写出来）。按"未设置"处理，让 P5.5 的默认值生效；
        // 之后用户在界面上做的选择会写 SettingsVersion = 2，从此不再被改写。
        if (state.SettingsVersion < CurrentSettingsVersion)
        {
            state.IsolateVst3 = null;
            state.SettingsVersion = CurrentSettingsVersion;
        }

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
    // 4 = 完整扫描（ScanDeep）把 VST3 的厂商/版本按官方口径写进缓存。
    // 3 = VST3 开始从 moduleinfo.json 读厂商（同一产品的两种格式不再有厂商落差）。
    // 2 = 开始区分乐器（并且不再把乐器写进缓存）；1 / 0 = 老格式，读的时候当空表。
    const int CurrentCacheVersion = 4;

    // 设置文件版本。只记"必须迁移的语义变化"，与缓存格式无关。
    // 2 = 隔离开关有了"没设置过"这个状态（P5.5 默认开）。
    //     1 / 0 = 那一版默认就是关，任何一次扫描 / 保存都会写出 IsolateVst3: false ——
    //     那个 false 不是用户的选择，所以读到旧版本时按"未设置"处理，让新默认生效。
    const int CurrentSettingsVersion = 2;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,

        // null 的字段干脆不写：旧版本程序读到 "IsolateVst3": null 会当反序列化失败，
        // 从而把整份清单（用户的扫描路径）一起丢掉。"没这个键"在旧版本里就是默认值，安全。
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed class State
    {
        public int CacheVersion { get; set; }
        public List<string>? Vst3Paths { get; set; }
        public List<string>? Vst2Paths { get; set; }
        public List<CachedPlugin> Cache { get; set; } = [];
        public string? LastScanUtc { get; set; }

        // [VST3-BRIDGE] 把 VST3 放进桥进程（隔离崩溃 / 挂起）。
        // null = 没设置过 → 跟"这台机器能不能隔离"走（P5.5 起默认开）；
        // true / false = 用户显式选的，一直按它来。
        public bool? IsolateVst3 { get; set; }

        // 设置文件自身的格式版本（与 CacheVersion 是两条独立的轴，见 CurrentSettingsVersion）。
        public int SettingsVersion { get; set; }
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




