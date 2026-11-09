namespace TuneLab.ExperimentalPatch.InsertHost;

// 宿主侧的"插件参数"统一形状。
//
// 【为什么不是 vendored 的 VST3Parameter】VST3 的进程内实现（ownvst3 包装层）已经从
// patch 本体搬到桥工程里了：宿主进程里不再有任何插件代码。宿主仍要画参数面板，
// 所以这里留一个**纯数据**类型，由过桥的 Vst3BridgeInsert 填。
//
// 字段名与 vendored 的那个一一对应（Id/Name/MinValue/MaxValue/DefaultValue/CurrentValue），
// 桥侧改起来只是换一个类型名，界面代码一行不用动。
public sealed class InsertParameter
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public double MinValue { get; set; }

    public double MaxValue { get; set; }

    public double DefaultValue { get; set; }

    public double CurrentValue { get; set; }

    // [EP-MIXER-AUTO] true = 选择型参数（枚举 / 步进）：不进插入参数自动化。
    // 缺省 false = 线性；VST3 的 vendored 包装层拿不到 stepCount、VST2 没有参数元数据，
    // 两者暂报 false（见桥协议 ParameterInfo.IsDiscrete 的注释）。
    public bool IsDiscrete { get; set; }
}
