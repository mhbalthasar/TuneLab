using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.Foundation;
using HostLog = TuneLab.Foundation.Log;

namespace TuneLab.Audio;

// 插入效果器的**实例工厂**（EP-MIXER-14）。
//
// 【它在整条链里的位置】契约与后端在 InsertHost，数据（InsertSlot）在 Data/，
// 编译进混音图在 ExperimentalPatchMixerAudio.cs —— 这一个类负责中间那一环：
//   数据槽位 ──建实例──> IMixInsert ──编进──> MixNode.Inserts
//
// 【为什么实例不放在槽位上】InsertSlot 是 DataObject，会被 undo 序列化 / 复制 / 丢弃；
// 而一个 VST3 实例是原生句柄 + 几块 pinned 内存。跟着 undo 走的对象不可以持有它们
// （undo 撤销到一半，实例没了，图里还指着它）。
// 所以实例由这里按"数据快照"现建现销毁：数据变 → 重建图 → 新实例；数据恢复 → 老实例没了。
//
// 【生命周期】实例从建出来那一刻起就归这里管，直到：
//   · 编译时用了它（交给图的 MixNode.Inserts）—— 之后由图持有，换快照时旧的随 GC 回收；
//   · 编译失败 / 被替换 —— 这里负责 Dispose，不能让原生句柄漏。
internal sealed class InsertInstanceFactory
{
    readonly Dictionary<string, IMixInsert> mLive = [];

    // [EP-MIXER-23] 链上那一环（湿/干 / 增益微调包装后的）。与 mLive 分开：
    // mLive 面向"插件本身"，这张表面向"图里跑的那一环"。
    readonly Dictionary<string, object> mWrapped = [];

    // [VST3-BRIDGE] 每个槽位**最近一次建实例失败**的原因（插件没装 / 隔离加载超时 / 桥崩 /
    // 插件拒绝 state…）。界面据此把那一行标红并把原因放进 tooltip —— 早先失败只写日志，
    // 用户看到的是"这个效果器没反应"，不知道是插件的问题还是宿主的问题。
    //
    // 生命期与 mLive 对齐：建成功就清、Release 就清。锁是因为编图可能发生在
    // 工程加载线程上，而读它的界面在 UI 线程。
    readonly Dictionary<string, string> mFailures = [];
    readonly object mFailuresLock = new();

    readonly Vst3Backend mVst3 = new();
    readonly Vst2Backend mVst2 = new();
    readonly ClapBackend mVstClap = new();
    readonly JsfxBackend mJsfx = new();

    // 宿主当前的采样率 / 块长。插件的 Initialize 要它们，所以必须在建实例前知道。
    // 由 MixerGraphCompiler.Compile 每次编译时按当前图刷新（见那里的注释：这两个值曾长期停在默认值上，
    // 造成"每块只处理一半"的爆音与"44.1k 工程用 48k 插件"的音高错误）。
    public double SampleRate { get; set; } = 48000;

    // 声明给插件的最大块长。要与引擎的块长上限一致（Mixer.DefaultMaxBlockSize）。
    public int MaxBlockSize { get; set; } = Mixer.DefaultMaxBlockSize;

    // 用户配的 VST3 扫描路径（宿主启动时从 InsertPluginSettings 注入）。
    // 为什么不让工厂自己去读设置：设置是"本机配置"，工厂该拿现成的值，
    // 而不是反过来去翻配置文件 —— 那样单测就没法固定输入了。
    public IReadOnlyList<string> Vst3Paths { get; set; } = InsertPluginSettings.DefaultVst3Paths;

    // 用户配的 VST2 扫描路径（与 VST3 分开：老插件目录常常是另一棵树）。
    public IReadOnlyList<string> Vst2Paths { get; set; } = InsertPluginSettings.DefaultVst2Paths;

    // 用户配的 CLAP 扫描路径（默认 = 三平台的标准位置，见 ClapPaths）。
    public IReadOnlyList<string> ClapPaths { get; set; } = InsertPluginSettings.DefaultClapPaths;

    // 用户配的 JSFX 扫描路径（TuneLab Effects + REAPER Effects + 用户自定义）。
    public IReadOnlyList<string> JsfxPaths { get; set; } = InsertPluginSettings.DefaultJsfxPaths;

    // 当前可用的格式（菜单里的第一级）。VST3 可用性取决于隔离宿主（64 位桥）在不在，
    // VST2 取决于桥可执行文件在不在（Windows-only），CLAP / JSFX 同样取决于桥。
    public IReadOnlyList<IInsertBackend> Backends => new IInsertBackend[] { mVst3, mVst2, mVstClap, mJsfx };

    // 图侧（MixerGraphCompiler）与工程侧（MixerPersistence 落盘）共用**同一个**工厂：
    // 插件实例里存着用户的参数与 state，各建一份就会"图里在跑、落盘是空的"。
    public static InsertInstanceFactory Shared { get; } = new();

    public IReadOnlyList<InsertPluginEntry> ListPlugins()
    {
        var list = new List<InsertPluginEntry>();

        // 扫描缓存由宿主设置（InsertPluginSettings）持有；这里只把它翻成契约形状。
        // 缓存是"这台机器装了什么"，工厂是"能装进来什么"—— 两个关注点分开。
        return list;
    }

    /// <summary>
    /// 按数据槽位建一个实例。失败返回 null（并把原因写进 reason）——
    /// 调用方据此把那条轨上的槽位标红 + 旁通，**绝不静默丢链**（DESIGN.md §5.6）。
    /// </summary>
    public IMixInsert? Create(InsertSlot slot, out string? reason)
    {
        reason = null;

        // 已经建过（换图但数据没变）：直接复用实例，包括它已载入的插件状态。
        // 重建一份会让"改了路由之后插件参数回默认值"——那是个真会挨骂的 bug；
        // 更糟的是插件的编辑器还挂在旧实例上，用户调的是"没人在听的"那一份。
        //
        // 换插件走 SetPlugin，它会先 Release；开工程走 ReleaseAll。所以"存在即复用"是安全的。
        if (mLive.TryGetValue(slot.Id, out var existing))
        {
            // 有活实例 = 这个槽位没失败过（失败的根本不会进 mLive）。
            ClearFailure(slot.Id);
            return existing;
        }

        var backend = Resolve(slot.Format);
        if (backend == null || !backend.IsAvailable)
            return Fail(slot.Id, backend?.UnavailableReason ?? $"unknown insert format '{slot.Format}'", out reason);

        var plugin = FindPlugin(backend, slot.PluginId.Value);
        if (plugin == null)
        {
            // 扫描缓存里没有、但槽位上自己存着路径：同一台机器上重开工程常是这样
            // （缓存被清过而插件还在老地方）。按存的那份建一个临时描述。
            if (!string.IsNullOrEmpty(slot.Locator))
            {
                plugin = new InsertPluginInfo
                {
                    Format = slot.Format,
                    PluginId = slot.PluginId.Value,
                    Name = slot.Name.Value,
                    Locator = slot.Locator,
                };
            }
            else
            {
                return Fail(slot.Id,
                    $"the plugin '{slot.Name.Value}' is not installed any more (id {slot.PluginId.Value})",
                    out reason);
            }
        }

        var settings = new InsertSettings
        {
            Bypass = slot.IsBypassed.Value,
            SampleRate = SampleRate,
            MaxBlockSize = MaxBlockSize,
        };

        var insert = backend.Create(plugin, settings, out reason);
        if (insert == null)
            return Fail(slot.Id, reason, out reason);

        // 落盘的插件状态在这里灌回去。灌失败（版本不兼容、插件换了）不致命：
        // 插件照常用它的默认参数，只记一条日志。
        if (slot.State != null && slot.State.Length > 0)
        {
            bool loaded = insert switch
            {
                IParameterInsert vst => vst.LoadState(slot.State),
                Vst2BridgeInsert vst2 => vst2.LoadState(slot.State),
                _ => true,
            };

            if (!loaded)
                HostLog.Warning($"insert: plugin '{slot.Name.Value}' refused the saved state; using its defaults");
        }

        ClearFailure(slot.Id);
        mLive[slot.Id] = insert;
        return insert;
    }

    // 建实例失败：把原因记进 mFailures 并把同一个原因交给调用方（返回 null 给 Create）。
    IMixInsert? Fail(string slotId, string? message, out string? reason)
    {
        reason = string.IsNullOrEmpty(message) ? "the insert could not be created" : message;

        lock (mFailuresLock)
            mFailures[slotId] = reason;

        return null;
    }

    void ClearFailure(string slotId)
    {
        lock (mFailuresLock)
            mFailures.Remove(slotId);
    }

    /// <summary>
    /// 某个槽位最近一次失败的原因；null = 正常（或还没编图）。
    /// 界面据此把那一行标红 + 把原因放进 tooltip。
    ///
    /// [P4] 两个来源合流：建实例失败（mFailures）优先；没有的话再看活着的实例是不是
    /// 在运行期停了（音频挂起）。对界面来说都是"这一行坏了"，不必区分。
    /// </summary>
    public string? FailureOf(string slotId)
    {
        lock (mFailuresLock)
            if (mFailures.TryGetValue(slotId, out var reason))
                return reason;

        if (mLive.TryGetValue(slotId, out var insert)
            && insert is IRuntimeInsertFailure { RuntimeFailureReason: { Length: > 0 } runtime })
            return runtime;

        return null;
    }

    // 单测用：把一个假实例塞进 mLive，覆盖"活实例后来在运行期停了"这条路径，
    // 不必起真插件 / 真桥（与 Vst3BridgeInsert.CreateForTest 同一套纪律）。
    internal void RegisterLiveForTest(string slotId, IMixInsert insert)
        => mLive[slotId] = insert;

    /// <summary>把某个槽位的实例状态读出来（保存工程时调）。</summary>
    public byte[]? CaptureState(InsertSlot slot)
    {
        if (mLive.TryGetValue(slot.Id, out var insert))
        {
            var state = insert switch
            {
                IParameterInsert vst => vst.SaveState(),
                Vst2BridgeInsert vst2 => vst2.SaveState(),
                _ => null,
            };

            if (state != null && state.Length > 0)
                return state;
        }

        return slot.State;
    }

    /// <summary>取某个槽位正在跑的实例（界面要开编辑器 / 读旁通时用）。</summary>
    public bool TryGetLive(string slotId, out IMixInsert? insert)
        => mLive.TryGetValue(slotId, out insert);

    /// <summary>旁通是活值：直接写到正在跑的实例上，不必等下一次编图。</summary>
    public void SetBypassed(string slotId, bool bypassed)
    {
        if (mLive.TryGetValue(slotId, out var insert))
            insert.SetBypassed(bypassed);
    }

    // [EP-MIXER-23] 湿/干是活值：写进图里那个包装器。
    //
    // 【为什么另建一张表】`mLive` 存的是工厂造出的**裸插件**（编辑器、参数面板、
    // 旁通都要用它），而湿/干包装器是编译期包在外面的那一层，只活在混音图里。
    // 两个表分开：一个面向"插件本身"，一个面向"链上那一环"。
    //
    // 【查不到怎么办】静默返回。编译还没发生过（工程刚打开、还没播过）时包装器
    // 不存在，这完全正常 —— 下一次编图会用数据槽位里的当前值建包装器。
    public void SetMix(string slotId, double mix)
    {
        if (mWrapped.TryGetValue(slotId, out var wrapper) && wrapper is WetDryInsert wet)
            wet.Mix = (float)mix;
    }

    public void SetGainDb(string slotId, double gainDb)
    {
        if (mWrapped.TryGetValue(slotId, out var wrapper) && wrapper is TrimInsert trim)
            trim.GainDb = gainDb;
    }

    /// <summary>编译期把链上那一环登记进来（湿/干 / 增益微调要能推给它）。</summary>
    public void RegisterWrapped(string slotId, IMixInsert wrapped)
    {
        mWrapped[slotId] = wrapped;
    }

    /// <summary>槽位被图放弃了：包装器一起丢掉（下一次编图会重建）。</summary>
    public void UnregisterWrapped(string slotId)
    {
        mWrapped.Remove(slotId);
    }

    /// <summary>某个槽位的实例被图放弃了：先关编辑器，再放掉原生句柄。</summary>
    public void Release(string slotId)
    {
        // 编辑器窗口里挂的是插件自己的 view：必须在 Dispose（effClose / FreeLibrary）之前关掉。
        InsertEditorHost.Close(slotId);

        // 槽位被释放了，失败标记也跟着走（重加同一个槽位是新的 id）。
        ClearFailure(slotId);

        // [EP-MIXER-AUTO] 自动化登记同样随槽位走（下一次编译按数据重建）。
        mAutomations.Remove(slotId);

        if (!mLive.Remove(slotId, out var insert))
            return;

        try
        {
            (insert as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            HostLog.Warning("insert: failed to dispose a plugin instance: " + ex.Message);
        }
    }

    public void ReleaseAll()
    {
        InsertEditorHost.CloseAll();

        foreach (var id in mLive.Keys.ToArray())
            Release(id);
    }

    // ── [EP-MIXER-AUTO] 参数自动化登记与"面板改基值"的接管 ──

    // 编译期登记：slotId → (参数 id → 冻结绑定的宿主侧实现)。绑定的曲线快照自己会跟着
    // 数据换（曲线编辑不必重建图），这张表只回答"这个槽位的这个参数有没有被自动化接管"。
    readonly Dictionary<string, Dictionary<long, MixerAutomation.Binding>> mAutomations = [];

    internal void RegisterAutomations(string slotId, IReadOnlyList<MixerAutomation.Binding> bindings)
    {
        var map = new Dictionary<long, MixerAutomation.Binding>(bindings.Count);

        foreach (var binding in bindings)
            map[long.Parse(binding.Entry.ParamId)] = binding;

        mAutomations[slotId] = map;
    }

    internal bool IsParameterAutomated(string slotId, long paramId)
        => mAutomations.TryGetValue(slotId, out var map) && map.ContainsKey(paramId);

    // 自动化接管期间的参数面板写入：value 解释为**基值**（叠加关系见 InsertSlotAutomation），
    // 并立刻把有效值（基值 + 当前曲线偏移，钳位）推一次 —— 停止时没有"下一块"，不能等。
    // 返回 false = 该参数没有被自动化接管，调用方走原来的直写路径。
    public bool TryWriteAutomatedParameter(string slotId, long paramId, double value)
    {
        if (!mAutomations.TryGetValue(slotId, out var map) || !map.TryGetValue(paramId, out var binding))
            return false;

        binding.Entry.BaseValue.Set(value);

        double tick = AutomationTickProvider?.Invoke() ?? 0;
        binding.Push(binding.Evaluate(tick));
        return true;
    }

    // 自动化接管期间的一次"拖基值"= 一步 undo：面板在滑杆的按下 / 松开处调用这一对。
    // 没有被接管的槽位返回 no-op 作用域（调用方不用区分）。
    public IDisposable BeginParameterEdit(string slotId)
    {
        if (!mAutomations.TryGetValue(slotId, out var map) || map.Count == 0)
            return new NoOpScope();

        var entry = map.Values.First().Entry;
        entry.BeginMergeNotify();
        return new MergeCommitScope(entry);
    }

    sealed class NoOpScope : IDisposable
    {
        public void Dispose() { }
    }

    // merge 作用域收口：EndMergeNotify（合并期间的变化归并成一步）+ Commit（推进撤销栈）。
    // 与 MixerChannelStrip 推子那一路的 EditSession 同一形状。
    sealed class MergeCommitScope(DataObject data) : IDisposable
    {
        public void Dispose()
        {
            data.EndMergeNotify();
            data.Commit();
        }
    }

    // "当前播放位置在乐谱上的 tick"。由宿主在工程装载 / tempo 变化时设置
    //（与 Mixer.TimeMapper 同一处接线）；null = 没有工程上下文，按 tick 0 处理。
    public static Func<double>? AutomationTickProvider { get; set; }

    // ── 内部 ──

    IInsertBackend? Resolve(string format)
        => Backends.FirstOrDefault(b => string.Equals(b.FormatName, format, StringComparison.OrdinalIgnoreCase));

    // 扫描缓存（由宿主注入）：PluginId → Locator 的**权威来源**。
    //
    // 【为什么需要它】Locator（插件文件的真实路径）按设计**不落盘**（换机器就失效）。
    // 于是重开工程时槽位上只有 PluginId，得重新定位。只按"目录清单再枚举一遍"定位会漏：
    // 用户把插件放在自定义目录、而工厂手里的目录还是默认值时，枚举不到 ——
    // 日志里就是 "the plugin 'X' is not installed any more"，槽位变占位旁通，
    // 「界面」键自然永远是灰的（实测 ZamDynamicEQ / DragonflyHallReverb 就是这样）。
    // 缓存是**菜单用的同一份清单**，按它找就不会与用户看到的不一致。
    public static Func<IReadOnlyList<InsertPluginEntry>>? PluginCacheProvider { get; set; }

    // 按 PluginId 在扫描缓存里找。缓存可能比工程旧（用户卸了插件）——
    // 找不到就如实说""找不到了""，不猜一个相似的。
    InsertPluginInfo? FindPlugin(IInsertBackend backend, string pluginId)
        => FindPlugin(backend, pluginId, PluginCacheProvider?.Invoke());

    // internal 而不是 private：TuneLab.Tests 要覆盖"旧路径型 id → 新 64 位缓存"的解析，
    // 而这条逻辑没有别的稳定入口；显式传缓存也避免测试去改全局 PluginCacheProvider。
    // 程序集本身 internal，这不是公开契约。
    internal InsertPluginInfo? FindPlugin(IInsertBackend backend, string pluginId,
        IReadOnlyList<InsertPluginEntry>? cached)
    {
        // 1) 先查扫描缓存（有 Locator，且与菜单同源）。
        if (cached != null)
        {
            var candidates = cached
                .Where(entry => string.Equals(entry.Format, backend.FormatName, StringComparison.OrdinalIgnoreCase))
                .Select(ToPluginInfo)
                .Where(plugin => Matches(plugin, pluginId))
                .ToList();

            if (candidates.Count > 0)
                return ChooseBest(candidates, pluginId);
        }

        // 2) 缓存里没有（从没扫过 / 缓存被清）→ 退回按目录枚举。
        // 每个格式有自己的一份路径清单（VST2 与 VST3 的老插件目录通常不是同一棵树）。
        //
        // CLAP 例外：它的"扫描"必须把每个 .clap 加载进桥问描述（没有静态元数据），
        // 而这段代码在**每次编译音频图**时都会跑。为一个槽位把几十个插件过一遍桥
        // 等于每次改路由都冻住好几秒 —— 所以这里不做，交给槽位上存的 Locator
        // （ClapBackend.MakeLocator 把"文件 + pluginId"一起存进去了）。
        if (string.Equals(backend.FormatName, ClapBackend.FormatName, StringComparison.OrdinalIgnoreCase))
            return null;

        var paths = string.Equals(backend.FormatName, Vst2Backend.FormatName, StringComparison.OrdinalIgnoreCase)
            ? Vst2Paths
            : string.Equals(backend.FormatName, JsfxBackend.FormatName, StringComparison.OrdinalIgnoreCase)
                ? JsfxPaths
                : Vst3Paths;

        // probeInstruments: false —— 这里只要 Locator。true 会把每个插件都加载一遍，
        // 而这段代码在**每次编译音频图**时都会跑（挂一个效果器就编译一次），
        // 让它去加载几百个插件等于把 UI 冻住好几秒。
        var scanned = backend.ListPlugins(paths, probeInstruments: false)
            .Where(plugin => Matches(plugin, pluginId))
            .ToList();

        return scanned.Count > 0 ? ChooseBest(scanned, pluginId) : null;
    }

    static InsertPluginInfo ToPluginInfo(InsertPluginEntry entry) => new()
    {
        Format = entry.Format,
        PluginId = entry.PluginId,
        Name = entry.Name,
        Vendor = entry.Vendor,
        Category = entry.Category,
        Locator = entry.Locator,
        IsInstrument = entry.IsInstrument,
        IsAvailable = entry.IsAvailable,
        UnavailableReason = entry.UnavailableReason,
    };

    // VST3 的工程 id 可能是 bundle 名（vst3:X），也可能是完整扫描写下的路径型 id
    // （vst3:C:\...\X.vst3）。两种都要能找回同一条缓存，否则老工程一开就是"插件没了"。
    // 同时按 bundle 名匹配，让 64 位副本可以接管只写了短 id 的工程。
    static bool Matches(InsertPluginInfo plugin, string pluginId)
    {
        if (string.Equals(plugin.PluginId, pluginId, StringComparison.Ordinal))
            return true;

        if (!string.Equals(plugin.Format, "VST3", StringComparison.OrdinalIgnoreCase))
            return false;

        string wanted = Vst3Key(pluginId);

        if (wanted.Length == 0)
            return false;

        return string.Equals(Vst3Key(plugin.PluginId), wanted, StringComparison.OrdinalIgnoreCase)
               || string.Equals(Vst3Key(plugin.Locator), wanted, StringComparison.OrdinalIgnoreCase)
               || string.Equals(plugin.Name, wanted, StringComparison.OrdinalIgnoreCase);
    }

    // 同名的 64/32 位副本：永远先选 64 位 —— x64 桥的 LoadLibrary 碰不了 x86 bundle。
    // 只有 32 位副本时仍返回它，让 Vst3Backend.Create 给出明确原因，而不是含糊的"插件没反应"。
    static InsertPluginInfo ChooseBest(IReadOnlyList<InsertPluginInfo> candidates, string pluginId)
        => candidates
            .OrderBy(plugin => plugin.Format == "VST3" && Vst3Backend.Is32BitBundle(plugin.Locator))
            .ThenByDescending(plugin => plugin.IsAvailable)
            .ThenByDescending(plugin => string.Equals(plugin.PluginId, pluginId, StringComparison.Ordinal))
            .First();

    static string Vst3Key(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string text = value.StartsWith("vst3:", StringComparison.OrdinalIgnoreCase) ? value[5..] : value;
        text = text.Replace('/', '\\');

        int at = text.IndexOf(".vst3\\", StringComparison.OrdinalIgnoreCase);

        if (at >= 0)
            text = text[..(at + ".vst3".Length)];

        return Path.GetFileNameWithoutExtension(text);
    }
}

/// <summary>宿主侧的插入视图（把 InsertSlot 翻成契约 IMixerInsert）。</summary>
internal sealed class InsertSlotAdapter(InsertSlot slot, InsertInstanceFactory factory, Action? onContentChanged = null)
    : IMixerInsert, IInsertPluginTarget
{
    // 宿主侧要用：RemoveInsert 靠它把实例与数据槽位对上。
    public InsertSlot Slot => slot;

    public string SlotId => slot.Id;

    public string Name => slot.Name.Value;

    public string FormatName => slot.Format;

    // [VST3-BRIDGE] 给界面一个可显示的原因（那一行标红 + tooltip）；null = 正常（或还没编图）。
    // 建实例失败（插件没装 / 隔离加载超时 / 桥崩）与**运行期音频挂起**（P4）都走这里。
    public string? FailureReason => factory.FailureOf(slot.Id);

    public bool IsBypassed
    {
        get => slot.IsBypassed.Value;
        set
        {
            if (slot.IsBypassed.Value == value)
                return;

            // 数据（undo / 落盘）+ 正在跑的实例（立刻听见）两边都要写。
            // 只写数据的话，旁通要等下一次结构变化才生效 —— 用户按下去就是"没反应"。
            slot.IsBypassed.Set(value);
            factory.SetBypassed(slot.Id, value);
            Changed?.Invoke();
        }
    }

    // [EP-MIXER-23] 湿/干与增益微调。与旁通不同：这两个是**每块现读**的活值，
    // 不需要重建图 —— 写进数据槽位（undo / 落盘）就够了，音频线程下一块就看到。
    // 前提是编译期建的包装器还活着，而它确实活着：包装器包着插件实例，
    // 实例由工厂持有，与图同寿命。
    public double Mix
    {
        get => slot.Mix.Value;
        set
        {
            slot.Mix.Set(value);

            // 活值：直接推到正在跑的包装器上，不必等下一次编图。
            factory.SetMix(slot.Id, value);
            Changed?.Invoke();
        }
    }

    public double GainDb
    {
        get => slot.GainDb.Value;
        set
        {
            slot.GainDb.Set(value);
            factory.SetGainDb(slot.Id, value);
            Changed?.Invoke();
        }
    }

    // 「界面」键是否可点：问**正在跑的实例**。没有实例（插件建失败 / 还没编图）就是 false。
    public bool HasEditor
        => factory.TryGetLive(slot.Id, out var insert)
           && insert switch
           {
               IParameterInsert vst3 => vst3.HasEditor,
               Vst2BridgeInsert vst2 => vst2.HasEditor,
               _ => false,
           };

    public void OpenEditor()
    {
        if (!factory.TryGetLive(slot.Id, out var insert) || insert == null)
        {
            HostLog.Warning($"insert: '{slot.Name.Value}' has no live instance; cannot open its editor");
            return;
        }

        InsertEditorHost.Show(slot.Id, slot.Name.Value, insert);
    }

    // 有活实例就认为"可以问参数"：VST3 的 Parameters 是本地调用，VST2 的走桥但只在
    // 真正打开面板时才查。这里不做往返，免得"建行"这种热路径被 IO 拖住。
    public bool HasParameters => factory.TryGetLive(slot.Id, out var insert) && insert != null;

    // [EP-MIXER-27] 侧链能力问**活实例**（与 HasEditor 同一套纪律）。
    // 工程刚打开、图还没编译的那一瞬间是 0 —— 菜单里不显示 SC，编译完就正常。
    public int InputChannels
        => factory.TryGetLive(slot.Id, out var insert) && insert != null ? insert.InputChannels : 0;

    public int SidechainChannels
        => factory.TryGetLive(slot.Id, out var insert) && insert != null ? insert.SidechainChannels : 0;

    public string? SidechainSourceId
        => slot.SidechainId.Value is { Length: > 0 } id ? id : null;

    public void SetSidechainSource(string? sourceId)
    {
        string value = sourceId ?? string.Empty;

        if (slot.SidechainId.Value == value)
            return;

        // 结构变化（图里多/少一条侧链边）→ 必须重建图，不像 mix/trim 那样是活值。
        slot.SidechainId.Set(value);
        onContentChanged?.Invoke();
        Changed?.Invoke();
    }

    public void OpenParameters(string context)
    {
        if (!factory.TryGetLive(slot.Id, out var insert) || insert == null)
        {
            HostLog.Warning($"insert: '{slot.Name.Value}' has no live instance; cannot show its parameters");
            return;
        }

        // context 由界面拼（「轨道名 — FX 名」）。缺了也照开，只是抬头退化成插件名。
        string title = string.IsNullOrWhiteSpace(context) ? slot.Name.Value : context;

        InsertEditorHost.ShowParameters(slot.Id, title, insert);
    }

    public event Action? Changed;

    // 绑定到扫描缓存里选中的那个插件（IInsertPluginTarget）。
    // 写的是数据槽位，所以 undo、落盘、重建图都免费跟上。
    public void SetPlugin(string pluginId, string name, string locator)
    {
        if (slot.PluginId.Value == pluginId || factory == null)
            return;

        slot.PluginId.Set(pluginId);
        slot.Name.Set(name);
        slot.Locator = locator;

        // 换了插件：老实例不能再用了（它绑的是旧插件），放掉，
        // 下一次编译图时会按新 pluginId 建一个新的。
        factory.Release(slot.Id);

        // 成员没变、内容变了：MembershipModified 不会响。
        // 这条**必须**重建图（onContentChanged），否则新选的插件根本不会进链 ——
        // 加槽位那次编译时 PluginId 还是空的，之后再没人编过图。
        onContentChanged?.Invoke();
        Changed?.Invoke();
    }
}







