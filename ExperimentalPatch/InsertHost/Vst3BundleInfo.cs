using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TuneLab.ExperimentalPatch.InsertHost;

// VST3 bundle 的厂商信息读取。
//
// 【为什么需要】同一个产品两种格式不该差一截：VST2 靠桥探 effGetVendorString，天然有厂商；
// VST3 的工厂信息（vendor / url / e-mail）是**结构体里的字符串**，要插件 load 之后才能问 ——
// 而扫描时不能 load（见 Vst3Backend.LooksLikeInstrument 的注释：会 abort、会 20 秒不返回）。
// 于是以前 VST3 一律"未知厂商"。
//
// 【按来源分两条路】
//
//   ① moduleinfo.json（**准**）：VST3 SDK 的 build 脚本会把工厂信息写成这个文件。
//      在 Contents\Resources\ 下。读到就用它 —— 这是官方口径，优先。
//      已知样本：ElephantDSP.com（RoomReverb）、Blue Wings Music（Fire）。
//
//   ② 二进制的 NUL 分隔字符串串（**启发式**）：插件自己嵌进去的明文。
//      DPF 系（Zam、Cardinal 等）把它编译成 `name\0描述\0vendor\0url\0license\0` 的常量串；
//      JUCE 系（Blue Cat、Surge XT）在控制器代码里嵌 `name\0vendor\0subcat\0` 那种小工厂信息块。
//      这条路不保证每个插件都有，也不保证都长得一样 —— 只作为拿不到 json 时的尽力而为。
//
// 【两条都不中就空串】不是所有插件都带 moduleinfo.json、也不是所有都嵌明文字符串。
// 读不到就返回空，界面按"未知厂商"分组 —— 与现状一致，不退化。
//
// 【性能】只在扫描时调用，一个 bundle 一次；结果按路径缓存，重扫不重复读同一文件。
// 用 1 MiB 的滑窗扫（与 ContainsMarker 同一套），不把整个二进制读进内存。
internal static class Vst3BundleInfo
{
    static readonly ConcurrentDictionary<string, string> sVendorCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取一个 VST3 文件的厂商名；读不到返回空串。</summary>
    public static string VendorOf(string bundleFile)
    {
        if (string.IsNullOrEmpty(bundleFile) || !File.Exists(bundleFile))
            return string.Empty;

        return sVendorCache.GetOrAdd(bundleFile, ReadSlow);
    }

    static string ReadSlow(string bundleFile)
    {
        try
        {
            var info = new FileInfo(bundleFile);
            DirectoryInfo? contents = info.Directory?.Parent;   // Contents
            DirectoryInfo? root = contents?.Parent;             // <name>.vst3

            // ① 官方口径：Contents\Resources\moduleinfo.json
            if (root != null)
            {
                string json = Path.Combine(root.FullName, "Contents", "Resources", "moduleinfo.json");
                if (File.Exists(json))
                {
                    var vendor = ReadVendorFromJson(File.ReadAllText(json, Encoding.UTF8));
                    if (!string.IsNullOrEmpty(vendor))
                        return vendor;
                }
            }

            // ② 启发式：二进制里的工厂信息字符串串
            return ReadVendorFromBinary(bundleFile);
        }
        catch (Exception)
        {
            // 读不动（IO / 权限 / 编码）就当没有：插件照常进列表，只是厂商为空。
            return string.Empty;
        }
    }

    // ── ① moduleinfo.json ───────────────────────────────────────────────
    //
    // 插件自带的 build 脚本生成的 JSON 常带尾逗号，严格解析会整个失败，所以用正则抠字段。
    internal static string ReadVendorFromJson(string json)
    {
        if (string.IsNullOrEmpty(json))
            return string.Empty;

        // 先抠 "Factory Info" 块（用大括号匹配），避免把 Classes 里类级的同名字段算进来。
        var factory = SliceBlock(json, "Factory Info");
        string scope = factory ?? json;

        return Strip(FirstStringValue(scope, "Vendor"));
    }

    static string? SliceBlock(string json, string key)
    {
        int start = IndexOfKey(json, key);
        if (start < 0)
            return null;

        int open = json.IndexOf('{', start);
        if (open < 0)
            return null;

        int depth = 0;
        for (int i = open; i < json.Length; i++)
        {
            if (json[i] == '{')
                depth++;
            else if (json[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return json.Substring(open, i - open + 1);
            }
        }

        return json.Substring(start);
    }

    static string FirstStringValue(string scope, string key)
    {
        int from = 0;
        while (from < scope.Length)
        {
            int start = IndexOfKey(scope, key, from);
            if (start < 0)
                return string.Empty;

            string? value = TryReadStringValue(scope, start);
            if (value != null)
                return value;

            from = start + key.Length;
        }

        return string.Empty;
    }

    static string? TryReadStringValue(string scope, int keyStart)
    {
        int colon = scope.IndexOf(':', keyStart);
        if (colon < 0)
            return null;

        int i = colon + 1;
        while (i < scope.Length && char.IsWhiteSpace(scope[i]))
            i++;

        if (i >= scope.Length || (scope[i] != '"' && scope[i] != '\''))
            return null;

        char close = scope[i];
        var text = new StringBuilder();

        for (int j = i + 1; j < scope.Length; j++)
        {
            char c = scope[j];
            if (c == close)
                return text.ToString();

            if (c == '\\' && j + 1 < scope.Length)
            {
                text.Append(scope[++j]);
                continue;
            }

            text.Append(c);
        }

        return text.ToString();
    }

    static int IndexOfKey(string text, string key)
        => IndexOfKey(text, key, 0);

    static int IndexOfKey(string text, string key, int from)
    {
        int index = text.IndexOf('"', from);

        while (index >= 0 && index + key.Length < text.Length)
        {
            int closing = text.IndexOf('"', index + 1);
            if (closing < 0)
                return -1;

            if (closing - index - 1 == key.Length &&
                string.CompareOrdinal(text, index + 1, key, 0, key.Length) == 0)
                return index;

            index = text.IndexOf('"', closing + 1);
        }

        return -1;
    }

    // ── ② 二进制的字符串串 ──────────────────────────────────────────────
    //
    // 三种成形布局（都来自 VST3 的工厂信息 / 类信息常量）：
    //
    //   DPF:  <name>\0<description>\0<vendor>\0<url>\0<license>\0
    //         vendor 紧跟在一个 URL 前面（Zam / Cardinal 全是这一种）。
    //   JUCE: <name>\0<vendor>\0<子类别...>\0  （小工厂信息块，Blue Cat / Surge XT）
    //         子类别是 "Fx|Analyzer" 这种管道分隔串。
    //
    // 已知坑：一句"拧螺丝"式的中文/英文 changelog 文案（"Fixed user interface freezing on
    // Mac OS X 10.8."）也会紧跟在 URL 前，形状和厂商一模一样。所以命中之后还要过
    // IsPlausibleVendor 的形态判断，并配一份已知的非厂商词表。
    //
    // 两个正则按 DPF → JUCE 的顺序取**靠前**的那个匹配（文件里两种都可能出现）。
    static readonly Regex[] sVendorPatterns =
    [
        // DPF 式：<vendor>\0<url>
        new("([A-Za-z][A-Za-z0-9&'._ -]{2,50})\x00https?://", RegexOptions.Compiled),

        // JUCE 式小工厂信息：<name>\0<vendor>\0<子类别>\0
        new("[A-Za-z0-9][A-Za-z0-9&'._ ()-]{2,60}\x00([A-Za-z][A-Za-z0-9&'._ -]{2,50})\x00(?:Fx|Instrument|Synth)", RegexOptions.Compiled),

        // ③ 嵌在"工厂信息"里的明文（JUCE 的 getName/getVendor 常量）
        //    形状是 <name>\0<vendor>\0<url>：name 与 vendor 相邻，vendor 后面是 URL。
        //    这是最后一道，前面两条都是从二进制里"嗅探"，这条是从 JUCE 族的代码常量。
        new("([A-Za-z0-9][A-Za-z0-9&'._ ()-]{3,60})\x00([A-Za-z][A-Za-z0-9&'._ -]{2,50})\x00https?://", RegexOptions.Compiled),
    ];

    static string ReadVendorFromBinary(string bundleFile)
    {
        var info = new FileInfo(bundleFile);

        // 每个滑窗都要能容纳"一条完整候选"，所以窗口给到 1 MiB（和 ContainsMarker 一致）。
        const int window = 1 << 20;

        // 滑窗边界的重叠：匹配不许跨窗断掉，所以每次留一个字符串上限的回头量。
        const int overlap = 128;

        byte[] buffer = new byte[window + overlap];

        using var stream = new FileStream(bundleFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);

        long position = 0;
        int carry = 0;

        while (true)
        {
            int read = stream.Read(buffer, carry, window);
            if (read <= 0)
                return string.Empty;

            var text = Encoding.ASCII.GetString(buffer, 0, carry + read);

            foreach (var pattern in sVendorPatterns)
            {
                // vendor 分组：DPF / JUCE 两条是第 1 组，最后一条是第 2 组。
                int group = pattern == sVendorPatterns[^1] ? 2 : 1;

                var match = pattern.Match(text);
                if (match.Success)
                {
                    var vendor = Strip(match.Groups[group].Value);
                    if (IsPlausibleVendor(vendor))
                        return vendor;
                }
            }

            // 已扫到文件尾，再没有下一窗
            if (read < window)
                return string.Empty;

            // 回头一段，让可能跨窗的匹配在下一次能接上
            carry = 0;
            _ = position;
        }
    }

    // 过滤明显不是厂商名的命中。厂商名是"名字"而不是"一句话"：
    //   · 不含数字与标点的正常名字（Damien Zammit / ElephantDSP.com / chowdsp）
    //   · changelog 句子几乎总有空格 + 动词，且常以 "Fixed"/"Update" 这类词开头
    // 所以：以句号结尾、含常见 changelog 词、或者以非名字词开头的，一律不要。
    // 另外有一条刻意收紧的判断：纯"名字"通常是 2-4 个词、每个词首字母大写（或全小写），
    // 而 GUID / hex id（BYOD 的 "ymct-tjb8f-..."）中间必带连字符接小写串 —— 那不是厂商。
    static bool IsPlausibleVendor(string vendor)
    {
        if (vendor.Length < 2 || vendor.Length > 40)
            return false;

        if (vendor.Any(c => !(char.IsLetterOrDigit(c) || " &'._- ".Contains(c))))
            return false;

        if (vendor.EndsWith("."))
            return false;

        // 连字符分隔的多段小写十六进制/随机串（许可密钥、GUID）不是厂商
        if (vendor.Count(c => c == '-') >= 3)
            return false;

        string[] bannedFragments =
        [
            "fixed", "update", "added", "improved", "bug", "crash", "release",
            "version", "freez", "movement", "popup", "mac os", "windows", "issue",
        ];

        foreach (var banned in bannedFragments)
            if (vendor.Contains(banned, StringComparison.OrdinalIgnoreCase))
                return false;

        return !sBanned.Any(banned => vendor.Equals(banned, StringComparison.OrdinalIgnoreCase));
    }

    // 二进制里极其常见的非厂商串（命中形态判断时也不会是厂商）。
    static readonly string[] sBanned =
    [
        "Fx", "Instrument", "Synth", "VST3", "http", "https", "Root Unit", "Factory Presets",
        "Audio Module Class", "Component Controller Class", "view", "file", "DllMain", "ExitDll",
        "asmcode", "MIDI Input", "JUCEPrivateData", "SharedData.xml", "Vendor", "Name", "Version",
        "dpf_stereo", "dpf_mono", "hs_rbj", "hs_rbj2", "host-track-name", "HPF", "UNKNOWN",
        "xMax", "chowdsp", "brummer10", "hpfreq", "hpfreq-bridge", "hp1b", "voxengo",
        "xMax", "icrd", "atom", "view", "inv", "Stereo version", "A granular delay plugin",
        "A simple mono echo plugin", "A simple mono delay plugin",
    ];

    // 有些厂商会把 URL 直接塞进 Vendor 字段；那类按 "://" 切开。
    static string Strip(string value)
    {
        value = value.Trim();

        int scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
            value = value[..scheme].Trim();

        return value;
    }
}
