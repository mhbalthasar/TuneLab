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

// 一条 send：目的地 + 自己的增益（dB，post-fader）。
// 挂在轨或 AUX 上；加删走 DataObjectList（一步 undo），改增益走 DataProperty（一步 undo）。
internal class Send : DataObject
{
    public DataProperty<MixerRoute> Target { get; }
    public DataProperty<double> Gain { get; }

    public Send(IDataObject parent) : base(parent)
    {
        Target = new DataStruct<MixerRoute>(this);
        Gain = new DataStruct<double>(this);
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
    }
}
