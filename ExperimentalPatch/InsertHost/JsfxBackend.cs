using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.ExperimentalPatch.VstBridge;

namespace TuneLab.ExperimentalPatch.InsertHost;

// JSFX 后端：宿主侧只做**文本扫描**（desc / author / 路径），绝不在这里执行 EEL2。
//
// 与 VST3 / CLAP 的分工相反：JSFX 没有二进制元数据要问，扫描的起点就是读文件头。
// 真正加载 / 编译 / 跑音频都在桥进程里交给 ysfx（JsfxPluginHost）。
//
// 【扫描时"探一下"】文本头只能回答"叫什么、谁写的、有没有 @gfx"，回答不了真正决定
// 能不能用的问题：EEL2 编不编得过、import 找不找得到、引脚是不是 2 进 2 出。
// 用户点进菜单才发现插不进去的那些，正是这类文件（例如 ReaRack2 - LFO：只有 @block，
// 没有 @sample，因此声明 0 进 0 出）。所以扫描（probeInstruments = true）时每个文件都在
// 桥里真走一次 Load（临时槽位，探完就卸）。探针停在"加载 + 编译 + 引脚检查"，
// 不跑 @init —— 理由见 LoadPluginMessage.ProbeOnly。
//
// 探不动（桥没起来 / 管道断了）时**不据此定罪**：条目照旧按文本头列出来、算可用。
// 宁可让用户看见一个用不了的，也不要让他看不见一个能用的（与 Vst2Backend 同一条原则）。
//
// 【厂商/作者】ysfx 自己也会读（ysfx_get_author）。探针走得动时以它的口径为准；
// 走不动时按同一套约定读文本：`author:` 行，以及不规范但很常见的 `//author:` 注释
// （与 ysfx 的 ysfx_parse.cpp 两遍解析一致：带标签的走一遍，注释的走一遍）。
public sealed class JsfxBackend : IInsertBackend
{
    public const string FormatName = "JSFX";

    string IInsertBackend.FormatName => FormatName;

    public bool IsAvailable => JsfxBridgeInsert.IsolationAvailable;

    public string? UnavailableReason => JsfxBridgeInsert.IsolationUnavailableReason;

    public IReadOnlyList<InsertPluginInfo> ListPlugins(IReadOnlyList<string>? directories = null,
        Action<int, int, string>? onDirectory = null, bool probeInstruments = true, Action<string>? onDetail = null)
    {
        var roots = directories ?? [];
        var list = new List<InsertPluginInfo>();

        for (int index = 0; index < roots.Count; index++)
        {
            string root = roots[index];
            onDirectory?.Invoke(index, roots.Count, root);

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                });
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string file in files)
            {
                if (!string.Equals(Path.GetExtension(file), ".jsfx", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var plugin = ReadPlugin(file, root);
                    if (plugin == null)
                        continue;

                    // 先报名字再探：探一个文件可能要几十毫秒，进度条上"正在检查 xxx"要能看见它。
                    onDetail?.Invoke(plugin.Name);

                    // probeInstruments = 这次调用是不是"扫描"（按 pluginId 反查路径的调用传 false，
                    // 不为它白跑一遍桥）。
                    if (probeInstruments)
                    {
                        var probe = Probe(file, root);
                        if (probe != null)
                            plugin = WithProbe(plugin, probe);
                    }

                    list.Add(plugin);
                }
                catch (Exception)
                {
                    // 读不了的文件不挡住同目录的其它 JSFX。
                }
            }
        }

        return list;
    }

    public IMixInsert? Create(InsertPluginInfo plugin, InsertSettings settings, out string? reason)
        => JsfxBridgeInsert.Create(plugin, settings, out reason, out _);

    static InsertPluginInfo? ReadPlugin(string file, string root)
    {
        string? desc = null;
        string? author = null;
        bool hasGfx = false;

        // 头部元数据只在第一个 `@section` 之前有效（与 ysfx 的头部段落解析口径一致）；
        // 过了这一段还继续扫，是为了找出 @gfx（有没有编辑器窗口）。
        bool inHeader = true;

        using (var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            for (int lineNumber = 0; lineNumber < 512 && reader.ReadLine() is { } line; lineNumber++)
            {
                string trimmed = line.Trim();

                if (trimmed.StartsWith("@", StringComparison.Ordinal))
                {
                    inHeader = false;

                    if (trimmed.StartsWith("@gfx", StringComparison.OrdinalIgnoreCase))
                        hasGfx = true;

                    continue;
                }

                if (!inHeader)
                    continue;

                if (desc == null && trimmed.StartsWith("desc:", StringComparison.OrdinalIgnoreCase))
                    desc = trimmed[5..].Trim();
                else if (author == null && trimmed.StartsWith("author:", StringComparison.OrdinalIgnoreCase))
                    author = trimmed[7..].Trim();
                else if (author == null && trimmed.StartsWith("//author:", StringComparison.OrdinalIgnoreCase))
                    author = trimmed[9..].Trim();
            }
        }

        string relative;

        try
        {
            relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        }
        catch (Exception)
        {
            relative = Path.GetFileName(file);
        }

        if (relative.StartsWith("../", StringComparison.Ordinal))
            relative = Path.GetFileName(file);

        string name = string.IsNullOrWhiteSpace(desc) ? Path.GetFileNameWithoutExtension(file) : desc!;

        return new InsertPluginInfo
        {
            Format = FormatName,
            PluginId = "jsfx:" + relative,
            Name = name,
            // 没有 author 就留空 —— 添加菜单会显示"(未知厂商)"。以前这里填死 "JSFX"，
            // 把"格式"当成了"作者"，所以所有 JSFX 都挤在同一栏里。
            Vendor = author ?? string.Empty,
            Category = hasGfx ? "JSFX (gfx)" : "JSFX",
            Locator = Path.GetFullPath(file),
            IsInstrument = false,
            IsAvailable = true,
        };
    }

    // ── 扫描探针 ──

    // 走与真正插入**完全同一条路**（桥的 Load），所以这里探得动的文件，插进混音台也装得进，
    // 反过来也成立 —— 不会出现"扫描时说能用、点下去才报错"。
    // 用的是临时槽位：与正在跑的插入实例互不干扰。
    //
    // 返回 null = 桥没起来 / 管道断了：不据此定罪（见类注释）。
    static ProbeResult? Probe(string file, string root)
    {
        var client = VstBridgeClient.ForBitness(64);

        if (!client.EnsureStartedBlocking())
            return null;

        int slot = client.AllocateSlot();

        // 与 Vst2Backend 同一个理由：文件把桥卡住时，扫描线程不能陪着它等一辈子。
        using var timeout = new CancellationTokenSource(ProbeTimeout);

        try
        {
            var loaded = Task.Run(() => client.TryLoadAsync(slot, file, 48000, 512, bypass: true,
                cancellation: timeout.Token, format: PluginFormats.Jsfx, importRoot: root, probeOnly: true))
                .GetAwaiter().GetResult();

            if (loaded is { Ok: true })
                return new ProbeResult(true, loaded.Name, loaded.Vendor, loaded.HasEditor, null);

            if (timeout.IsCancellationRequested)
            {
                // 管道里的回复已经错位，留着它只会答错下一个问题（同 Vst2Backend.Probe）。
                client.KillBridge();
                return new ProbeResult(false, null, null, false, "timed out while loading this file");
            }

            if (loaded == null)
                return null;

            return new ProbeResult(false, loaded.Name, loaded.Vendor, loaded.HasEditor,
                loaded.Reason ?? "ysfx cannot load this file");
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try
            {
                client.UnloadBlocking(slot);
            }
            catch (Exception)
            {
                // 卸载失败不影响清单：槽位会随桥进程一起消失。
            }
        }
    }

    // 探一个文件的上限。编译一个 .jsfx 是毫秒级的事，20 秒不动就是坏了。
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    sealed record ProbeResult(bool Ok, string? Name, string? Vendor, bool HasEditor, string? Reason);

    // 探针结论落到清单条目上。
    //
    // 探得过：名字 / 作者 / 有没有 @gfx 都以 ysfx 的口径为准（真正加载文件的是它，
    // 文本头只是猜）；探不过：条目照样留在结果里（缓存里留个底），IsAvailable = false，
    // 由宿主决定"不摆进菜单"（见 InsertPluginSettings.ScanCore）。
    static InsertPluginInfo WithProbe(InsertPluginInfo plugin, ProbeResult probe)
    {
        return new InsertPluginInfo
        {
            Format = plugin.Format,
            PluginId = plugin.PluginId,
            Name = string.IsNullOrWhiteSpace(probe.Name) ? plugin.Name : probe.Name!,
            Vendor = probe.Ok ? probe.Vendor ?? string.Empty : plugin.Vendor,
            Category = probe.Ok ? (probe.HasEditor ? "JSFX (gfx)" : "JSFX") : plugin.Category,
            Locator = plugin.Locator,
            IsInstrument = plugin.IsInstrument,
            IsAvailable = probe.Ok,
            UnavailableReason = probe.Ok ? null : probe.Reason,
        };
    }
}
