using Avalonia;
using Avalonia.Media;

namespace TuneLab.ExperimentalPatch.Patches.Splash;

// 闪屏用到的主题色 —— **镜像** TuneLab.GUI/GUI/Style.cs，不是另发明一套。
//
// 为什么不直接引用 Style：Style 是 TuneLab.GUI 的 internal 类型（只对 TuneLab / TuneLab.Setup /
// ScreenshotBot / Tests 开放）。补丁层刻意不申请 InternalsVisibleTo——那会把整片 GUI 内部面变成
// 补丁的编译期依赖，与「补丁只认锚点契约」这条架构底线冲突。代价是这几个数值可能随主题漂移，
// 故每个数值后面注了 Style.cs 里的字段名，便于对账；改主题色时顺手同步这里。
//
// 若将来觉得这份镜像碍事，正解是在 TuneLab.GUI 上开一个**公开的调色板**（一小撮公开字段），
// 而不是给补丁层开 InternalsVisibleTo —— 前者是收窄的契约，后者是敞开的门。
//
// 【命名】叫 SplashTheme 而不是 Theme：Avalonia 的 StyledElement 上有一个 Theme 属性
// （类型 ControlTheme），在窗口类里会把同名静态类遮蔽掉，直接叫 Theme 编译不过。
internal static class SplashTheme
{
    public static readonly Color Dark = Color.FromRgb(0x12, 0x12, 0x1A);        // Style.DARK
    public static readonly Color Back = Color.FromRgb(0x1B, 0x1B, 0x24);        // Style.BACK
    public static readonly Color Interface = Color.FromRgb(0x27, 0x27, 0x36);   // Style.INTERFACE
    public static readonly Color Item = Color.FromRgb(0x3A, 0x3F, 0x69);        // Style.ITEM
    public static readonly Color HighLight = Color.FromRgb(0x62, 0x6F, 0xFC);   // Style.HIGH_LIGHT
    public static readonly Color ButtonPrimary = Color.FromRgb(0x60, 0x60, 0xC0); // Style.BUTTON_PRIMARY
    public static readonly Color LightWhite = Color.FromRgb(0xB5, 0xB5, 0xBF);  // Style.LIGHT_WHITE
    public static readonly Color TextLight = Color.FromRgb(0xFF, 0xFF, 0xFF);   // Style.TEXT_LIGHT
    public static readonly Color Line = Color.FromArgb(0x33, 0xC4, 0xC4, 0xC4); // Style.LINE

    public static IBrush Brush(Color color, double opacity = 1) => new SolidColorBrush(color, opacity);

    // 主背景：面板色在上、主背景色在下——Office 系闪屏那种「一块面板浮在背景上」的层次。
    public static IBrush Backdrop() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Interface, 0),
            new GradientStop(Back, 1),
        },
    };
}
