using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.ExperimentalPatch.VstBridge;

namespace TuneLab.ExperimentalPatch.InsertHost;

// CLAP 后端（宿主侧）。与 VST3 / VST2 同一条纪律：**插件代码一律不在本进程里跑**。
//
//   目录枚举 / 文件存在性 → 本进程，纯文件读取（不认识插件内容）
//   问描述（init → 枚举 → deinit）→ 64 位桥（ListPlugins 消息，见 ClapScanner）
//   加载 / 音频 / 状态 / 参数 → P-C2，同样在桥里
//
// 【为什么扫描不能"只读文件"】VST3 的厂商/类别能在 moduleinfo.json 与二进制常量里找到，
// CLAP 没有这些东西：id / 名称 / 厂商 / features 全是运行时的字符串。想知道就得加载。
// 加载坏插件能带走进程，所以这件事只能发生在桥里。
//
// 【Locator 为什么要带 id】一个 .clap 可以声明多个插件（factory 里多个 descriptor），
// 而落盘的 PluginId 是 "clap:" + descriptor.id。定位一个具体插件需要"文件 + id"两段，
// 所以 Locator 编码成 `<文件路径>\n<pluginId>`（见 MakeLocator）。
public sealed class ClapBackend : IInsertBackend
{
    // 界面上的格式名。协议值是 PluginFormats.Clap（小写）。
    public const string FormatName = "CLAP";

    string IInsertBackend.FormatName => FormatName;

    // P-C1 只做到扫描：能列出、能看厂商、能记进缓存，但还不能发声。
    // P-C2（音频/参数/状态）落地时把这里改成 true，并把 InsertPluginSettings 的
    // CurrentCacheVersion 加一 —— 否则旧缓存里那批"IsAvailable = false"会一直灰着。
    //
    // 用 static readonly（不是 const）：Create 里对它的判断是真的运行期分支，
    // 写成 const false 会让编译器把后半段当不可达代码报警告。
    internal static readonly bool PlaybackAvailable = false;

    internal const string PlaybackUnavailableReason =
        "CLAP playback is not implemented yet (this build can only scan)";

    // 能不能用 = 桥可执行文件在不在。CLAP 不需要 per-RID 的 native 宿主库，
    // 所以"桥在"就是全部条件；桥到底带不带 CLAP 由握手后的 BridgeFormats 决定。
    public bool IsAvailable => VstBridgeClient.BridgeExecutableExists;

    public string? UnavailableReason => IsAvailable ? null : "the isolated host process is not installed";

    public IReadOnlyList<InsertPluginInfo> ListPlugins(IReadOnlyList<string>? directories = null,
        Action<int, int, string>? onDirectory = null, bool probeInstruments = true, Action<string>? onDetail = null)
    {
        // 扫哪 = 用户配的清单（EP-MIXER-12）。空清单 = 什么都不扫 —— 与 VST2 一致，
        // 不擅自去猜系统目录（默认清单由设置层给，见 InsertPluginSettings.DefaultClapPaths）。
        if (directories == null || directories.Count == 0)
            return [];

        var files = new List<string>();

        // 同一个 .clap 可能被两个目录同时包含（公共目录 + 私有收藏）：按完整路径去重。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < directories.Count; i++)
        {
            string directory = directories[i];

            // 进度先报后扫（见 IInsertBackend.ListPlugins 的说明）。
            onDirectory?.Invoke(i, directories.Count, directory);

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            foreach (string file in EnumeratePlugins(directory))
            {
                if (seen.Add(file))
                    files.Add(file);
            }
        }

        return ScanFiles(files, onDetail);
    }

    // 完整扫描。CLAP 没有"快扫"那半：描述本来就得加载问，厂商一直是官方口径 ——
    // 所以这条路与 ListPlugins 是同一件事，只是多了进度回调。
    public IReadOnlyList<InsertPluginInfo> ListPluginsDeep(IProgress<float>? progress = null,
        Action<string>? onDetail = null)
    {
        var paths = DefaultDirectories();

        if (paths.Count == 0)
            return [];

        return ScanDirectories(paths, progress, onDetail);
    }

    // 与 ListPlugins 同路，只是把进度报给调用方（完整扫描按钮用）。
    IReadOnlyList<InsertPluginInfo> ScanDirectories(IReadOnlyList<string> directories,
        IProgress<float>? progress, Action<string>? onDetail)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            foreach (string file in EnumeratePlugins(directory))
            {
                if (seen.Add(file))
                    files.Add(file);
            }
        }

        progress?.Report(0f);

        var entries = ScanFiles(files, onDetail);

        progress?.Report(1f);

        return entries;
    }

    // 默认清单（完整扫描没有"用户清单"可用时的兜底，与 VST3 的完整扫描同义）。
    // 设置层有同一份默认值；这里只用于"完整扫描"这条不经过设置页的路。
    static IReadOnlyList<string> DefaultDirectories() => ClapPaths.Default;

    // 一个目录里的 .clap。
    //
    // Windows / Linux 上插件就是一个文件；macOS 上是**目录包**（Foo.clap/Contents/MacOS/Foo）。
    // 两个平台分开枚举：Windows 上偶尔能看到空目录或解压残留的 Foo.clap 文件夹，
    // 那不是插件，不该在菜单里占一行。
    static IEnumerable<string> EnumeratePlugins(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*.clap", SearchOption.AllDirectories))
            yield return file;

        if (!OperatingSystem.IsMacOS())
            yield break;

        foreach (string bundle in Directory.EnumerateDirectories(directory, "*.clap", SearchOption.AllDirectories))
            yield return bundle;
    }

    // 过一个桥把文件清单问成插件描述。
    //
    // 桥不可用 / 不带 CLAP / 超时 → 空表 + onDetail 说一句为什么（设置页显示它）。
    // **不回退进程内**：那正是隔离要避免的事（同 Vst3Backend 的注释）。
    //
    // 【为什么一个文件问一次】扫描必须加载插件（CLAP 没有可静态读取的元数据），而坏插件
    // 能把桥带走。一次问完的话，几十个文件里有一个坏的，整轮扫描就只剩"失败"；
    // 分批之后坏的那个退化成一条灰掉的占位条目，其余照常出现。顺带解决两件事：
    //   · 进度：桥在一条阻塞的请求里没法回报进度，按文件问就天然有了"正在检查 xxx"；
    //   · 卡死：单个文件的时限可以远短于整轮扫描的时限。
    // 代价是每个文件一次往返 —— 与"加载一个插件"比起来，管道往返可以忽略。
    IReadOnlyList<InsertPluginInfo> ScanFiles(IReadOnlyList<string> files, Action<string>? onDetail)
    {
        if (files.Count == 0)
        {
            onDetail?.Invoke("no .clap files in the configured folders");
            return [];
        }

        if (!VstBridgeClient.BridgeExecutableExists)
        {
            onDetail?.Invoke("the isolated host process is not installed");
            return [];
        }

        var client = VstBridgeClient.ForBitness(64);

        if (!client.EnsureStartedBlocking())
        {
            onDetail?.Invoke(client.LastStartError ?? "the isolated host could not be started");
            return [];
        }

        // 桥不带 CLAP（旧桥）→ 如实说，不发一条它看不懂的消息。
        if (client.BridgeFormats is { } formats
            && !formats.Contains(PluginFormats.Clap, StringComparer.OrdinalIgnoreCase))
        {
            onDetail?.Invoke("the isolated host does not support CLAP (rebuild the bridge)");
            return [];
        }

        var list = new List<InsertPluginInfo>(files.Count);

        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);

            // 设置页的"正在检查 xxx"。桥没法在一条阻塞请求里回报进度，所以按文件报。
            onDetail?.Invoke(name);

            // 上一个文件把桥弄崩了：这里重新起一座，接着扫剩下的（崩的只是那一个插件）。
            if (!client.EnsureStartedBlocking())
            {
                list.Add(Unavailable(file, name,
                    "the isolated host could not be restarted: "
                    + (client.LastStartError ?? "unknown reason")));

                continue;
            }

            var entries = client.ScanPluginsBlocking(deep: false, PluginFormats.Clap, [file], ScanFileTimeout);

            if (entries == null)
            {
                // 超时 / 崩溃 / 协议错：这个文件扫不出来，别的文件不受影响。
                list.Add(Unavailable(file, name, client.LastTimeoutError
                    ?? client.LastProtocolError
                    ?? client.LastBridgeError
                    ?? "the isolated host did not answer"));
                continue;
            }

            foreach (var entry in entries)
                list.Add(Map(entry));
        }

        return list;
    }

    // 一个文件一个时限：加载慢是正常的（Surge XT 有 20 MB），但卡死不该占满整轮扫描的预算。
    static readonly TimeSpan ScanFileTimeout = TimeSpan.FromSeconds(20);

    static InsertPluginInfo Map(ScannedPluginInfo entry) => new()
    {
        Format = FormatName,
        PluginId = entry.PluginId,
        Name = entry.Name,
        Vendor = entry.Vendor,
        Category = entry.Category,
        IsInstrument = entry.IsInstrument,

        // 桥只说"这个文件能不能用"；能不能**出声**是宿主这一层的事（P-C2）。
        IsAvailable = entry.IsAvailable && PlaybackAvailable,
        UnavailableReason = !entry.IsAvailable
            ? entry.UnavailableReason ?? "the plugin cannot be loaded"
            : PlaybackAvailable ? null : PlaybackUnavailableReason,

        Locator = MakeLocator(entry.Locator, entry.PluginId),
    };

    // 扫不出来的文件也给一条：用户看得见"这个文件在这儿、但它用不了"，而不是凭空少一行。
    static InsertPluginInfo Unavailable(string file, string name, string reason)
    {
        string id = "clap:file:" + name;

        return new InsertPluginInfo
        {
            Format = FormatName,
            PluginId = id,
            Name = name,
            Locator = MakeLocator(file, id),
            IsAvailable = false,
            UnavailableReason = reason,
        };
    }

    // `<文件路径>\n<pluginId>`。用 \n 当分隔符：Windows 文件名不允许它，Linux 上路径里
    // 出现换行属于病态情况，而且我们只要第一个 \n 之前的部分。
    internal static string MakeLocator(string path, string pluginId) => path + "\n" + pluginId;

    internal static bool TrySplitLocator(string locator, out string path, out string pluginId)
    {
        int at = locator.IndexOf('\n');

        if (at <= 0)
        {
            path = locator;
            pluginId = string.Empty;
            return false;
        }

        path = locator[..at];
        pluginId = locator[(at + 1)..];
        return pluginId.Length > 0;
    }

    public IMixInsert? Create(InsertPluginInfo plugin, InsertSettings settings, out string? reason)
    {
        // P-C1：只到扫描。这里如实拒绝，而不是造一个"接上了但没声音"的插入 ——
        // 后者用户没法从现象上区分"插件没声"和"宿主没实现"。
        //
        // P-C2 的接缝：把 PlaybackAvailable 打开，并在下面按 Vst3BridgeInsert 的形状建
        // ClapBridgeInsert（Locator 里的"文件 + id"正好喂给桥的 LoadPlugin）。
        if (!PlaybackAvailable)
        {
            reason = PlaybackUnavailableReason;
            return null;
        }

        if (string.IsNullOrEmpty(plugin.Locator)
            || !TrySplitLocator(plugin.Locator, out _, out _))
        {
            reason = "plugin location is unknown (rescan first)";
            return null;
        }

        reason = "CLAP playback is not wired into the bridge yet";
        return null;
    }
}
