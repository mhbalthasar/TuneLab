using Avalonia;
using Avalonia.Media;

namespace TuneLab.ExperimentalPatch.UI;

// 宿主主题色的**镜像** —— 数值一一对应 TuneLab.GUI/GUI/Style.cs（字段名保持一致，便于逐行对账）。
//
// 为什么不直接引用 Style：它是 TuneLab.GUI 的 internal 类型（只对 TuneLab / TuneLab.Setup /
// ScreenshotBot / Tests 开放）。补丁层刻意不申请 InternalsVisibleTo——那会把整片 GUI 内部面
// 变成补丁的编译期依赖，与「补丁只认锚点契约」这条底线冲突。代价是这份副本会随主题漂移，
// 故字段名与 Style 同名，改主题色时机械对照即可。
//
// 若将来觉得这份镜像碍事，正解是在 TuneLab.GUI 上开一个**公开的调色板**（一小撮公开字段），
// 而不是给补丁层开 InternalsVisibleTo —— 前者是收窄的契约，后者是敞开的门。
//
// 这里用 public 而不是 internal：补丁外的工具（headless 外观稿 harness、将来的截图工具）要用
// 同一份颜色出图。它不在冻结面上（只有 TuneLab.SDK / TuneLab.Foundation 是），公开没有代价。
public static class Palette
{
    public static readonly Color Transparent = new(0, 0, 0, 0);
    public static readonly Color White = new(255, 255, 255, 255);
    public static readonly Color LightWhite = new(255, 181, 181, 191);
    public static readonly Color Black = new(255, 0, 0, 0);
    public static readonly Color Dark = new(255, 18, 18, 26);
    public static readonly Color Back = new(255, 27, 27, 36);
    public static readonly Color Interface = new(255, 39, 39, 54);
    public static readonly Color Line = new(51, 196, 196, 196);
    public static readonly Color Item = new(255, 58, 63, 105);
    public static readonly Color HighLight = new(255, 98, 111, 252);

    public static readonly Color TextNormal = new(178, 255, 255, 255);
    public static readonly Color TextLight = new(255, 255, 255, 255);
    public static readonly Color TextLabel = new(191, 181, 181, 191);

    public static readonly Color ButtonPrimary = new(255, 96, 96, 192);
    public static readonly Color ButtonNormal = new(255, 58, 63, 105);
    public static readonly Color ButtonPrimaryHover = new(255, 127, 127, 255);
    public static readonly Color ButtonNormalHover = new(255, 85, 92, 153);

    // 自绘标题栏的关闭键（悬浮红底 / 按下更深）。
    public static readonly Color CloseHover = new(255, 232, 17, 35);
    public static readonly Color ClosePressed = new(255, 197, 15, 31);

    // 电平表语义色（Style.AMP_*）。绿=正常、黄=偏高、红=顶到 0。
    public static readonly Color AmpNormal = new(255, 102, 255, 51);
    public static readonly Color AmpWarn = new(255, 255, 214, 51);
    public static readonly Color AmpPeak = new(255, 255, 71, 71);

    // 轨道头 M/S 开关的勾选色：这两个值在 TrackHead.cs 里是直接写死的字面量，不是 Style 字段。
    public static readonly Color MuteChecked = new(255, 0, 186, 173);
    public static readonly Color SoloChecked = new(255, 135, 84, 255);

    // AUX 条的固定色（不参与 TRACK_COLORS：AUX 不是轨道，不该跟轨道抢颜色）。
    public static readonly Color AuxBar = new(255, 124, 143, 166);

    // [EP-MIXER-26] VCA 条的固定色。用琥珀色：它与"轨道色 / AUX 钢蓝 / Master 灰"都不同，
    // 一眼能认出"这条不是信号路径，是控制组"。
    public static readonly Color VcaBar = new(255, 196, 158, 74);

    // 轨道配色（Style.TRACK_COLORS）：轨道色为空时按序号取，与轨道头的规则一致。
    public static readonly string[] TrackColors =
    [
        "#737CE5", "#73B5E5", "#73E5DB", "#73E5A2", "#7DE573", "#B5E573",
        "#E5DB73", "#E5A273", "#E5737D", "#E573B5", "#DB73E5", "#A273E5",
    ];

    public static Color DefaultTrackColor => Color.Parse(TrackColors[0]);

    public static IBrush Brush(Color color, double opacity = 1) => new SolidColorBrush(color, opacity);

    // 轨道色字符串 → 颜色。空串（= 用主题默认色）或解析失败都回落到默认色。
    public static Color TrackColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return DefaultTrackColor;

        try
        {
            return Color.Parse(hex);
        }
        catch
        {
            return DefaultTrackColor;
        }
    }
}
