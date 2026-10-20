using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using OwnVST3Host;
using TuneLab.ExperimentalPatch.MixerEngine;
using VstInsert = TuneLab.ExperimentalPatch.InsertHost.Vst3Insert;

namespace TuneLab.ExperimentalPatch.InsertHost;

// VST3 后端：把 OwnAudioVST3（降级 vendor 在 Vendored/）接到引擎的 IMixInsert 上。
//
// 【线程分工】本类只在 UI 线程用（ListPlugins / Create）。
// 真正在音频线程上跑的是 Vst3Insert（见那个文件顶部的说明）。
// 两者的交界处只有一次"装配完成"：插件 Initialize 好、状态载入好，然后才交给音频线程。
//
// 【.tlhost 后缀】原生库叫 ownvst3.tlhost.dll，而 vendor 的托管层写死了几个候选路径
// （它自己的 GetNativeLibraryPath 不会找到改过名的文件）。所以这里不用托管层的自动定位，
// 而是自己算出路径、显式传给 OwnVst3Wrapper(string) 构造。
public sealed class Vst3Backend : IInsertBackend
{
    // 我们的 native dll 文件名（设计文档 §5.4 的"改名"方案，见 Vendored/README）。
    // native 库的文件名按平台取扩展名。目前仓库里只 vendor 了 win-x64 一份，
    // 但名字与查找路径都按平台算 —— 往 Native/<rid>/ 里放一份对应平台的二进制就能用，
    // 不需要改代码（DESIGN.md §5.4）。
    public const string NativeBaseName = "ownvst3.tlhost";

    // 文件名要与 vendored 包装层（OwnVST3.cs 的 GetNativeLibraryName）一致：
    // Windows 是重命名过的 ownvst3.tlhost.dll，Linux/macOS 用上游的 libownvst3.*。
    public static string NativeFileName =>
        OperatingSystem.IsWindows() ? NativeBaseName + ".dll" :
        OperatingSystem.IsMacOS() ? "libownvst3.dylib" :
        "libownvst3.so";

    // 运行时标识（与 .NET 的 RID 同名）：win-x64 / win-arm64 / win-x86 /
    // linux-x64 / linux-arm64 / osx-x64 / osx-arm64。
    public static string NativeRid
    {
        get
        {
            var arch = RuntimeInformation.ProcessArchitecture;

            if (OperatingSystem.IsWindows())
                return arch == Architecture.Arm64 ? "win-arm64" : arch == Architecture.X86 ? "win-x86" : "win-x64";

            if (OperatingSystem.IsMacOS())
                return arch == Architecture.Arm64 ? "osx-arm64" : "osx-x64";

            return arch == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        }
    }

    string IInsertBackend.FormatName => "VST3";

    public bool IsAvailable => NativePath != null;

    public string? UnavailableReason { get; private set; }

    string? NativePath { get; set; }

    public Vst3Backend()
    {
        NativePath = FindNativeLibrary();

        if (NativePath == null)
        {
            UnavailableReason = "VST3 host library is missing";
            return;
        }

        // 托管层内部还有几处 DllImport（编辑器那条路）走默认解析，
        // 装上 resolver 才不会去加载没改名的那一份。
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Vst3Backend).Assembly, ResolveNative);
        }
        catch (Exception)
        {
            // 重复绑定是预期的（多个后端/测试进程各装一次），忽略。
        }
    }

    // 找 native dll 的规则（按优先级）：
    //   1. 本程序集同目录（宿主 exe 旁边 —— 我们随应用分发的那一份）
    //   2. 环境变量 TL_VST3_NATIVE 指明的路径（排查 / 特殊部署用）
    // 不做"去系统目录搜"：那正是我们要避免的撞车来源。
    static string? FindNativeLibrary()
    {
        // 1. 与 exe 同目录（发布/构建输出的布局）
        string beside = Path.Combine(AppContext.BaseDirectory, NativeFileName);
        if (File.Exists(beside))
            return beside;

        // 2. runtimes/<rid>/native/（NuGet 惯例，vendored 包装层也认这个布局）
        string packed = Path.Combine(AppContext.BaseDirectory, "runtimes", NativeRid, "native", NativeFileName);
        if (File.Exists(packed))
            return packed;

        // 3. native/<rid>/（本仓库自己的分目录放法，免得 arm64/x64 的同名文件打架）
        string perRid = Path.Combine(AppContext.BaseDirectory, "native", NativeRid, NativeFileName);
        if (File.Exists(perRid))
            return perRid;

        // 3. 环境变量（开发机 / 外部注入）
        string? fromEnv = Environment.GetEnvironmentVariable("TL_VST3_NATIVE");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        return null;
    }

    static IntPtr ResolveNative(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "ownvst3.dll", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(libraryName, "ownvst3", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        string? resolved = FindNativeLibrary();
        return resolved != null ? NativeLibrary.Load(resolved) : IntPtr.Zero;
    }

    public IReadOnlyList<InsertPluginInfo> ListPlugins(IReadOnlyList<string>? directories = null,
        Action<int, int, string>? onDirectory = null, bool probeInstruments = true, Action<string>? onDetail = null)
    {
        if (NativePath == null)
            return [];

        // 上游的 PluginScanner 有个硬限制：ScanAsync **不接受目录参数**，
        // 只扫它自己认定的标准位置。而"用户自定的扫描路径清单"是本功能的硬需求
        // （编辑器设置里那份），所以这里自己走一遍目录枚举，再用
        // PluginScanner.ResolveAsync 把 bundle 补成完整描述（名字 / 厂商 / 声道数）。
        if (directories != null && directories.Count > 0)
            return ListFromDirectories(directories, onDirectory, probeInstruments, onDetail);

        try
        {
            // 没有清单时只有"一遍标准位置"，进度就报一次（1/1）。
            onDirectory?.Invoke(0, 1, "standard VST3 locations");

            List<PluginDescriptor> found;
            try
            {
                found = PluginScanner.ScanAsync().GetAwaiter().GetResult().ToList();
            }
            catch (Exception)
            {
                // 这个库里扫描器的版本可能不对（我们的 native 是 1.7.0，带扫描器；
                // 换版本时兜住）。扫不出来只当"没有插件"，不让整个菜单挂掉。
                return [];
            }

            var list = new List<InsertPluginInfo>(found.Count);
            foreach (var plugin in found)
            {
                list.Add(new InsertPluginInfo
                {
                    Format = "VST3",
                    PluginId = PluginIdOf(plugin),
                    Name = plugin.Name,
                    Vendor = plugin.Vendor,
                    Category = plugin.Category,
                    Locator = plugin.Identifier,
                    IsInstrument = IsInstrumentCategory(plugin.Category),
                });
            }

            return list;
        }
        catch (Exception)
        {
            return [];
        }
    }

    // 插件 id：classId 优先（VST3 的稳定标识），拿不到就用 bundle 名兜底。
    // 落盘的真实内容，DESIGN.md §9.5 明确"pluginId 不存路径"。
    static string PluginIdOf(PluginDescriptor plugin)
        => string.IsNullOrWhiteSpace(plugin.Identifier)
            ? plugin.Name
            : "vst3:" + plugin.Identifier;

    // VST3 的类别串（PClassInfo 的 category / subcategories）。乐器必带 Instrument
    // （形如 "Instrument|Synth"），效果器是 "Fx|..."。这条只用于**不支持目录清单**的
    // 那条扫描路径（上游扫出来的 PluginDescriptor 自带类别，不必再加载插件）。
    static bool IsInstrumentCategory(string? category)
        => !string.IsNullOrEmpty(category)
           && (category.Contains("Instrument", StringComparison.OrdinalIgnoreCase)
               || category.Contains("Synth", StringComparison.OrdinalIgnoreCase));

    // 按用户配的目录清单扫。
    //
    // 两个选择说明：
    //   ① 自己枚举 .vst3，而不是让上游扫 —— 上游那个不接受目录参数（见上）。
    //   ② 用 bundle 名做 PluginId，而不是 classId —— 拿 classId 要把插件 load 一遍，
    //      而 bundle 名在**同一台机器**上稳定，换机器重新扫就对上了。
    //      ⚠️ 代价：两个不同插件同名时会撞。取舍记在这里，别以后误以为是 bug。
    //
    // 【probeInstruments】用来判断"是不是乐器"。扫描时**不加载插件**，读二进制里的类别标记
    // （见 LooksLikeInstrument 的注释：加载会 abort 进程，也有插件 20 秒不返回）。
    // 代价是 DPF 系乐器的子类别是运行时拼的、标记里没有 —— 那些靠 Create 时的真实加载补记
    // （sKnownInstruments），用户点错一次之后就不再出现。
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

                // 判乐器：先看本进程里是否**真实加载过**（更可靠），否则读二进制的类别标记（不加载）。
                // 见 LooksLikeInstrument / sKnownInstruments 的注释。
                bool isInstrument = false;

                if (probeInstruments)
                {
                    onDetail?.Invoke(name);

                    isInstrument = sKnownInstruments.TryGetValue(bundle, out bool known)
                        ? known
                        : LooksLikeInstrument(bundle);
                }

                list.Add(new InsertPluginInfo
                {
                    Format = "VST3",
                    PluginId = "vst3:" + Path.GetFileNameWithoutExtension(bundle),
                    Name = name,
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
    // 【为什么不加载】试过：扫描时对每个 bundle 走 LoadPlugin → Initialize → 问 IsInstrument。
    // 结果是灾难性的，两条都实测：
    //   · 整个进程被带走 —— 某个 DPF 插件的 assert 直接 abort，宿主没有任何机会救（真机 66 个一起扫时复现）；
    //   · 加载本身没有上限 —— 逐个隔离跑时 66 个里有 2 个 20 秒都没返回（同样会卡住扫描）。
    // 扫描是"随手点一下"的动作，不能有这两种代价。VST2 那边之所以敢探，是因为它跑在**桥进程**里，
    // 崩了只是桥崩；VST3 是进程内加载（§5.4），代价直接落在宿主身上。
    //
    // 【标记准不准】VST3 的类别是类信息里的**子类别字符串**，编译器把它作为明文常量放进二进制
    // （"Instrument|Synth" / "Fx|Delay"）。开发机上 64 个 64 位插件与逐个真实加载对过：63 个一致，
    // 差的那一个（Surge XT，类别确实是 Instrument）反而是标记更对 —— native 的 IsInstrument 判它 false。
    // 已知漏判：DPF 系（Cardinal）的子类别是运行时拼的，二进制里没有这个字面量。
    //
    // 【宁可漏判，不可误判】判成乐器的插件会从菜单里消失。误判一个效果器（用户会以为"插件没扫到"）
    // 比漏判一个合成器糟得多，所以只有在"有 Instrument 标记、且**没有** Fx 标记"时才判乐器。
    // 漏掉的由下面那道真实加载兜住（见 sKnownInstruments）。
    internal static bool LooksLikeInstrument(string file)
    {
        try
        {
            return ContainsMarker(file, "Instrument|") && !ContainsMarker(file, "Fx|");
        }
        catch (Exception)
        {
            // 读不动（占用 / 权限）就当"不知道" —— 列出来。
            return false;
        }
    }

    // 在二进制里找一段 ASCII 或 UTF-16LE 的字面量，分块读（插件有好几百 MB 的，不能整个读进内存）。
    static bool ContainsMarker(string file, string needle)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(needle);
        byte[] utf16 = Encoding.Unicode.GetBytes(needle);

        int overlap = Math.Max(ascii.Length, utf16.Length) - 1;
        byte[] buffer = new byte[(1 << 20) + overlap];

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);

        int carry = 0;

        while (true)
        {
            int read = stream.Read(buffer, carry, buffer.Length - carry);
            if (read <= 0)
                return false;

            var chunk = buffer.AsSpan(0, carry + read);

            if (chunk.IndexOf(ascii) >= 0 || chunk.IndexOf(utf16) >= 0)
                return true;

            // 留下尾巴：标记可能正好跨在两块之间。
            carry = Math.Min(overlap, chunk.Length);
            chunk.Slice(chunk.Length - carry).CopyTo(buffer);
        }
    }

    // 真正加载过的插件给出的答案，优先于上面的静态标记。
    //
    // 为什么需要它：静态标记漏判 DPF 系的乐器（Cardinal Synth 就是这样）。用户第一次从菜单里
    // 选中它时，Create 会真实加载、如实拒绝（"this plugin is an instrument"），并在这里记一笔 ——
    // 下一次扫描它就从菜单里消失了。不落盘：这是"本进程里已经证实过的事"，
    // 重启后由静态标记兜底，代价只是可能再点错一次。
    static readonly ConcurrentDictionary<string, bool> sKnownInstruments = new(StringComparer.OrdinalIgnoreCase);


    public IMixInsert? Create(InsertPluginInfo plugin, InsertSettings settings, out string? reason)
    {
        reason = null;

        string? dll = NativePath;
        if (dll == null)
        {
            reason = "VST3 host library is missing";
            return null;
        }

        if (string.IsNullOrEmpty(plugin.Locator))
        {
            reason = "plugin location is unknown (rescan first)";
            return null;
        }

        OwnVst3Wrapper wrapper;
        try
        {
            wrapper = new OwnVst3Wrapper(dll);
        }
        catch (Exception ex)
        {
            reason = "failed to load the VST3 host library: " + ex.Message;
            return null;
        }

        try
        {
            if (!wrapper.LoadPlugin(plugin.Locator))
            {
                reason = "the plugin could not be loaded (bitness or a missing dependency)";
                wrapper.Dispose();
                return null;
            }

            double sampleRate = settings.SampleRate > 0 ? settings.SampleRate : 48000;
            int block = settings.MaxBlockSize > 0 ? settings.MaxBlockSize : 512;

            if (!wrapper.Initialize(sampleRate, block))
            {
                reason = "the plugin refused to initialise at " + sampleRate + " Hz";
                wrapper.Dispose();
                return null;
            }

            // 只看这个：是**效果器**才挂。乐器在这里毫无意义——
            // 它要收 MIDI、要自己产声音，而我们的插入点是"处理已经存在的音频"。
            //
            // 判据是 native 的 IsInstrument，不是自己的猜测。这里试过两个"更聪明"的
            // 替代品，都错了，记下来免得再走一遍：
            //   · ActualInputChannels <= 0 —— Surge XT 报 2 in / 2 out（它确实能当效果器用）；
            //   · !IsEffect —— 上游 native 的 IsEffect getter 是从 IsInstrument 复制来的
            //     （同一个函数指针），单独用它会把每个插件都判成乐器。
            if (wrapper.IsInstrument)
            {
                // 记一笔：下次扫描就不会再把它列出来了（静态标记漏掉 DPF 系乐器，见 sKnownInstruments）。
                sKnownInstruments[plugin.Locator] = true;

                reason = "this plugin is an instrument; inserts need an effect";
                wrapper.Dispose();
                return null;
            }

            // 真实加载过的答案优先于静态标记。
            sKnownInstruments[plugin.Locator] = false;

            return new VstInsert(plugin, wrapper, sampleRate, block, settings.Bypass);
        }
        catch (Exception ex)
        {
            reason = "unexpected error while loading the plugin: " + ex.Message;
            wrapper.Dispose();
            return null;
        }
    }

    // 重新定位：用户在别的机器上打开工程，插件扫描结果变了。
    // PluginId 是软的（路径会变），所以这里就是"按 PluginId 再扫一次"。
    // probeInstruments: false —— 这里只要 Locator，不为"是不是乐器"白加载每个插件。
    public InsertPluginInfo? Resolve(string pluginId)
        => ListPlugins(probeInstruments: false).FirstOrDefault(p => p.PluginId == pluginId);
}





