using System;
using System.Collections.Generic;
using System.IO;
using TuneLab.ExperimentalPatch.InsertHost;

// 桥进程里的 CLAP 扫描器：把一个 .clap 文件里的**插件描述**问出来。
//
// 【为什么必须在这里】CLAP 的描述（id / 名称 / 厂商 / features）是运行时的字符串，
// 二进制里没有可读的静态副本 —— 想知道就得把插件加载进来。而"加载坏插件"能把进程
// 直接带走（VST3 那边真机复现过），所以这件事只能在桥里做，与 VST3 的扫描同一条纪律。
//
// 【扫描 = init → 枚举描述 → deinit → Free】不 Create 插件实例：描述是工厂的静态信息，
// 问它不需要实例化。库的加载/卸载走 ClapLibrary 的引用计数 —— 插着一个 CLAP 插件时
// 再点扫描，不会把活实例脚下的库 deinit 掉。
//
// 【每个文件的结果】成功的每个插件一条；整个文件用不了时也给一条（IsAvailable = false），
// 与 VST2 目录里那种"扩展名对但内容不是 VST2"的处理一致：用户看得见它在那儿，
// 但不会点进去才发现装不进来。
internal static unsafe class ClapScanner
{
    // 这个桥能不能扫 CLAP：纯 P/Invoke，没有 per-RID 的 native 依赖 —— 永远为真。
    // x86 桥理论上也能扫 32 位 .clap，但宿主不把 32 位 CLAP 交给它（见 BridgeSession 的握手）。
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    // 界面上显示的格式名（与 IInsertBackend.FormatName 一致）。协议里那套是小写的 clap。
    const string ClapFormatName = "CLAP";

    public static List<InsertPluginInfo> Scan(IReadOnlyList<string>? files, Action<string>? detail = null)
    {
        var list = new List<InsertPluginInfo>();

        if (files == null || files.Count == 0)
            return list;

        foreach (string file in files)
        {
            string fallbackName = Path.GetFileNameWithoutExtension(file);

            // 界面上的"正在检查 xxx"：一个文件可能有几 MB，init 慢的时候要看得出还活着。
            detail?.Invoke(fallbackName);

            var described = Describe(file, out string? reason);

            if (described == null || described.Count == 0)
            {
                list.Add(new InsertPluginInfo
                {
                    Format = ClapFormatName,
                    PluginId = "clap:file:" + fallbackName,
                    Name = fallbackName,
                    Locator = file,
                    IsAvailable = false,
                    UnavailableReason = reason ?? "the file declares no CLAP plugins",
                });

                continue;
            }

            list.AddRange(described);
        }

        return list;
    }

    // 问一个 .clap 文件要描述。返回 null = 这个文件用不了（原因写进 reason）。
    static List<InsertPluginInfo>? Describe(string path, out string? reason)
    {
        // 租约保证 deinit 发生在**所有实例**都释放之后；扫描本身只是一个短暂的引用。
        using var lease = ClapLibrary.Acquire(path, out reason);

        if (lease == null)
            return null;

        return DescribeFromFactory(lease, path, out reason);
    }

    static List<InsertPluginInfo>? DescribeFromFactory(ClapLibrary.Lease lease, string path,
        out string? reason)
    {
        reason = null;

        var factory = lease.Factory;

        uint count = ClapNative.PluginCount(factory, out bool plausible);

        if (!plausible)
        {
            reason = "the plugin factory reported an implausible plugin count";
            return null;
        }

        var list = new List<InsertPluginInfo>((int)count);

        for (uint index = 0; index < count; index++)
        {
            ClapNative.PluginDescriptor* descriptor = factory->GetPluginDescriptor(factory, index);

            if (descriptor == null)
                continue;

            string? id = ClapNative.StringAt(descriptor->Id);

            // 没有 id 就没法落盘（PluginId 是工程里存的标识），也不算一个可用插件。
            if (string.IsNullOrWhiteSpace(id))
                continue;

            string? name = ClapNative.StringAt(descriptor->Name);
            string? vendor = ClapNative.StringAt(descriptor->Vendor);

            var features = ClapNative.FeaturesAt(descriptor->Features);

            list.Add(new InsertPluginInfo
            {
                Format = ClapFormatName,

                // descriptor.id 是 CLAP 规定的反向域名唯一标识 —— 天生跨机器稳定，
                // 比 VST3 那边"用包名当 id"的将就办法干净（见 Vst3Backend 的注释）。
                PluginId = "clap:" + id,
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name,
                Vendor = vendor ?? string.Empty,

                // features 是插件自报的类别词（"audio-effect" / "instrument" / "reverb" …）。
                // 存下来给菜单/将来的分类用；IsInstrument 也由它推。
                Category = features.Count == 0 ? string.Empty : string.Join(", ", features),
                Locator = path,
                IsInstrument = LooksLikeInstrument(features),
            });
        }

        if (list.Count == 0)
            reason = "the file declares no CLAP plugins";

        return list;
    }

    // features 里有没有"乐器"标记。
    //
    // 【口径】CLAP 规定乐器标 "instrument"、效果器标 "audio-effect"。与 VST3 那边
    // "有 Instrument 标记且没有 Fx 标记才算乐器"同一个取舍：宁可漏判（合成器混进效果
    // 菜单，用户点了没用）也不误判（一个效果器从菜单里消失，用户会以为没扫到）。
    internal static bool LooksLikeInstrument(IReadOnlyList<string> features)
    {
        bool instrument = false;
        bool effect = false;

        foreach (string feature in features)
        {
            if (string.Equals(feature, "instrument", StringComparison.OrdinalIgnoreCase))
                instrument = true;
            else if (string.Equals(feature, "audio-effect", StringComparison.OrdinalIgnoreCase))
                effect = true;
        }

        return instrument && !effect;
    }
}
