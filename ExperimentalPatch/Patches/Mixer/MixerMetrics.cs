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
    public const double InputHeight = 22;    // 输入级 [IN]（Trim / Ø / HPF）
    public const double CountButtonHeight = 20;   // Send N / FX N
    public const double ToggleHeight = 22;   // M / S
    public const double ReadoutHeight = 24;
    public const double OutputHeight = 22;   // → Master  ▾

    // 推子区：[刻度][推子][电平表]。
    public const double ScaleWidth = 24;
    public const double FaderWidth = 28;
    public const double MeterWidth = 13;
    public const double MeterGap = 4;

    // send 浮窗：目标名 + 五个固定控件。名字列给 82px —— 常见 AUX 名一行放得下，
    // 再长就换行；窗口宽度按这一行算死，不留一块把名字和旋钮拉开的空白。
    // 218 = 左右 padding 20 + 5 个 4px 间隔 + 固定控件 178（48+46+42+24+18）。
    public const double SendNameWidth = 82;
    public const double SendPopupWidth = SendNameWidth + 218;

    // 插入效果浮窗的行更高：一行里有旁通/界面/删除三个键，挤在 30 里会贴边。
    // EP-MIXER-23 之后是**两条子行**（主行 + 湿/干·增益行），所以 = 40 + 24。
    public const double FxRowHeight = 64;

    // send 浮窗的一行：目标名 + 增益旋钮（下挂 dB 读数）+ PRE/POST + pan 旋钮 + M + 删除。
    // 旋钮本体 28，pan 旋钮连读数 42；行高 48 让两边的读数都完整露出。
    public const double SendRowHeight = 48;
    public const double PopupHeaderHeight = 30;
    public const double PopupPadding = 10;
}



