using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer.Controls;

// 双通道电平表（L / R 两根竖条）。
//
// 这是对宿主 StereoAmplitudeViewer / AmplitudeViewer（TuneLab.GUI，internal）的**行为镜像**：
// 同样的量程（−86.8..+6 dB）、同样的快起慢落（瞬时起、48 dB/s 落）、同样的峰值保持（1 s，之后 24 dB/s 落）、
// 同样的绿→黄→红渐变锚点（−12 / −3 / 0 dB）。这样同一个工程里，调音台的表与轨道头的表读数一致——
// 两个表各说各话比没有表更糟。
internal sealed class StereoMeter : Control
{
    // 量程：−48..+6 dB。
    // 宿主 AmplitudeViewer 用的是 −86.8..+6 —— 那个量程是为轨道头那根 12px 高的表调的；
    // 调音台的表有 300~400px 高，照搬会让 −20 dB 以下全是死区。这里按 DESIGN.md §4.5 列的
    // 刻度（+6 / 0 / −6 / −12 / −24 / −48）取 −48 为底。**语义没变**（都是 dB 峰值，静音/未 solo 时返回 null），
    // 变的只是同一 dB 值画多高 —— 数值读数两边仍然一致。
    const double MinDb = -48.0;
    const double MaxDb = 6.0;

    const double WarnDb = -12.0;
    const double WarnPeakDb = -3.0;
    const double PeakDb = 0.0;

    const double BarReleaseDbPerSec = 48.0;
    const double PeakHoldSeconds = 1.0;
    const double PeakReleaseDbPerSec = 24.0;
    const double MaxFrameSeconds = 0.1;

    public const double ChannelGap = 2;
    // 通道条里的紧凑档（两根 4px）；Master 条更宽，用 Bounds.Width 摊开两根柱子。
    public const double CompactChannelWidth = 4;
    public const double CompactWidth = CompactChannelWidth * 2 + ChannelGap;

    double mTargetDb = MinDb;
    double mBarDb = MinDb;
    double mPeakHoldDb = MinDb;
    double mPeakHoldTimer;

    readonly System.Diagnostics.Stopwatch mClock = new();
    DispatcherTimer? mTimer;

    IBrush? mCachedBrush;

    // 传 null = 此刻听不到（无信号 / 被静音 / 有 solo 且本轨未 solo）：表缓慢落到底。
    public void SetValue((double LeftDb, double RightDb)? peak)
    {
        if (peak == null)
        {
            Release();
            return;
        }

        SetAmplitude(peak.Value.LeftDb, peak.Value.RightDb);
    }

    // 硬归零（还没播过、或停止播放时用）。
    public void Reset()
    {
        mTargetDb = MinDb;
        mBarDb = MinDb;
        mPeakHoldDb = MinDb;
        mPeakHoldTimer = 0;
        mTimer?.Stop();
        InvalidateVisual();
    }

    // 目标降到底：让当前读数自然落回去，而不是"啪"地清掉。
    public void Release()
    {
        mTargetDb = MinDb;
        EnsureRunning();
    }

    void SetAmplitude(double leftDb, double rightDb)
    {
        // 取两通道较大的一个驱动显示：柱子是两根、但"顶到哪儿"由更响的那边决定。
        // （宿主是两条独立 viewer、各自一个 dB；这里一根控件画两根，故取 max。
        //  代价是两边的回落速度统一——在调音台上看不出差别。）
        double db = Math.Max(leftDb, rightDb);
        double v = double.IsNaN(db) ? MinDb : Math.Max(db, MinDb);
        mTargetDb = v;

        if (v > mBarDb)
            mBarDb = v;   // 瞬时起振

        if (mBarDb >= mPeakHoldDb)
        {
            mPeakHoldDb = mBarDb;
            mPeakHoldTimer = PeakHoldSeconds;
        }

        EnsureRunning();
        InvalidateVisual();
    }

    void EnsureRunning()
    {
        if (mTimer == null)
        {
            mTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            mTimer.Tick += OnTick;
        }

        if (!mTimer.IsEnabled)
        {
            mClock.Restart();
            mTimer.Start();
        }
    }

    void OnTick(object? sender, EventArgs e)
    {
        double dt = Math.Min(mClock.Elapsed.TotalSeconds, MaxFrameSeconds);
        mClock.Restart();

        if (mTargetDb > mBarDb)
            mBarDb = mTargetDb;
        else if (mBarDb > mTargetDb)
            mBarDb = Math.Max(mTargetDb, mBarDb - BarReleaseDbPerSec * dt);

        if (mBarDb >= mPeakHoldDb)
        {
            mPeakHoldDb = mBarDb;
            mPeakHoldTimer = PeakHoldSeconds;
        }
        else
        {
            mPeakHoldTimer -= dt;
            if (mPeakHoldTimer <= 0)
                mPeakHoldDb = Math.Max(mBarDb, mPeakHoldDb - PeakReleaseDbPerSec * dt);
        }

        InvalidateVisual();

        if (mBarDb <= MinDb && mPeakHoldDb <= MinDb)
            mTimer?.Stop();
    }

    static double Percent(double db) => Math.Clamp((db - MinDb) / (MaxDb - MinDb), 0, 1);

    IBrush MeterBrush(double height)
    {
        // 渐变的锚点与宿主一致；按控件高度缓存（同一高度不重建）。
        if (mCachedBrush != null)
            return mCachedBrush;

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, height, RelativeUnit.Absolute),   // 底
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),         // 顶
        };
        brush.GradientStops.Add(new GradientStop(Palette.AmpNormal, 0));
        brush.GradientStops.Add(new GradientStop(Palette.AmpNormal, Percent(WarnDb)));
        brush.GradientStops.Add(new GradientStop(Palette.AmpWarn, Percent(WarnPeakDb)));
        brush.GradientStops.Add(new GradientStop(Palette.AmpPeak, Percent(PeakDb)));
        brush.GradientStops.Add(new GradientStop(Palette.AmpPeak, 1));

        mCachedBrush = brush;
        return brush;
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        double level = Percent(mBarDb);
        var brush = MeterBrush(height);
        double channelWidth = Math.Max(1, (width - ChannelGap) / 2);

        for (int channel = 0; channel < 2; channel++)
        {
            double x = channel * (channelWidth + ChannelGap);
            var column = new Rect(x, 0, channelWidth, height);

            context.FillRectangle(Palette.Brush(Palette.Back), column);

            if (level > 0)
                context.FillRectangle(brush, new Rect(x, height * (1 - level), channelWidth, height * level));
        }

        // 峰值保持线：到 0 dB 以上时变红——那是"顶了"，不是"偏高"。
        if (mPeakHoldDb > MinDb)
        {
            double y = height * (1 - Percent(mPeakHoldDb));
            var color = mPeakHoldDb >= PeakDb ? Palette.AmpPeak : Palette.LightWhite;
            var pen = new Pen(Palette.Brush(color), 1);
            context.DrawLine(pen, new Point(0, y), new Point(width, y));
        }
    }
}
