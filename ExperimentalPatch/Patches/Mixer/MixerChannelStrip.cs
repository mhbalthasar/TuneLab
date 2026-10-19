using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.Patches.Mixer.Controls;
using TuneLab.ExperimentalPatch.UI;

namespace TuneLab.ExperimentalPatch.Patches.Mixer;

// 一条通道条 —— 混音台的主要视觉单元。
//
// 自上而下（与 DESIGN.md §4.2 一致）：轨号色块 / 轨名 / pan / M S / 推子 + 电平表 / dB 读数。
// Master 走同一条渲染路径，只是省掉 pan 与 M/S —— 宿主没有「总输出静音」这个状态（ExportMaster
// 只看 Track.IsMute/IsSolo），画一个按不响的按钮是撒谎。
//
// 【不持有状态】所有显示值都从 IMixerChannel 读；用户改动立刻写回。补丁侧不缓存增益或电平，
// 否则编辑器那边的改动会让这里与现实脱节。
internal sealed class MixerChannelStrip : UserControl
{
    readonly IMixerChannel mChannel;
    readonly bool mIsMaster;

    readonly Border mHeaderBar = new();
    readonly TextBlock mIndex = new();
    readonly TextBlock mName = new();
    readonly PanKnob mPan = new();
    readonly MixerToggle mMute = new() { Text = "M", CheckedColor = Palette.MuteChecked };
    readonly MixerToggle mSolo = new() { Text = "S", CheckedColor = Palette.SoloChecked };
    readonly Fader mFader = new();
    readonly StereoMeter mMeter = new();
    readonly TextBlock mReadout = new();
    readonly Border mSelectionBar = new() { Width = 3, Background = Palette.Brush(Palette.HighLight), IsVisible = false };

    bool mSyncing;

    public MixerChannelStrip(IMixerChannel channel, int index, bool isMaster)
    {
        mChannel = channel;
        mIsMaster = isMaster;

        Width = isMaster ? MixerMetrics.MasterWidth : MixerMetrics.StripWidth;
        Background = Palette.Brush(Palette.Dark);
        BorderBrush = Palette.Brush(Palette.Line, 0.35);
        BorderThickness = new Thickness(0, 0, 1, 0);

        Content = BuildContent(index);
        HookEvents();
        Refresh();
    }

    Control BuildContent(int index)
    {
        var rows = new Grid
        {
            RowDefinitions = mIsMaster
                ? new RowDefinitions("Auto,*,Auto")                      // 头 / 推子 / 读数
                : new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"),      // 头 / 名 / pan / M S / 推子 / 读数
        };
        int row = 0;

        rows.Children.Add(Place(BuildHeader(index), row++));

        if (!mIsMaster)
        {
            rows.Children.Add(Place(BuildName(), row++));
            rows.Children.Add(Place(BuildPan(), row++));
            rows.Children.Add(Place(BuildToggles(), row++));
        }

        rows.Children.Add(Place(BuildFader(), row++));
        rows.Children.Add(Place(BuildReadout(), row));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(mSelectionBar, 0);
        Grid.SetColumn(rows, 1);
        grid.Children.Add(mSelectionBar);
        grid.Children.Add(rows);
        return grid;
    }

    static Control Place(Control control, int row)
    {
        Grid.SetRow(control, row);
        return control;
    }

    Control BuildHeader(int index)
    {
        if (mIsMaster)
        {
            mIndex.Text = "MASTER";
            mIndex.FontSize = 11;
            mIndex.FontWeight = FontWeight.SemiBold;
            mIndex.Foreground = Palette.Brush(Palette.TextLight);
            mIndex.HorizontalAlignment = HorizontalAlignment.Center;
            mIndex.VerticalAlignment = VerticalAlignment.Center;
            return new Border { Height = MixerMetrics.IndexHeight, Child = mIndex };
        }

        // 轨号压在轨道色条上（黑字压亮色），与轨道头的序号 chip 同一读法：
        // 调音台第 3 条 = 轨道头第 3 条，颜色也是同一个。
        mIndex.Text = (index + 1).ToString();
        mIndex.FontSize = 11;
        mIndex.Foreground = Palette.Brush(Palette.Black);
        mIndex.HorizontalAlignment = HorizontalAlignment.Center;
        mIndex.VerticalAlignment = VerticalAlignment.Center;

        mHeaderBar.Background = Palette.Brush(Palette.DefaultTrackColor);

        mHeaderBar.Height = MixerMetrics.IndexHeight;
        mHeaderBar.Child = mIndex;
        return mHeaderBar;
    }

    Control BuildName()
    {
        mName.Height = MixerMetrics.NameHeight;
        mName.FontSize = 11;
        mName.Foreground = Palette.Brush(Palette.TextNormal);
        mName.HorizontalAlignment = HorizontalAlignment.Center;
        mName.VerticalAlignment = VerticalAlignment.Center;
        mName.TextTrimming = TextTrimming.CharacterEllipsis;
        mName.Margin = new Thickness(4, 0);
        return mName;
    }

    Control BuildPan()
    {
        mPan.HorizontalAlignment = HorizontalAlignment.Center;
        mPan.VerticalAlignment = VerticalAlignment.Center;
        mPan.Width = 42;
        mPan.Height = MixerMetrics.PanHeight;
        return mPan;
    }

    Control BuildToggles()
    {
        mMute.Width = 26;
        mMute.Height = 18;
        mSolo.Width = 26;
        mSolo.Height = 18;

        var panel = new StackPanel
        {
            Height = MixerMetrics.ToggleHeight,
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(mMute);
        panel.Children.Add(mSolo);
        return panel;
    }

    Control BuildFader()
    {
        mFader.VerticalAlignment = VerticalAlignment.Stretch;

        mMeter.VerticalAlignment = VerticalAlignment.Stretch;
        mMeter.Width = mIsMaster ? 26 : StereoMeter.CompactWidth;
        mMeter.Margin = new Thickness(2, 0, 0, 0);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(2, 8, MixerMetrics.Pad - 2, 4),
        };

        Grid.SetColumn(mFader, 0);
        Grid.SetColumn(mMeter, 1);
        grid.Children.Add(mFader);
        grid.Children.Add(mMeter);
        return grid;
    }

    Control BuildReadout()
    {
        mReadout.Height = MixerMetrics.ReadoutHeight;
        mReadout.FontSize = 11;
        mReadout.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
        mReadout.Foreground = Palette.Brush(Palette.TextLight);
        mReadout.HorizontalAlignment = HorizontalAlignment.Center;
        mReadout.VerticalAlignment = VerticalAlignment.Center;
        return mReadout;
    }

    void HookEvents()
    {
        mChannel.StateChanged += Refresh;

        mFader.ValueEdited += value =>
        {
            if (mSyncing)
                return;

            mChannel.GainDb = value;
            mReadout.Text = Fader.FormatDb(value);
        };

        mPan.PanEdited += value =>
        {
            if (!mSyncing)
                mChannel.Pan = value;
        };

        mMute.Toggled += value =>
        {
            if (!mSyncing)
                mChannel.IsMute = value;
        };

        mSolo.Toggled += value =>
        {
            if (!mSyncing)
                mChannel.IsSolo = value;
        };
    }

    // 电平由窗口在 MetersUpdated 时统一推过来（一次事件 + 拉取，见 IMixerModel）。
    public void PumpMeter()
    {
        mMeter.SetValue(mChannel.Peak);
    }

    // 静态占位（还没播过、或工程刚装载）：把表硬清零，别留上一次的残影。
    public void ResetMeter()
    {
        mMeter.Reset();
    }

    void Refresh()
    {
        mSyncing = true;
        try
        {
            if (!mIsMaster)
            {
                mName.Text = mChannel.Name;
                mHeaderBar.Background = Palette.Brush(Palette.TrackColor(mChannel.ColorHex));
                mPan.Value = mChannel.Pan;
                mMute.IsChecked = mChannel.IsMute;
                mSolo.IsChecked = mChannel.IsSolo;
                mSelectionBar.IsVisible = mChannel.IsSelected;
                Background = Palette.Brush(mChannel.IsSelected ? Palette.Interface : Palette.Dark);
            }

            mFader.Value = mChannel.GainDb;
            mReadout.Text = Fader.FormatDb(mChannel.GainDb);
        }
        finally
        {
            mSyncing = false;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        mChannel.StateChanged -= Refresh;
    }
}
