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
            return state.Cache.Select(ToEntry).ToList();
        }
    }

    public string? LastScanText { get; private set; }

    public IReadOnlyList<InsertPluginEntry> Scan()
    {
        var state = Load();

        var entries = new List<InsertPluginEntry>();

        // 扫哪 = 用户配的清单（EP-MIXER-12）。空清单 = 什么都不扫，不擅自去扫系统目录。
        // 扫哪 = 用户配的清单（EP-MIXER-12）。空清单 = 什么都不扫，不擅自去扫系统目录。
        entries.AddRange(sVst3Backend.ListPlugins(state.Vst3Paths ?? []).Select(p => new InsertPluginEntry
        {
            Format = p.Format,
            PluginId = p.PluginId,
            Name = p.Name,
            Vendor = p.Vendor,
            Category = p.Category,
            Locator = p.Locator,
        }));

        // VST2 的 32 位插件要过桥（Vst2Backend / VstBridge），这里先扫出来、标成
        // "当前进程用不了"。用户看得见自己装了，而不是"明明装了却搜不到"。
        entries.AddRange(sVst2Backend.ListPlugins(state.Vst2Paths ?? []).Select(p => new InsertPluginEntry
        {
            Format = p.Format,
            PluginId = p.PluginId,
            Name = p.Name,
            Vendor = p.Vendor,
            Category = p.Category,
            Locator = p.Locator,
        }));

        state.Cache = entries.Select(ToCached).ToList();
        state.LastScanUtc = DateTime.UtcNow.ToString("u");

        Save(state);

        LastScanText = DateTime.Now.ToString("G");

        return entries;
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
        IsAvailable = entry.IsAvailable,
        UnavailableReason = entry.UnavailableReason,
        Locator = entry.Locator,
    };

    // ── 落盘 ──

    public static State Load()
    {
        string path = SettingsPath;

        if (!File.Exists(path))
            return new State();

        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path), JsonOptions) ?? new State();
            state.Vst3Paths ??= DefaultVst3Paths.ToList();
            state.Vst2Paths ??= DefaultVst2Paths.ToList();
            return state;
        }
        catch (Exception ex)
        {
            // 读不出来就退回默认清单：设置坏了不该让混音台打不开。
            HostLog.Error("insert-plugins: failed to read settings, falling back to defaults: " + ex.Message);
            return new State();
        }
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

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public sealed class State
    {
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
        public bool IsAvailable { get; set; } = true;
        public string? UnavailableReason { get; set; }
        public string Locator { get; set; } = string.Empty;
    }
}

