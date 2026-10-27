using System;
using System.Collections.Generic;
using System.Linq;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.MixerEngine;
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
    readonly Vst3Backend mVst3 = new();
    readonly Vst2Backend mVst2 = new();

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

    // 当前可用的格式（菜单里的第一级）。VST3 可用性取决于原生库在不在，
    // VST2 取决于桥可执行文件在不在（Windows-only）。
    public IReadOnlyList<IInsertBackend> Backends => new IInsertBackend[] { mVst3, mVst2 };

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
            return existing;

        var backend = Resolve(slot.Format);
        if (backend == null || !backend.IsAvailable)
        {
            reason = backend?.UnavailableReason ?? $"unknown insert format '{slot.Format}'";
            return null;
        }

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
                reason = $"the plugin '{slot.Name.Value}' is not installed any more (id {slot.PluginId.Value})";
                return null;
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
            return null;

        // 落盘的插件状态在这里灌回去。灌失败（版本不兼容、插件换了）不致命：
        // 插件照常用它的默认参数，只记一条日志。
        if (slot.State != null && slot.State.Length > 0)
        {
            bool loaded = insert switch
            {
                Vst3Insert vst => vst.LoadState(slot.State),
                Vst2Insert vst2 => vst2.LoadState(slot.State),
                _ => true,
            };

            if (!loaded)
                HostLog.Warning($"insert: plugin '{slot.Name.Value}' refused the saved state; using its defaults");
        }

        mLive[slot.Id] = insert;
        return insert;
    }

    /// <summary>把某个槽位的实例状态读出来（保存工程时调）。</summary>
    public byte[]? CaptureState(InsertSlot slot)
    {
        if (mLive.TryGetValue(slot.Id, out var insert))
        {
            var state = insert switch
            {
                Vst3Insert vst => vst.SaveState(),
                Vst2Insert vst2 => vst2.SaveState(),
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
    {
        // 1) 先查扫描缓存（有 Locator，且与菜单同源）。
        var cached = PluginCacheProvider?.Invoke();

        if (cached != null)
        {
            foreach (var entry in cached)
            {
                if (!string.Equals(entry.Format, backend.FormatName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.Equals(entry.PluginId, pluginId, StringComparison.Ordinal))
                    continue;

                return new InsertPluginInfo
                {
                    Format = entry.Format,
                    PluginId = entry.PluginId,
                    Name = entry.Name,
                    Vendor = entry.Vendor,
                    Category = entry.Category,
                    Locator = entry.Locator,
                    IsInstrument = entry.IsInstrument,
                };
            }
        }

        // 2) 缓存里没有（从没扫过 / 缓存被清）→ 退回按目录枚举。
        // 每个格式有自己的一份路径清单（VST2 与 VST3 的老插件目录通常不是同一棵树）。
        var paths = string.Equals(backend.FormatName, Vst2Backend.FormatName, StringComparison.OrdinalIgnoreCase)
            ? Vst2Paths
            : Vst3Paths;

        // probeInstruments: false —— 这里只要 Locator。true 会把每个插件都加载一遍，
        // 而这段代码在**每次编译音频图**时都会跑（挂一个效果器就编译一次），
        // 让它去加载几百个插件等于把 UI 冻住好几秒。
        foreach (var plugin in backend.ListPlugins(paths, probeInstruments: false))
        {
            if (plugin.PluginId == pluginId)
                return plugin;
        }

        return null;
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
               Vst3Insert vst3 => vst3.HasEditor,
               Vst2Insert vst2 => vst2.HasEditor,
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







