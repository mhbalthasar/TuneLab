using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using OwnVST3Host;
using TuneLab.ExperimentalPatch.InsertHost;

namespace TuneLab.ExperimentalPatch.VstBridge.InProcess;

// 桥进程里的 VST3 **进程内**装配：加载 native 宿主库、把插件装进来、问描述。
//
// 【它为什么在这里】这段代码以前在 InsertHost 里 —— 也就是跑在 TuneLab 进程里。
// 插件一崩宿主就没了（实测 DPF 插件的 assert 直接 abort 整个进程）。现在宿主侧
// 只留过桥的 Vst3BridgeInsert，插件的加载 / 处理 / 参数 / 状态 / 界面全在这一个
// 独立进程里；崩了、卡死超时了都只是桥没了（VST3-BRIDGE.md）。
//
// 【.tlhost 后缀】原生库叫 ownvst3.tlhost.dll，而 vendor 的托管层写死了几个候选路径
// （它自己的 GetNativeLibraryPath 不会找到改过名的文件）。所以这里不用托管层的自动定位，
// 而是自己算出路径、显式传给 OwnVst3Wrapper(string) 构造。
internal sealed class Vst3InProcessFactory
{
    // 我们的 native dll 文件名（设计文档 §5.4 的"改名"方案，见 InProcess/Vendored/README）。
    public const string NativeBaseName = "ownvst3.tlhost";

    // 文件名要与 vendored 包装层（OwnVST3.cs 的 GetNativeLibraryName）一致：三个平台都加
    // .tlhost 后缀（Linux/macOS 是 libownvst3.tlhost.so / .dylib）。我们按显式路径加载，
    // 不依赖 soname / install name，所以改名安全，也避免和别人的 libownvst3.* 撞车。
    public static string NativeFileName =>
        OperatingSystem.IsWindows() ? NativeBaseName + ".dll" :
        OperatingSystem.IsMacOS() ? "libownvst3.tlhost.dylib" :
        "libownvst3.tlhost.so";

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

    // 只在**读文件**：桥能不能承载 VST3 由一个文件是否存在决定，探测本身不该有副作用。
    public static string? NativePath => sNativePath.Value;

    public static bool IsSupported => NativePath != null;

    static readonly Lazy<string?> sNativePath = new(FindNativeLibrary, isThreadSafe: true);

    public Vst3InProcessFactory()
    {
        // 托管层内部还有几处 DllImport（编辑器那条路）走默认解析，
        // 装上 resolver 才不会去加载没改名的那一份。
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Vst3InProcessFactory).Assembly, ResolveNative);
        }
        catch (Exception)
        {
            // 重复绑定是预期的，忽略。
        }
    }

    // 找 native dll 的规则（按优先级）：
    //   1. 本程序集同目录（桥 exe 旁边 —— 我们随桥分发的那一份）
    //   2. runtimes/<rid>/native/（NuGet 惯例，vendored 包装层也认这个布局）
    //   3. native/<rid>/（本仓库自己的分目录放法，免得 arm64/x64 的同名文件打架）
    //   4. 环境变量 TL_VST3_NATIVE（排查 / 特殊部署用）
    // 不做"去系统目录搜"：那正是我们要避免的撞车来源。
    static string? FindNativeLibrary()
    {
        // 32 位桥装不了 64 位 native：直接当"不支持 VST3"（x86 桥只发 vst2）。
        if (!Environment.Is64BitProcess)
            return null;

        string beside = Path.Combine(AppContext.BaseDirectory, NativeFileName);
        if (File.Exists(beside))
            return beside;

        string packed = Path.Combine(AppContext.BaseDirectory, "runtimes", NativeRid, "native", NativeFileName);
        if (File.Exists(packed))
            return packed;

        string perRid = Path.Combine(AppContext.BaseDirectory, "native", NativeRid, NativeFileName);
        if (File.Exists(perRid))
            return perRid;

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

    // 真正加载过的插件给出的"是不是乐器"，优先于静态标记。
    //
    // 为什么需要它：静态标记漏判 DPF 系的乐器（Cardinal Synth 就是这样）。用户第一次从菜单里
    // 选中它时，Create 会真实加载、如实拒绝（"this plugin is an instrument"），并在这里记一笔 ——
    // 同一次桥的生命周期内，后续的完整扫描据此把它标成乐器。不落盘：桥重启后由 native 描述兜底。
    static readonly ConcurrentDictionary<string, bool> sKnownInstruments = new(StringComparer.OrdinalIgnoreCase);

    public Vst3Insert? Create(InsertPluginInfo plugin, InsertSettings settings, out string? reason)
    {
        reason = null;

        string? dll = NativePath;
        if (dll == null)
        {
            reason = "VST3 host library is missing";
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
                sKnownInstruments[plugin.Locator] = true;

                reason = "this plugin is an instrument; inserts need an effect";
                wrapper.Dispose();
                return null;
            }

            // 真实加载过的答案优先于静态标记。
            sKnownInstruments[plugin.Locator] = false;

            return new Vst3Insert(plugin, wrapper, sampleRate, block, settings.Bypass);
        }
        catch (Exception ex)
        {
            reason = "unexpected error while loading the plugin: " + ex.Message;
            wrapper.Dispose();
            return null;
        }
    }

    // ── 扫描（同样只在这个进程里）──
    //
    // 完整扫描让 native 的 PluginScanner 走 Full 模式，把每个插件的**描述**问一遍
    // （不是实例化）。厂商 / 版本 / 类别 / 声道数全在里面，是官方口径。
    //
    // 为什么不用 VST3Plugin_LoadPlugin 拿厂商：那条路会 createPluginInstance，
    // 真实例化插件 —— 实测在 Blue Cat 的 Chorus/Flanger/Phaser 两两先后 load 时
    // 直接 abort 整个进程（3/3 必崩）。而 Full scan 走的是 findAllTypesForFile，
    // 只问 factory 要一份 PluginDescription，插件的代码路径根本不执行。
    //
    // 【在桥里跑】即便"只是问 factory"，那也是把插件的 dll 加载进本进程。
    // 放宿主进程里做，等于把上面那条 abort 风险留在 TuneLab 自己身上。
    public IReadOnlyList<InsertPluginInfo> ScanDeep(IProgress<float>? progress = null, Action<string>? onDetail = null)
    {
        if (!IsSupported)
            return [];

        try
        {
            var descriptors = PluginScanner.ScanAsync(PluginFormat.Vst3, ScanMode.Full,
                new ScanProgressBridge(progress, onDetail)).GetAwaiter().GetResult();

            var list = new List<InsertPluginInfo>(descriptors.Count);

            foreach (var plugin in descriptors)
            {
                bool isInstrument = plugin.IsInstrument
                    || IsInstrumentCategory(plugin.Category)
                    || (sKnownInstruments.TryGetValue(plugin.Identifier, out bool known) && known);

                list.Add(new InsertPluginInfo
                {
                    Format = "VST3",
                    PluginId = PluginIdOf(plugin),
                    Name = plugin.Name,
                    Vendor = plugin.Vendor,
                    Category = plugin.Category,
                    Locator = plugin.Identifier,

                    // Full 扫来的 isInstrument 是直接从插件元数据读的，比二进制的
                    // Instrument| 标记更可靠（Surge XT 就是标记对、native 判错的那类）。
                    IsInstrument = isInstrument,
                });
            }

            return list;
        }
        catch (Exception)
        {
            // native 库比 1.7.0 老（没有 scanner），或扫描被取消。
            // 调用方（设置页）负责提示"完整扫描不可用"，这里只保证不炸。
            return [];
        }
    }

    // 快速扫描（没有目录清单时）：扫 native 认定的标准位置。
    public IReadOnlyList<InsertPluginInfo> ScanStandard()
    {
        if (!IsSupported)
            return [];

        try
        {
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

    // 把 native 的 ScanProgress（分数 + 当前条目名）桥到我们的两个回调上。
    sealed class ScanProgressBridge(IProgress<float>? progress, Action<string>? detail) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            if (float.IsFinite(value.Fraction))
                progress?.Report(Math.Clamp(value.Fraction, 0, 1));

            if (!string.IsNullOrEmpty(value.CurrentItem))
                detail?.Invoke(value.CurrentItem);
        }
    }

    // 插件 id：classId 优先（VST3 的稳定标识），拿不到就用 bundle 名兜底。
    // 落盘的真实内容，DESIGN.md §9.5 明确"pluginId 不存路径"。
    static string PluginIdOf(PluginDescriptor plugin)
        => string.IsNullOrWhiteSpace(plugin.Identifier)
            ? plugin.Name
            : "vst3:" + plugin.Identifier;

    // VST3 的类别串（PClassInfo 的 category / subcategories）。乐器必带 Instrument
    // （形如 "Instrument|Synth"），效果器是 "Fx|..."。
    static bool IsInstrumentCategory(string? category)
        => !string.IsNullOrEmpty(category)
           && (category.Contains("Instrument", StringComparison.OrdinalIgnoreCase)
               || category.Contains("Synth", StringComparison.OrdinalIgnoreCase));
}
