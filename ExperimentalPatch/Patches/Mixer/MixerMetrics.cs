namespace TuneLab.ExperimentalPatch.Patches.Mixer;

// 混音台的尺寸常量，集中一处：布局散落的魔数最难受，也要跟 DESIGN.md §4.1 保持一致。
internal static class MixerMetrics
{
    public const double StripWidth = 84;
    public const double MasterWidth = 132;

    public const double HeaderHeight = 36;
    public const double DefaultWidth = 1000;
    public const double DefaultHeight = 560;
    public const double MinWidth = 420;
    public const double MinHeight = 380;

    // 通道条内部各段高度（自上而下）。
    public const double Pad = 8;
    public const double IndexHeight = 22;    // 轨号 + 色块
    public const double NameHeight = 20;
    public const double PanHeight = 42;      // 旋钮 + 数值
    public const double ToggleHeight = 22;
    public const double ReadoutHeight = 24;

    // 推子区：[刻度][推子][电平表]。
    public const double ScaleWidth = 24;
    public const double FaderWidth = 28;
    public const double MeterWidth = 13;
    public const double MeterGap = 4;
}
