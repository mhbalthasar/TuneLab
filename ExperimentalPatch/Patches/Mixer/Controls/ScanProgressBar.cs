using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer.Controls;

// 一条细长的进度条（0..1）。
//
// 为什么自绘而不用 Avalonia 的 ProgressBar：Fluent 那套自带浅色轨道，放进这一页
// （配色全部取自宿主主题的镜像 Palette）会像贴上去的补丁 —— 同 ChromeButton 的理由。
//
// 只画"已完成的比例"，不画文字：进度文字由调用方放在旁边（它知道自己想说什么）。
internal sealed class ScanProgressBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ScanProgressBar, double>(nameof(Value));

    // 0..1。超界会被夹进来，调用方算比例时不必自己防。
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public ScanProgressBar()
    {
        Height = 6;
        IsHitTestVisible = false;   // 纯显示，别去抢扫过的指针
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        double radius = height / 2;

        context.DrawRectangle(Palette.Brush(Palette.Item), null, new Rect(0, 0, width, height), radius, radius);

        double filled = width * Math.Clamp(Value, 0, 1);
        if (filled <= 0)
            return;

        context.DrawRectangle(Palette.Brush(Palette.HighLight), null, new Rect(0, 0, filled, height), radius, radius);
    }
}
