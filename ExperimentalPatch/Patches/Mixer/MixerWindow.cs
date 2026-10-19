using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.Patches.Mixer.Controls;
using TuneLab.ExperimentalPatch.UI;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace TuneLab.ExperimentalPatch.Patches.Mixer;

// 混音台窗口（独立工具窗口，见 DESIGN.md §6）。
//
// 【只做视图】本窗不持有任何混音状态：所有值来自 IMixerModel，用户改动立刻写回。
// 通道条由 ChannelsChanged 触发整表重建 —— 轨道数在人类尺度上很小，重建比增量 diff 不易错。
//
// 浮窗（send / FX）由本窗创建与定位：它们需要知道"锚在哪条通道的哪个按钮上"。
// 关闭 = 隐藏（宿主负责），所以订阅不挂在 Closed 上，而是由宿主在真正丢弃时调 Detach()。
public sealed class MixerWindow : Window
{
    readonly IMixerModel mModel;
    readonly IHostServices mHost;

    readonly StackPanel mStrips = new() { Orientation = Orientation.Horizontal };
    readonly ScrollViewer mScroller;
    readonly TextBlock mEmptyHint;
    readonly Border mMasterSlot = new();
    readonly ChromeButton mTopmost = new() { Text = "置顶", Width = 44, Height = 22 };
    readonly ChromeButton mAddAux = new() { Text = "+ AUX", Width = 52, Height = 22 };

    readonly List<MixerChannelStrip> mStripViews = new();
    IMixerChannel? mMasterChannel;
    MixerChannelStrip? mMasterView;
    SendPopupWindow? mSendPopup;
    FxPopupWindow? mFxPopup;

    bool mResizing;
    double mResizeStartWidth;
    double mResizeStartHeight;
    Point mResizeStartPointer;

    public MixerWindow(IMixerModel model, IHostServices host)
    {
        mModel = model;
        mHost = host;

        Title = L("混音台", "Mixer");
        Width = MixerMetrics.DefaultWidth;
        Height = MixerMetrics.DefaultHeight;
        MinWidth = MixerMetrics.MinWidth;
        MinHeight = MixerMetrics.MinHeight;
        // 与闪屏、宿主其它工具窗同一策略：自绘标题栏，免得系统标题栏把窗口割裂成两种风格。
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Brush(Palette.Back);

        mScroller = new ScrollViewer
        {
            Content = mStrips,
            Background = Palette.Brush(Palette.Back),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            // 高度不滚：条要占满可视高度（= 推子行程），只有横向滚。
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        mEmptyHint = new TextBlock
        {
            Text = L("没有打开的工程", "No project is open"),
            FontSize = 12,
            Foreground = Palette.Brush(Palette.TextLabel),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };

        Content = BuildContent();

        mModel.ChannelsChanged += Rebuild;
        mModel.MetersUpdated += PumpMeters;
        Rebuild();
    }

    // 宿主丢弃本窗时调用（关闭只是隐藏，不该在这里退订）。
    public void Detach()
    {
        mModel.ChannelsChanged -= Rebuild;
        mModel.MetersUpdated -= PumpMeters;
        ClosePopups();
    }

    Control BuildContent()
    {
        var stripsArea = new Grid();
        stripsArea.Children.Add(mScroller);
        stripsArea.Children.Add(mEmptyHint);

        mMasterSlot.BorderBrush = Palette.Brush(Palette.Line, 0.5);
        mMasterSlot.BorderThickness = new Thickness(1, 0, 0, 0);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(stripsArea, 0);
        Grid.SetColumn(mMasterSlot, 1);
        body.Children.Add(stripsArea);
        body.Children.Add(mMasterSlot);

        var header = BuildHeader();
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        grid.Children.Add(header);
        grid.Children.Add(body);

        var corners = new Grid();
        corners.Children.Add(grid);
        corners.Children.Add(BuildResizeGrip());

        return new Border
        {
            BorderBrush = Palette.Brush(Palette.Line),
            BorderThickness = new Thickness(1),
            Child = corners,
        };
    }

    Control BuildHeader()
    {
        var title = new TextBlock
        {
            Text = L("混音台", "Mixer"),
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.Brush(Palette.TextLight),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };

        mTopmost.Click += () =>
        {
            Topmost = !Topmost;
            mTopmost.IsChecked = Topmost;
        };

        mAddAux.Click += () => mModel.AddAux();

        var close = new ChromeButton
        {
            Text = "✕",
            Width = 34,
            Height = 22,
            HoverColor = Palette.CloseHover,
        };
        // 关闭 = 隐藏：窗口与轨道订阅都留着，下次打开是瞬时的。
        close.Click += Hide;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        buttons.Children.Add(mAddAux);
        buttons.Children.Add(mTopmost);
        buttons.Children.Add(close);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(title);
        row.Children.Add(buttons);

        var header = new Border
        {
            Height = MixerMetrics.HeaderHeight,
            Background = Palette.Brush(Palette.Interface),
            BorderBrush = Palette.Brush(Palette.Line, 0.5),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row,
        };

        // 拖标题栏移窗（按钮会自己吃掉按下事件，不会误触发移动）。
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        return header;
    }

    Control BuildResizeGrip()
    {
        var grip = new ShapePath
        {
            Data = StreamGeometry.Parse("M2,13 L13,2 M8,13 L13,8 M12,13 L13,12"),
            Stroke = Palette.Brush(Palette.LightWhite, 0.45),
            StrokeThickness = 1,
            Width = 15,
            Height = 15,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 3, 3),
            Cursor = new Cursor(StandardCursorType.BottomRightCorner),
        };

        // 无边框窗没有系统可拖的边角，自己算尺寸。窗口左上角不动，故指针相对窗口的位移就是尺寸增量。
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            mResizing = true;
            mResizeStartWidth = Width;
            mResizeStartHeight = Height;
            mResizeStartPointer = e.GetPosition(this);
            e.Pointer.Capture(grip);
            e.Handled = true;
        };

        grip.PointerMoved += (_, e) =>
        {
            if (!mResizing)
                return;

            var point = e.GetPosition(this);
            Width = Math.Max(MinWidth, mResizeStartWidth + point.X - mResizeStartPointer.X);
            Height = Math.Max(MinHeight, mResizeStartHeight + point.Y - mResizeStartPointer.Y);
            e.Handled = true;
        };

        grip.PointerReleased += (_, e) =>
        {
            if (!mResizing)
                return;

            mResizing = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };

        return grip;
    }

    void Rebuild()
    {
        mStrips.Children.Clear();
        mStripViews.Clear();

        int trackIndex = 0;
        int auxIndex = 0;

        foreach (var channel in mModel.Channels)
        {
            // 序号在各自域内连续：轨道 1..n，AUX 1..m（与编辑器轨道列表的序号一致）。
            string label = channel.Kind == MixerChannelKind.Aux
                ? (++auxIndex).ToString()
                : (++trackIndex).ToString();

            var strip = new MixerChannelStrip(channel, label);
            HookStrip(strip);
            mStripViews.Add(strip);
            mStrips.Children.Add(strip);
        }

        // Master 的实例会随换工程而换，所以按实例判等，变了才重建。
        if (!ReferenceEquals(mMasterChannel, mModel.Master))
        {
            mMasterChannel = mModel.Master;
            mMasterView = new MixerChannelStrip(mMasterChannel, "MASTER");
            HookStrip(mMasterView);
            mMasterSlot.Child = mMasterView;
        }

        mEmptyHint.IsVisible = mModel.Channels.Count == 0;
        ClosePopups();
    }

    void HookStrip(MixerChannelStrip strip)
    {
        strip.SendRequested += ShowSendPopup;
        strip.FxRequested += ShowFxPopup;
        strip.OutputRequested += ShowOutputMenu;
    }

    void ShowSendPopup(MixerChannelStrip strip)
    {
        mSendPopup?.Close();
        var popup = new SendPopupWindow(strip.Channel, mModel.Outputs, mHost);
        PlacePopup(popup, strip.SendAnchor);
        popup.Show();
        mSendPopup = popup;
    }

    void ShowFxPopup(MixerChannelStrip strip)
    {
        mFxPopup?.Close();
        var popup = new FxPopupWindow(strip.Channel, mModel.InsertFormats, mHost);
        PlacePopup(popup, strip.FxAnchor);
        popup.Show();
        mFxPopup = popup;
    }

    // output 菜单：一级固定四类（Aux / Master / Device / Cable），当前项打勾。
    void ShowOutputMenu(MixerChannelStrip strip)
    {
        var channel = strip.Channel;
        var flyout = new MenuFlyout();

        foreach (var (kind, header) in new (MixerOutputKind, string)[]
                 {
                     (MixerOutputKind.Aux, "Aux"),
                     (MixerOutputKind.Master, "Master"),
                     (MixerOutputKind.Device, L("设备", "Device")),
                     (MixerOutputKind.Cable, "Cable"),
                 })
        {
            var parent = new MenuItem { Header = header };
            bool any = false;

            foreach (var output in mModel.Outputs)
            {
                if (output.Kind != kind)
                    continue;

                any = true;
                var item = new MenuItem
                {
                    Header = output.Name,
                    IsEnabled = CanRoute(channel, output),
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = channel.Output.Id == output.Id,
                };
                item.Click += (_, _) =>
                {
                    channel.Output = output;
                    ClosePopups();
                };
                parent.Items.Add(item);
            }

            if (!any)
                parent.Items.Add(new MenuItem { Header = L("（无）", "(none)"), IsEnabled = false });

            parent.IsEnabled = any;
            flyout.Items.Add(parent);
        }

        flyout.ShowAt(strip.OutputAnchor);
    }

    // 不能输出到自己；Master 只能去设备 / Cable；不可用的目的地（Cable 未实现）一律禁用。
    static bool CanRoute(IMixerChannel channel, IMixerOutput output)
    {
        if (!output.IsAvailable)
            return false;

        if (channel.Kind == MixerChannelKind.Master)
            return output.Kind is MixerOutputKind.Device or MixerOutputKind.Cable;

        if (output.Kind == MixerOutputKind.Aux && output.Id == channel.Id)
            return false;

        return true;
    }

    void PlacePopup(Window popup, Control anchor)
    {
        var point = anchor.PointToScreen(new Point(0, anchor.Bounds.Height + 4));

        // 别把浮窗推出屏幕右边。
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
        {
            int right = point.X + (int)popup.Width;
            if (right > screen.Bounds.Right)
                point = new PixelPoint(Math.Max(0, screen.Bounds.Right - (int)popup.Width - 8), point.Y);
        }

        popup.Position = point;
    }

    void ClosePopups()
    {
        mSendPopup?.Close();
        mSendPopup = null;
        mFxPopup?.Close();
        mFxPopup = null;
    }

    void PumpMeters()
    {
        foreach (var strip in mStripViews)
            strip.PumpMeter();

        mMasterView?.PumpMeter();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ClosePopups();
    }

    string L(string zh, string en)
        => mHost.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
}
