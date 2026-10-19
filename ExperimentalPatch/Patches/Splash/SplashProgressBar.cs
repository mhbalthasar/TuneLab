using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace TuneLab.ExperimentalPatch.Patches.Splash;

// 启动闪屏底部的进度条。
//
// 行为：条**越长 = 进度越高**，同时条槽内有一道**持续从左到右滚动**的高光；进度到 1 时条正好铺满
// 整个条槽（槽不再露出）、高光停住——那就是"加载完成"的样子。
//
// 为什么不直接用 Avalonia 的 ProgressBar：它只有两种形态——确定态（一根按 Value 变长的实条）与
// 不确定态（一段来回弹的条），没有"变长 + 条内滚动高光"这个组合。而闪屏的阶段进度是**粗粒度**的
// （音频 / 扩展 / 引擎 / 建窗口，每个阶段要好几秒），光靠条变长会在阶段中间僵住，必须靠内滚高光
// 把"还在动"表达出来，两者是叠加的。
public sealed class SplashProgressBar : Control
{
    // 目标进度 0..1。外部只设目标值，内部平滑推过去——直接跳变在阶段边界上会显得突兀。
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<SplashProgressBar, double>(nameof(Progress));

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public const double BarHeight = 5;

    const double SweepSeconds = 1.4;   // 高光扫过一趟的时长
    const double EaseTau = 0.22;       // 进度追目标的平滑时间常数（秒）
    const double MinShineWidth = 28;

    double mCurrent;                   // 当前显示的进度（平滑后的）
    double mSweep;                     // 高光位置 0..1
    DateTime mLastTick;
    DispatcherTimer? mTimer;

    public SplashProgressBar()
    {
        Height = BarHeight;
        // 按住闪屏任何一处都应能拖动窗口，所以本控件不吃指针事件。
        IsHitTestVisible = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (mTimer != null)
            return;

        mLastTick = DateTime.UtcNow;
        mTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        mTimer.Tick += OnTick;
        mTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        mTimer?.Stop();
        mTimer = null;
    }

    void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - mLastTick).TotalSeconds;
        mLastTick = now;

        if (dt <= 0)
            return;

        // 掉帧时（比如刚干完一段重活）别一次跳太远。
        if (dt > 0.25)
            dt = 0.25;

        var target = Math.Clamp(Progress, 0, 1);
        mCurrent += (target - mCurrent) * (1 - Math.Exp(-dt / EaseTau));

        // 收敛后贴死，免得永远差那么一点点、永远不"走满"。
        if (Math.Abs(target - mCurrent) < 0.002)
            mCurrent = target;

        mSweep += dt / SweepSeconds;
        if (mSweep >= 1)
            mSweep -= Math.Floor(mSweep);

        // 走满之后是静止画面，没必要再按帧重绘。
        if (mCurrent >= 1 && target >= 1)
            mTimer?.Stop();

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        var radius = height / 2;
        context.DrawRectangle(TrackBrush, null, new Rect(0, 0, width, height), radius, radius);

        var fillWidth = width * mCurrent;
        if (fillWidth <= 0)
            return;

        var fill = new Rect(0, 0, fillWidth, height);
        context.DrawRectangle(FillBrush, null, fill, radius, radius);

        // 高光只画到"还没走完"为止：走完就是一根实心满条，那正是加载完成的静止样子。
        if (mCurrent >= 1)
            return;

        var shineWidth = Math.Max(MinShineWidth, fillWidth * 0.35);
        var shineX = -shineWidth + (fillWidth + shineWidth) * mSweep;

        // 裁在已填充范围内：高光和"条的长度"才是同一件事，而不是在整个槽里乱跑。
        using (context.PushClip(new RoundedRect(fill, radius)))
        {
            context.FillRectangle(ShineBrush, new Rect(shineX, 0, shineWidth, height));
        }
    }

    // 静态缓存：颜色固定，别按帧新建画刷。
    static readonly IBrush TrackBrush = SplashTheme.Brush(SplashTheme.Item);
    static readonly IBrush FillBrush = SplashTheme.Brush(SplashTheme.HighLight);

    // 一道软边高光（透明 → 白 → 透明），比硬边色块更像"光扫过"。
    static readonly IBrush ShineBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromArgb(0x7A, 0xFF, 0xFF, 0xFF), 0.5),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1),
        },
    };
}
