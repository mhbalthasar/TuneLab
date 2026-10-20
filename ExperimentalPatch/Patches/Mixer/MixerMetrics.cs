namespace TuneLab.ExperimentalPatch.Patches.Mixer;

// 混音台的尺寸常量，集中一处：布局散落的魔数最难受，也要跟 DESIGN.md §6.1 保持一致。
internal static class MixerMetrics
{
    public const double StripWidth = 84;
    public const double MasterWidth = 132;

    public const double HeaderHeight = 36;
    public const double DefaultWidth = 1180;
    public const double DefaultHeight = 660;
    public const double MinWidth = 420;
    public const double MinHeight = 420;

    // 通道条内部各段高度（自上而下）。固定行合计 192。
    public const double Pad = 8;
    public const double IndexHeight = 22;    // 轨号 / AUX 名（压在色条上）
    public const double NameHeight = 20;
    public const double PanHeight = 42;      // 旋钮 + 数值
    public const double CountButtonHeight = 20;   // Send N / FX N
    public const double ToggleHeight = 22;   // M / S
    public const double ReadoutHeight = 24;
    public const double OutputHeight = 22;   // → Master  ▾

    // 推子区：[刻度][推子][电平表]。
    public const double ScaleWidth = 24;
    public const double FaderWidth = 28;
    public const double MeterWidth = 13;
    public const double MeterGap = 4;

    // 浮窗（send / FX / output 菜单）。
    public const double PopupWidth = 380;
    public const double PopupRowHeight = 30;
    public const double PopupHeaderHeight = 30;
    public const double PopupPadding = 10;
}

