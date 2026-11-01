using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.ExperimentalPatch.VstBridge;

namespace TuneLab.ExperimentalPatch.InsertHost;

// VST3 后端（宿主侧）。**插件代码一律不在本进程里跑** —— 这是硬边界，不是开关：
//
//   加载 / 音频 / 状态 / 参数 / 界面 → 64 位桥里的 Vst3PluginHost（本类只造 Vst3BridgeInsert）
//   完整扫描（问插件要描述）        → 64 位桥（ListPlugins 消息，见 VstBridgeClient.ScanPluginsBlocking）
//   目录枚举 / moduleinfo / 二进制类别标记 → 本进程，纯文件读取，不加载也不执行插件
//
// 【为什么扫描也要过桥】"问插件要一份描述"实际是把插件的 dll 加载进本进程；坏插件在
// 这一步就能 abort 掉整个宿主（真机 66 个一起扫时复现过）。所以扫描与加载同一条纪律。
//
// 【没有回退了】桥不可用（没随包发出来 / 起不来 / 不带 VST3）时，VST3 就用不了 ——
// 如实报原因，不再把插件拉进 TuneLab 进程。进程内的那套实现已经搬到桥工程
// （VstBridge/InProcess/），宿主程序集里不再引用 ownvst3。
public sealed class Vst3Backend : IInsertBackend
{
    string IInsertBackend.FormatName => "VST3";

    // 能不能用 VST3 = 这台机器上有没有可用的隔离宿主（平台 + 64 位 + 桥可执行文件就位）。
    // 桥到底带不带 VST3（native 是否挨着桥）要握手后才确定，加载时会如实报错。
    public bool IsAvailable => Vst3BridgeInsert.IsolationAvailable;

    public string? UnavailableReason => IsAvailable ? null : Vst3BridgeInsert.IsolationUnavailableReason;

    public IReadOnlyList<InsertPluginInfo> ListPlugins(IReadOnlyList<string>? directories = null,
        Action<int, int, string>? onDirectory = null, bool probeInstruments = true, Action<string>? onDetail = null)
    {
        // 上游的 PluginScanner 有个硬限制：只扫它自己认定的标准位置，不接受目录参数。
        // 而"用户自定的扫描路径清单"是本功能的硬需求（EP-MIXER-12），所以：
        //   · 有清单 → 自己枚举（纯文件读取，见 ListFromDirectories）
        //   · 没有清单 → 让桥去扫标准位置（也就"完整扫描"用的那条）
        if (directories != null && directories.Count > 0)
            return ListFromDirectories(directories, onDirectory, probeInstruments, onDetail);

        if (directories == null)
            return ListFromBridgeScan(deep: false, onDetail);

        return [];
    }

    /// <summary>
    /// 完整扫描：让桥里的 native 扫描器走 Full 模式，把每个插件的**描述**问一遍
    /// （不是实例化）。厂商 / 版本 / 类别 / 声道数全在里面，是官方口径。
    /// </summary>
    public IReadOnlyList<InsertPluginInfo> ListPluginsDeep(
        IProgress<float>? progress = null, Action<string>? onDetail = null)
        => ListFromBridgeScan(deep: true, onDetail, progress);

    // 过桥扫描。桥不可用 / 超时 → 空表 + onDetail 说一句为什么（设置页显示它）。
    // 注意：**不**回退到进程内扫描 —— 那正是这条路径要避免的事。
    IReadOnlyList<InsertPluginInfo> ListFromBridgeScan(bool deep, Action<string>? onDetail,
        IProgress<float>? progress = null)
    {
        if (!VstBridgeClient.BridgeExecutableExists)
        {
            onDetail?.Invoke(Vst3BridgeInsert.IsolationUnavailableReason ?? "the isolated host is not installed");
            return [];
        }

        var client = VstBridgeClient.ForBitness(64);

        if (!client.EnsureStartedBlocking())
        {
            onDetail?.Invoke(client.LastStartError ?? "the isolated host could not be started");
            return [];
        }

        progress?.Report(0f);

        var entries = client.ScanPluginsBlocking(deep);
        progress?.Report(1f);

        if (entries == null)
        {
            onDetail?.Invoke(client.LastTimeoutError
                ?? client.LastBridgeError
                ?? "the isolated host did not answer the scan");
            return [];
        }

        var list = new List<InsertPluginInfo>(entries.Count);
        foreach (var entry in entries)
        {
            list.Add(new InsertPluginInfo
            {
                Format = entry.Format,
                PluginId = entry.PluginId,
                Name = entry.Name,
                Vendor = entry.Vendor,
                Category = entry.Category,
                Locator = entry.Locator,
                IsInstrument = entry.IsInstrument,
            });
        }

        return list;
    }

    // 按用户配的目录清单扫。
    //
    // 【纯文件读取】枚举 .vst3 包、读 moduleinfo.json 的厂商、在二进制里找类别标记 ——
    // 不加载插件，也不执行它的代码（所以放在宿主进程里是安全的）。
    //
    // 两个选择说明：
    //   ① 自己枚举 .vst3，而不是让上游扫 —— 上游那个不接受目录参数（见上）。
    //   ② 用 bundle 名做 PluginId，而不是 classId —— 拿 classId 要把插件 load 一遍，
    //      而 bundle 名在**同一台机器**上稳定，换机器重新扫就对上了。
    //      ⚠️ 代价：两个不同插件同名时会撞。取舍记在这里，别以后误以为是 bug。
    //
    // 【probeInstruments】判"是不是乐器"同样不加载插件：读二进制里的类别标记
    // （见 LooksLikeInstrument 的注释）。代价是 DPF 系乐器的子类别是运行时拼的、
    // 标记里没有 —— 真正的答案在完整扫描（桥里）或加载时（桥里）拿到。
    IReadOnlyList<InsertPluginInfo> ListFromDirectories(IReadOnlyList<string> directories,
        Action<int, int, string>? onDirectory, bool probeInstruments, Action<string>? onDetail)
    {
        var list = new List<InsertPluginInfo>();

        for (int i = 0; i < directories.Count; i++)
        {
            string directory = directories[i];

            // 进度先报后扫：卡在一个大目录上时，界面上写的就是"正在扫这个"。
            onDirectory?.Invoke(i, directories.Count, directory);

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            foreach (string bundle in Directory.EnumerateFiles(directory, "*.vst3", SearchOption.AllDirectories))
            {
                bool is32Bit = bundle.Contains(@"\x86-win\") || bundle.Contains(@"/x86-win/");

                string name = Path.GetFileNameWithoutExtension(bundle);

                bool isInstrument = false;

                if (probeInstruments)
                {
                    onDetail?.Invoke(name);
                    isInstrument = LooksLikeInstrument(bundle);
                }

                list.Add(new InsertPluginInfo
                {
                    Format = "VST3",
                    PluginId = "vst3:" + Path.GetFileNameWithoutExtension(bundle),
                    Name = name,

                    // 厂商从 Contents\Resources\moduleinfo.json 读（VST3 的二进制里没有它）。
                    // VST2 那边靠桥探 effGetVendorString，同一产品两种格式不该差一截。读不到就空，
                    // 界面按"未知厂商"分组 —— 与插件不带 moduleinfo.json 的现状一致。
                    Vendor = Vst3BundleInfo.VendorOf(bundle),
                    Locator = bundle,
                    Category = is32Bit ? "32-bit (needs the bridge)" : string.Empty,
                    IsInstrument = isInstrument,
                });
            }
        }

        return list;
    }

    // 探一个插件是乐器还是效果器 —— **不加载它**，只读 bundle 里的二进制。
    //
    // 【为什么不加载】实测过：扫描时对每个 bundle 走 LoadPlugin → Initialize → 问 IsInstrument，
    // 结果是灾难性的 —— 某个 DPF 插件的 assert 直接 abort，宿主没有任何机会救。
    // 扫描是"随手点一下"的动作，不能有这种代价。
    //
    // 【标记准不准】VST3 的类别是类信息里的**子类别字符串**，编译器把它作为明文常量放进二进制
    // （"Instrument|Synth" / "Fx|Delay"）。开发机上 64 个 64 位插件与逐个真实加载对过：63 个一致，
    // 差的那一个（Surge XT，类别确实是 Instrument）反而是标记更对 —— native 的 IsInstrument 判它 false。
    // 已知漏判：DPF 系（Cardinal）的子类别是运行时拼的，二进制里没有这个字面量。
    //
    // 【宁可漏判，不可误判】判成乐器的插件会从菜单里消失。误判一个效果器（用户会以为"插件没扫到"）
    // 比漏判一个合成器糟得多，所以只有在"有 Instrument 标记、且**没有** Fx 标记"时才判乐器。
    // 漏掉的由完整扫描（桥）兜住。
    internal static bool LooksLikeInstrument(string file)
    {
        try
        {
            return BinaryMarker.Contains(file, "Instrument|") && !BinaryMarker.Contains(file, "Fx|");
        }
        catch (Exception)
        {
            // 读不动（占用 / 权限）就当"不知道" —— 列出来。
            return false;
        }
    }

    public IMixInsert? Create(InsertPluginInfo plugin, InsertSettings settings, out string? reason)
    {
        reason = null;

        if (string.IsNullOrEmpty(plugin.Locator))
        {
            reason = "plugin location is unknown (rescan first)";
            return null;
        }

        if (!Vst3BridgeInsert.PlatformSupported)
        {
            reason = Vst3BridgeInsert.IsolationUnavailableReason ?? "the isolated host is not available";
            return null;
        }

        // 一律过桥。桥不可用 / 起不来 → 如实报错，**不回退进程内**：
        // 回退等于把插件拉回 TuneLab 自己的进程，正是隔离要避免的事。
        var insert = Vst3BridgeInsert.Create(plugin, settings, out string? bridgeReason, out bool bridgeUnavailable);

        if (insert != null)
            return insert;

        reason = bridgeUnavailable
            ? "VST3 runs only in the isolated host process: " + (bridgeReason ?? "the bridge is unavailable")
            : bridgeReason;

        return null;
    }

    // 重新定位：用户在别的机器上打开工程，插件扫描结果变了。
    // PluginId 是软的（路径会变），所以这里就是"按 PluginId 再扫一次"。
    public InsertPluginInfo? Resolve(string pluginId)
        => ListPlugins(probeInstruments: false).FirstOrDefault(p => p.PluginId == pluginId);
}
