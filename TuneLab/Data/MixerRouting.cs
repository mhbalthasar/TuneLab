using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.Foundation;

namespace TuneLab.Data;

// 一条路由目的地（output 或 send 的目标）。
//
// 为什么是 readonly record struct：
//   · 要进 DataStruct<T>（要求 struct）；
//   · 值相等 → "设成同一个值"不会白产生一步 undo（DataProperty.SetInfo 会去重）。
internal readonly record struct MixerRoute(MixerOutputKind Kind, string Id, string Name)
{
    public static MixerRoute Master => new(MixerOutputKind.Master, "master", "Master");
}

// 一条 send：目的地 + 自己的增益（dB）+ 推子前/后 + 声向 + 静音。
// 挂在轨或 AUX 上；加删走 DataObjectList（一步 undo），改字段走 DataProperty（一步 undo）。
internal class Send : DataObject
{
    public DataProperty<MixerRoute> Target { get; }

    // dB，-∞..+12。0 = 原样推送。
    public DataProperty<double> Gain { get; }

    // true = 推子前（pre-fader）：不受本通道推子影响。
    //
    // 为什么两种都要：post-fader（默认）拉低推子 send 也跟着小 —— 做混响尾音时
    // 一拉推子尾音就断了；pre-fader 保持独立，推子只影响直达声。侧链总线、
    // 耳机混音都依赖 pre-fader。引擎侧见 MixEdge.IsPreFader。
    public DataProperty<bool> IsPreFader { get; }

    // -1..+1，0 = 中央。send 自己的声向（与通道条那个无关）——
    // 把同一路送进两个 AUX 时，可以一个偏左一个偏右，不必拆两条轨道。
    public DataProperty<double> Pan { get; }

    // true = 这条 send 自己哑掉（不影响同通道的其他 send）。
    public DataProperty<bool> IsMute { get; }

    public Send(IDataObject parent) : base(parent)
    {
        Target = new DataStruct<MixerRoute>(this);
        Gain = new DataStruct<double>(this);
        IsPreFader = new DataStruct<bool>(this);
        Pan = new DataStruct<double>(this);
        IsMute = new DataStruct<bool>(this);
    }
}

// 一条 AUX 总线：与 Master 同构（输入缓冲 → 推子 → 分发），但有自己的名字 / 颜色位 / 增删。
// 与 Track 的区别只有两条：没有音源（不读 parts）、没有"轨道序号"这个概念。
internal class AuxBus : DataObject
{
    public string Id { get; }

    public DataProperty<string> Name { get; }
    public DataProperty<double> Gain { get; }
    public DataProperty<double> Pan { get; }
    public DataProperty<bool> IsMute { get; }
    public DataProperty<bool> IsSolo { get; }
    public DataProperty<MixerRoute> Output { get; }
    public DataObjectList<Send> Sends { get; }
    public DataObjectList<InsertSlot> Inserts { get; }
    public DataProperty<MixerPin> Pinned { get; }

    // [EP-MIXER-20] 输入级（与轨道同一套语义）。
    public DataProperty<double> TrimDb { get; }
    public DataProperty<bool> PhaseInvert { get; }
    public DataProperty<double> HpfHz { get; }

    public AuxBus(string id, string name, DataObject parent) : base(parent)
    {
        Id = id;
        Name = new DataString(this, name);
        Gain = new DataStruct<double>(this);
        Pan = new DataStruct<double>(this);
        IsMute = new DataStruct<bool>(this);
        IsSolo = new DataStruct<bool>(this);
        Output = new DataStruct<MixerRoute>(this);

        // 构造期尚未接上 document，这条命令会被丢弃、只落值（见 DataProperty.SetInfo）。
        Output.SetInfo(MixerRoute.Master);

        Sends = new DataObjectList<Send>(this);
        Inserts = new DataObjectList<InsertSlot>(this);
        Pinned = new DataStruct<MixerPin>(this);

        // 输入级：默认关闭。
        TrimDb = new DataStruct<double>(this);
        PhaseInvert = new DataStruct<bool>(this);
        HpfHz = new DataStruct<double>(this);
    }
}

// ── 插入效果（阶段二，DESIGN.md §5）──
//
// 一个槽位：挂哪个插件 + 旁通 + 插件的私有状态。
//
// 【为什么状态不走 DataProperty】VST 的 state chunk 是几百 KB 到几 MB 的**二进制**。
// DataProperty 只承载可 JSON/CBOR 序列化的标量；塞不进去。所以它单独一个 byte[] 字段，
// 由宿主在保存工程时一并读走（见 ExperimentalPatchMixerPersistence.cs）。
// 代价：改参数（插件 GUI 直接写）不逐条进 undo —— 这是多数 DAW 的做法，也是可接受的：
// 用户想撤销"加载这个插件"而不是"动了哪个滑杆"。加删槽位、旁通、换序是 undo 的。
//
// 【Id 为什么要有】工程里同一个轨道可能先后挂过两个插件、或者同一个插件挂两条轨；
// 落盘要用稳定 id 描述"第几个槽位是什么"，undo/复制才不会串。
internal class InsertSlot : DataObject
{
    public string Id { get; }

    // "VST3" / "VST2" / "JSFX"。改格式 = 换一个插件，所以它与 Id 一样构造后不变。
    public string Format { get; }

    // 插件的稳定标识（VST3 classId / VST2 uniqueId）。路径会变，它不变（DESIGN.md §9.5）。
    public DataProperty<string> PluginId { get; }

    // 显示名。插件改名 / 升级之后它不会自动跟，所以做成可写的。
    public DataProperty<string> Name { get; }

    // 旁通 = 音频直通，槽位与顺序都不变。与"从链里删掉"是两件事。
    public DataProperty<bool> IsBypassed { get; }

    // [EP-MIXER-23] 湿/干混合。1 = 全湿（默认，插件输出全部通过）；
    // 0 = 全干（插件照样跑以保持内部状态，但输出完全用干信号）。
    //
    // 为什么全干时插件仍然要跑：混响的尾巴得继续衰减。直接旁通会掐断尾巴，
    // 听感上是"突变"而不是"淡出"。
    public DataProperty<double> Mix { get; }

    // [EP-MIXER-23] 插入自身的增益微调（dB）。0 = 不变。
    // 用来把插件的电平匹配回原位，否则 A/B 对比听到的差异里混着电平差，
    // 人耳会把更响的那个当成更好的。
    public DataProperty<double> GainDb { get; }

    // 插件的私有状态（getState 的原样字节）。null = 还没存过 / 插件没报状态。
    public byte[]? State { get; set; }

    // 后端私有的定位信息（VST3 的 bundle 路径）。与 PluginId 分开存：
    // PluginId 落盘、跨机器；Locator 是本机扫描的结果，不该进工程
    // （换机器打开时路径本来就无效，重新扫一次即可，INSERT-README 的 §5.4.1）。
    public string Locator { get; set; } = string.Empty;

    public InsertSlot(string id, string format, string pluginId, string name, DataObject parent) : base(parent)
    {
        Id = id;
        Format = format;
        PluginId = new DataString(this, pluginId);
        Name = new DataString(this, name);
        IsBypassed = new DataStruct<bool>(this);

        // 默认：全湿 + 0 dB 增益 = 包装器完全透明（一次乘法都不做）。
        Mix = new DataStruct<double>(this);
        GainDb = new DataStruct<double>(this);
        Mix.SetInfo(1);
    }
}
