using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.Abstractions;
// Path 在 ImplicitUsings 下与 System.IO.Path 撞名，起个别名。
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace TuneLab.ExperimentalPatch.Patches.Splash;

// 启动闪屏：宿主在「初始化加载」期间显示的那扇窗。
//
// ── 尺寸 ──
// 对齐 Office 2003~2007 那类产品闪屏的体量（400~500 宽 × 250~300 高），取中位 480×300（16:10）。
// 只改下面两个常量即可整体缩放；布局全走相对/自动尺寸，不写死像素。
//
// ── 视觉 ──
// 配色取自 TuneLab 自己的主题（见 SplashTheme.cs）：底 = 面板色渐变到主背景色，
// 强调 = HIGH_LIGHT，文字 = TEXT_LIGHT / LIGHT_WHITE，分隔 = LINE。品牌图形直接用
// tools/icons/mark-shape.svg 的路径数据——同一个标记，故闪屏与文件图标是同一个字形。
//
// ── 时序 ──
// 宿主负责何时显示/关闭（见 .agents/startup-timeline.md）；本窗只负责长什么样、显示什么阶段。
public sealed class SplashWindow : Window, IStartupScreen
{
    // 改这两个值即可整体缩放。
    public const double BaseWidth = 480;
    public const double BaseHeight = 300;

    readonly IHostServices mHost;
    readonly TextBlock mStatus;
    readonly TextBlock mVersion;
    readonly ProgressBar mBar;
    readonly StackPanel mBrand;
    readonly StackPanel mFailureList;
    readonly ScrollViewer mFailurePanel;
    readonly Button mContinue;
    // 「已看过失败、继续启动」的信号：宿主 await 它，故用户不点就不会往下走。
    readonly TaskCompletionSource mContinueRequested = new();

    public SplashWindow(IHostServices host)
    {
        mHost = host;

        Width = BaseWidth;
        Height = BaseHeight;
        CanResize = false;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // 与宿主 ProgressWindow 同一策略：初始化期间不被别的窗口盖住。
        Topmost = true;
        Background = SplashTheme.Backdrop();

        mStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = SplashTheme.Brush(SplashTheme.LightWhite),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        mVersion = new TextBlock
        {
            FontSize = 11,
            Foreground = SplashTheme.Brush(SplashTheme.LightWhite, 0.65),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Text = string.IsNullOrWhiteSpace(host.AppVersion) ? string.Empty : "v" + host.AppVersion,
        };

        mBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Height = 4,
            IsIndeterminate = true,
            Foreground = SplashTheme.Brush(SplashTheme.HighLight),
            Background = SplashTheme.Brush(SplashTheme.Item),
            Margin = new Thickness(0, 12, 0, 0),
        };

        mBrand = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 18,
        };
        mBrand.Children.Add(new Viewbox { Width = 96, Height = 96, Child = BuildMark() });
        mBrand.Children.Add(new TextBlock
        {
            Text = "TuneLab",
            FontSize = 30,
            FontWeight = FontWeight.SemiBold,
            Foreground = SplashTheme.Brush(SplashTheme.TextLight),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        mFailureList = new StackPanel { Spacing = 6 };
        mFailurePanel = new ScrollViewer
        {
            Content = mFailureList,
            MaxHeight = 150,
            IsVisible = false,
            Margin = new Thickness(24, 20, 24, 12),
            VerticalAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        mContinue = new Button
        {
            Content = "Continue",
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            Height = 30,
            MinWidth = 96,
            Margin = new Thickness(0, 12, 0, 0),
            Background = SplashTheme.Brush(SplashTheme.ButtonPrimary),
            Foreground = SplashTheme.Brush(SplashTheme.TextLight),
            CornerRadius = new CornerRadius(6),
        };
        mContinue.Click += (_, _) =>
        {
            if (!mContinueRequested.TrySetResult())
                return;
            // 回到「正在继续」的样子，然后放行宿主。
            mContinue.IsVisible = false;
            mFailurePanel.IsVisible = false;
            mBrand.IsVisible = true;
            mBar.IsVisible = true;
        };

        Content = BuildContent();
        SetPhase(StartupPhase.Preparing);

        // 整窗可拖动：初始化可能持续几秒，用户想挪开就挪开（与 ProgressWindow 同一行为）。
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
    }

    public void SetPhase(StartupPhase phase) => mStatus.Text = PhaseText(phase);

    public Task ShowFailuresAsync(IReadOnlyList<string> failures)
    {
        mStatus.Text = L("有音源引擎未能加载", "Some sound engines failed to load");

        mFailureList.Children.Clear();
        foreach (var failure in failures)
        {
            mFailureList.Children.Add(new TextBlock
            {
                Text = "• " + failure,
                FontSize = 11,
                Foreground = SplashTheme.Brush(SplashTheme.LightWhite),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        mContinue.Content = L("继续", "Continue");
        mBrand.IsVisible = false;
        mFailurePanel.IsVisible = true;
        mBar.IsVisible = false;
        mContinue.IsVisible = true;

        return mContinueRequested.Task;
    }

    Control BuildContent()
    {
        // 顶部 3px 强调条：把闪屏与主界面的强调色绑在一起。
        var accent = new Border { Background = SplashTheme.Brush(SplashTheme.HighLight) };

        var centre = new Grid();
        centre.Children.Add(mBrand);
        centre.Children.Add(mFailurePanel);

        var statusRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(mStatus, 0);
        Grid.SetColumn(mVersion, 1);
        statusRow.Children.Add(mStatus);
        statusRow.Children.Add(mVersion);

        var footerGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        Grid.SetRow(statusRow, 0);
        Grid.SetRow(mBar, 1);
        Grid.SetRow(mContinue, 2);
        footerGrid.Children.Add(statusRow);
        footerGrid.Children.Add(mBar);
        footerGrid.Children.Add(mContinue);

        var footer = new Border
        {
            Background = SplashTheme.Brush(SplashTheme.Dark),
            BorderBrush = SplashTheme.Brush(SplashTheme.Line),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14, 20, 16),
            Child = footerGrid,
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("3,*,Auto") };
        Grid.SetRow(accent, 0);
        Grid.SetRow(centre, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(accent);
        grid.Children.Add(centre);
        grid.Children.Add(footer);

        // 1px 描边：深色桌面背景下给窗口一个明确的边缘。
        return new Border
        {
            BorderBrush = SplashTheme.Brush(SplashTheme.Line),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    // 阶段文案归闪屏自己：本层不做 i18n（免得为一句闪屏文案去动上游翻译表，见 .agents/host-seams.md 待办），
    // 但也不该由宿主写死——宿主只说"到哪个阶段了"，措辞是呈现层的事。
    string PhaseText(StartupPhase phase) => phase switch
    {
        StartupPhase.LoadingExtensions => L("正在加载扩展…", "Loading extensions…"),
        StartupPhase.InitializingEngines => L("正在初始化音源引擎…", "Initializing sound engines…"),
        StartupPhase.OpeningEditor => L("正在打开编辑器…", "Opening the editor…"),
        _ => L("正在初始化…", "Initializing…"),
    };

    string L(string zh, string en)
        => mHost.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;

    // 品牌标记：路径数据抄自 tools/icons/mark-shape.svg（1024×1024 viewBox）。
    // 上游改了那个文件，这里要跟着改——它是同一个标记的唯一真相源。
    static Control BuildMark()
    {
        var canvas = new Canvas { Width = 1024, Height = 1024 };

        // 拱形 + 两侧短柱用一个色（次级），中央的 "T" 用强调色（视觉焦点）。
        void Add(string data, IBrush fill)
            => canvas.Children.Add(new ShapePath { Data = StreamGeometry.Parse(data), Fill = fill });

        var secondary = SplashTheme.Brush(SplashTheme.LightWhite);
        Add("M284.8 384C291.52 384 294.881 384 297.448 385.308C299.706 386.458 301.542 388.294 302.692 390.552C304 393.119 304 396.48 304 403.2V524.8C304 531.52 304 534.881 302.692 537.448C301.542 539.706 299.706 541.542 297.448 542.692C294.881 544 291.52 544 284.8 544H281.6C261.438 544 251.357 544 243.656 540.076C236.882 536.625 231.375 531.118 227.924 524.344C224 516.643 224 506.562 224 486.4V441.6C224 421.438 224 411.357 227.924 403.656C231.375 396.883 236.882 391.375 243.656 387.924C251.357 384 261.438 384 281.6 384H284.8Z", secondary);
        Add("M742.4 384C762.562 384 772.643 384 780.344 387.924C787.118 391.375 792.625 396.882 796.076 403.656C800 411.357 800 421.438 800 441.6V486.4C800 506.562 800 516.643 796.076 524.344C792.625 531.118 787.118 536.625 780.344 540.076C772.643 544 762.562 544 742.4 544H739.2C732.48 544 729.119 544 726.552 542.692C724.294 541.542 722.458 539.706 721.308 537.448C720 534.881 720 531.52 720 524.8V403.2C720 396.48 720 393.119 721.308 390.552C722.458 388.294 724.294 386.458 726.552 385.308C729.119 384 732.48 384 739.2 384H742.4Z", secondary);
        Add("M512 192C644.027 192 726.575 274.204 759.551 360.075C761.031 363.929 758.138 368 754.01 368H732.458C730.009 368 727.814 366.516 726.861 364.26C696.261 291.811 624.671 224 512 224C399.329 224 327.738 291.811 297.138 364.26C296.185 366.516 293.991 368 291.542 368H269.989C265.861 368 262.968 363.929 264.448 360.075C297.424 274.204 379.973 192 512 192Z", secondary);
        Add("M668.8 416C675.52 416 678.881 416 681.448 417.308C683.706 418.458 685.542 420.294 686.692 422.552C688 425.119 688 428.48 688 435.2V492.8C688 499.52 688 502.881 686.692 505.448C685.542 507.706 683.706 509.542 681.448 510.692C678.881 512 675.52 512 668.8 512H560V812.8C560 819.521 560 822.881 558.692 825.448C557.542 827.706 555.706 829.542 553.448 830.692C550.881 832 547.52 832 540.8 832H483.2C476.48 832 473.119 832 470.552 830.692C468.294 829.542 466.458 827.706 465.308 825.448C464 822.881 464 819.52 464 812.8V512H355.2C348.48 512 345.119 512 342.552 510.692C340.294 509.542 338.458 507.706 337.308 505.448C336 502.881 336 499.52 336 492.8V435.2C336 428.48 336 425.119 337.308 422.552C338.458 420.294 340.294 418.458 342.552 417.308C345.119 416 348.48 416 355.2 416H668.8Z", SplashTheme.Brush(SplashTheme.HighLight));

        return canvas;
    }
}
