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
// 布局（DESIGN.md §6.1）：色条序号 / 名 / pan / Send N / FX N / M S / 推子+表 / 读数 / output。
// **三种通道共用同一套行**，好让推子在不同条上落在同一高度（比较电平的前提）。
//   - Track：全部；
//   - AUX：全部（AUX 也有 pan / M / S，只是 solo 只在 AUX 之间生效，那是模型的事）；
//   - Master：没有 pan / send / M S —— 那几行**留空**，不是画假控件。
//
// 【不持有状态】所有显示值都从 IMixerChannel 读；用户改动立刻写回。补丁侧不缓存增益或电平。
internal sealed class MixerChannelStrip : UserControl
{
    readonly IMixerChannel mChannel;
    readonly string mLabel;
    readonly bool mIsMaster;

    readonly Border mHeaderBar = new();
    readonly TextBlock mIndex = new();
    readonly TextBlock mName = new();
    readonly PanKnob mPan = new();
    readonly StripButton mSend = new();
    readonly StripButton mFx = new();
    readonly MixerToggle mMute = new() { Text = "M", CheckedColor = Palette.MuteChecked };
    readonly MixerToggle mSolo = new() { Text = "S", CheckedColor = Palette.SoloChecked };
    readonly Fader mFader = new();
    readonly StereoMeter mMeter = new();
    readonly TextBlock mReadout = new();
    readonly OutputSelector mOutput = new();
    readonly Border mSelectionBar = new() { Width = 3, Background = Palette.Brush(Palette.HighLight), IsVisible = false };

    bool mSyncing;

    public IMixerChannel Channel => mChannel;
    public Control SendAnchor => mSend;
    public Control FxAnchor => mFx;
    public Control OutputAnchor => mOutput;

    public event Action<MixerChannelStrip>? SendRequested;
    public event Action<MixerChannelStrip>? FxRequested;
    public event Action<MixerChannelStrip>? OutputRequested;

    public MixerChannelStrip(IMixerChannel channel, string label)
    {
        mChannel = channel;
        mLabel = label;
        mIsMaster = channel.Kind == MixerChannelKind.Master;

        Width = mIsMaster ? MixerMetrics.MasterWidth : MixerMetrics.StripWidth;
        Background = Palette.Brush(Palette.Dark);
        BorderBrush = Palette.Brush(Palette.Line, 0.35);
        BorderThickness = new Thickness(0, 0, 1, 0);

        Content = BuildContent();
        HookEvents();
        Refresh();
    }

    Control BuildContent()
    {
        var rows = new Grid
        {
            // 固定行合计 192；* 给推子。三种通道行数一致 → 推子对齐。
            RowDefinitions = new RowDefinitions(
                $"{MixerMetrics.IndexHeight},{MixerMetrics.NameHeight},{MixerMetrics.PanHeight}," +
                $"{MixerMetrics.CountButtonHeight},{MixerMetrics.CountButtonHeight},{MixerMetrics.ToggleHeight}," +
                $"*,{MixerMetrics.ReadoutHeight},{MixerMetrics.OutputHeight}"),
        };

        int row = 0;
        rows.Children.Add(Place(BuildHeader(), row++));
        rows.Children.Add(Place(BuildName(), row++));
        rows.Children.Add(Place(BuildPan(), row++));
        rows.Children.Add(Place(BuildSendButton(), row++));
        rows.Children.Add(Place(BuildFxButton(), row++));
        rows.Children.Add(Place(BuildToggles(), row++));
        rows.Children.Add(Place(BuildFader(), row++));
        rows.Children.Add(Place(BuildReadout(), row++));
        rows.Children.Add(Place(BuildOutput(), row));

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

    Control BuildHeader()
    {
        mIndex.Text = mIsMaster ? "MASTER" : mLabel;
        mIndex.FontSize = mIsMaster ? 11 : 11;
        mIndex.FontWeight = mIsMaster ? FontWeight.SemiBold : FontWeight.Normal;
        mIndex.Foreground = Palette.Brush(mIsMaster ? Palette.TextLight : Palette.Black);
        mIndex.HorizontalAlignment = HorizontalAlignment.Center;
        mIndex.VerticalAlignment = VerticalAlignment.Center;

        mHeaderBar.Height = MixerMetrics.IndexHeight;
        mHeaderBar.Child = mIndex;
        return mHeaderBar;
    }

    Control BuildName()
    {
        if (mIsMaster)
            return new Control();

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
        if (mIsMaster)
            return new Control();

        mPan.HorizontalAlignment = HorizontalAlignment.Center;
        mPan.VerticalAlignment = VerticalAlignment.Center;
        mPan.Width = 42;
        mPan.Height = MixerMetrics.PanHeight;
        return mPan;
    }

    Control BuildSendButton()
    {
        if (mIsMaster)
            return new Control();

        return ConfigureCountButton(mSend, () => SendRequested?.Invoke(this));
    }

    Control BuildFxButton()
    {
        return ConfigureCountButton(mFx, () => FxRequested?.Invoke(this));
    }

    Control ConfigureCountButton(StripButton button, Action clicked)
    {
        button.Height = MixerMetrics.CountButtonHeight;
        button.Margin = new Thickness(MixerMetrics.Pad - 2, 1, MixerMetrics.Pad - 2, 1);
        button.Click += clicked;
        return button;
    }

    Control BuildToggles()
    {
        if (mIsMaster)
            return new Control();

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
        mReadout.FontSize = 11;
        mReadout.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
        mReadout.Foreground = Palette.Brush(Palette.TextLight);
        mReadout.HorizontalAlignment = HorizontalAlignment.Center;
        mReadout.VerticalAlignment = VerticalAlignment.Center;
        return mReadout;
    }

    Control BuildOutput()
    {
        mOutput.Height = MixerMetrics.OutputHeight;
        mOutput.Margin = new Thickness(MixerMetrics.Pad - 3, 2, MixerMetrics.Pad - 3, 4);
        mOutput.Click += () => OutputRequested?.Invoke(this);
        return mOutput;
    }

    void HookEvents()
    {
        mChannel.StateChanged += Refresh;
        mChannel.RoutingChanged += Refresh;

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
            var kind = mChannel.Kind;
            mHeaderBar.Background = Palette.Brush(kind switch
            {
                MixerChannelKind.Master => Palette.Interface,
                MixerChannelKind.Aux => Palette.AuxBar,
                _ => Palette.TrackColor(mChannel.ColorHex),
            });

            if (!mIsMaster)
            {
                mName.Text = mChannel.Name;
                mPan.Value = mChannel.Pan;
                mMute.IsChecked = mChannel.IsMute;
                mSolo.IsChecked = mChannel.IsSolo;

                int sends = mChannel.Sends.Count;
                mSend.Text = $"Send {sends}";
                mSend.IsActive = sends > 0;
            }

            int inserts = mChannel.Inserts.Count;
            mFx.Text = $"FX {inserts}";
            mFx.IsActive = inserts > 0;

            mFader.Value = mChannel.GainDb;
            mReadout.Text = Fader.FormatDb(mChannel.GainDb);

            mOutput.Text = mChannel.Output.ShortName;
            mOutput.IsDirect = mChannel.Output.Kind == MixerOutputKind.Device;

            mSelectionBar.IsVisible = mChannel.IsSelected;
            Background = Palette.Brush(mChannel.IsSelected ? Palette.Interface : Palette.Dark);
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
        mChannel.RoutingChanged -= Refresh;
    }
}
