using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
    ResizeEdge mResizeEdge;
    double mStartWidth;
    double mStartHeight;
    PixelPoint mStartPosition;
    PixelPoint mStartScreen;

    enum ResizeEdge
    {
        Left,
        Right,
        Bottom,
        BottomLeft,
        BottomRight,
    }

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
        LoadGeometry();

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
        corners.Children.Add(BuildResizeHandles());

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

        // 诚实标注：路由（AUX / output / send）目前只活在内存里，存盘不带走。
        // 不写这一句，用户会以为工程里已经有了——那是最糟的一种"看起来成功"。
        var persistenceHint = new TextBlock
        {
            Text = L("路由暂不随工程保存", "Routing is not saved with the project yet"),
            FontSize = 10,
            Foreground = Palette.Brush(Palette.TextLabel, 0.6),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleRow.Children.Add(title);
        titleRow.Children.Add(persistenceHint);

        mTopmost.Click += () =>
        {
            Topmost = !Topmost;
            mTopmost.IsChecked = Topmost;
        };

        mAddAux.Click += () => mModel.AddAux();
        // 宿主还没接 AUX（阶段一）→ 直接不画这个按钮，而不是画一个点了没反应的。
        mAddAux.IsVisible = mModel.SupportsAux;

        var close = new ChromeButton
        {
            Text = "✕",
            Width = 34,
            Height = 22,
            HoverColor = Palette.CloseHover,
        };
        // 关闭 = 隐藏：窗口与轨道订阅都留着，下次打开是瞬时的；顺手记住尺寸。
        close.Click += () =>
        {
            SaveGeometry();
            Hide();
        };

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
        Grid.SetColumn(titleRow, 0);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(titleRow);
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

    // 无边框窗没有系统可拖的边角，自己铺一层透明命中区：
    // 左右两条边（宽 5）、下边（高 5）、两个下角（14×14），再叠一个**不可命中**的可视把手。
    // 之前的实现只有一个 15×15 的 Path —— Path 只有描边能命中，那条 1px 斜线几乎拖不到，
    // 所以"窗口能调大小"在手上等于不存在。
    Control BuildResizeHandles()
    {
        var layer = new Grid();

        layer.Children.Add(Edge(ResizeEdge.Left, HorizontalAlignment.Left, VerticalAlignment.Stretch,
            5, double.NaN, StandardCursorType.SizeWestEast));
        layer.Children.Add(Edge(ResizeEdge.Right, HorizontalAlignment.Right, VerticalAlignment.Stretch,
            5, double.NaN, StandardCursorType.SizeWestEast));
        layer.Children.Add(Edge(ResizeEdge.Bottom, HorizontalAlignment.Stretch, VerticalAlignment.Bottom,
            double.NaN, 5, StandardCursorType.SizeNorthSouth));
        layer.Children.Add(Edge(ResizeEdge.BottomLeft, HorizontalAlignment.Left, VerticalAlignment.Bottom,
            14, 14, StandardCursorType.BottomLeftCorner));
        layer.Children.Add(Edge(ResizeEdge.BottomRight, HorizontalAlignment.Right, VerticalAlignment.Bottom,
            14, 14, StandardCursorType.BottomRightCorner));

        layer.Children.Add(new ShapePath
        {
            Data = StreamGeometry.Parse("M2,13 L13,2 M8,13 L13,8 M12,13 L13,12"),
            Stroke = Palette.Brush(Palette.LightWhite, 0.45),
            StrokeThickness = 1,
            Width = 15,
            Height = 15,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 3, 3),
            IsHitTestVisible = false,
        });

        return layer;
    }

    Control Edge(ResizeEdge edge, HorizontalAlignment horizontal, VerticalAlignment vertical,
        double width, double height, StandardCursorType cursor)
    {
        var border = new Border
        {
            // 必须给透明背景：null 背景的控件不参与命中测试。
            Background = Brushes.Transparent,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Width = width,
            Height = height,
            Cursor = new Cursor(cursor),
        };

        border.PointerPressed += (_, e) => BeginResize(e, edge, border);
        border.PointerMoved += (_, e) => UpdateResize(e);
        border.PointerReleased += (_, e) => EndResize(e);
        return border;
    }

    // 用**屏幕坐标**算增量：拖左边/左下角时窗口原点也在动，用窗口内坐标会把两个位移叠在一起。
    // 屏幕坐标是物理像素，除以 RenderScaling 换算回 DIP（Width/Height 是 DIP）。
    void BeginResize(PointerPressedEventArgs e, ResizeEdge edge, Control grip)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        mResizing = true;
        mResizeEdge = edge;
        mStartWidth = Width;
        mStartHeight = Height;
        mStartPosition = Position;
        mStartScreen = this.PointToScreen(e.GetPosition(this));
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    void UpdateResize(PointerEventArgs e)
    {
        if (!mResizing)
            return;

        var screen = this.PointToScreen(e.GetPosition(this));
        double scale = RenderScaling > 0 ? RenderScaling : 1;
        double dx = (screen.X - mStartScreen.X) / scale;
        double dy = (screen.Y - mStartScreen.Y) / scale;

        double width = mStartWidth;
        double height = mStartHeight;
        int x = mStartPosition.X;

        if (mResizeEdge is ResizeEdge.Left or ResizeEdge.BottomLeft)
        {
            // 左边拖动：宽度减、原点右移；夹住最小宽度时原点也不能再动。
            dx = Math.Min(dx, mStartWidth - MinWidth);
            width = mStartWidth - dx;
            x = mStartPosition.X + (int)Math.Round(dx * scale);
        }
        else if (mResizeEdge is ResizeEdge.Right or ResizeEdge.BottomRight)
        {
            width = Math.Max(MinWidth, mStartWidth + dx);
        }

        if (mResizeEdge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight)
            height = Math.Max(MinHeight, mStartHeight + dy);

        Width = width;
        Height = height;

        if (mResizeEdge is ResizeEdge.Left or ResizeEdge.BottomLeft)
            Position = new PixelPoint(x, Position.Y);

        e.Handled = true;
    }

    void EndResize(PointerReleasedEventArgs e)
    {
        if (!mResizing)
            return;

        mResizing = false;
        e.Pointer.Capture(null);
        SaveGeometry();
        e.Handled = true;
    }

    // ── 窗口几何记忆（落在宿主数据目录，受沙盒重定向）──

    void LoadGeometry()
    {
        try
        {
            string path = GeometryPath;
            if (!File.Exists(path))
                return;

            var geometry = JsonSerializer.Deserialize<WindowGeometry>(File.ReadAllText(path));
            if (geometry == null)
                return;

            if (geometry.Width >= MinWidth)
                Width = geometry.Width;
            if (geometry.Height >= MinHeight)
                Height = geometry.Height;
        }
        catch
        {
            // 几何读不出来就用默认尺寸 —— 不值得为它打扰用户。
        }
    }

    void SaveGeometry()
    {
        try
        {
            Directory.CreateDirectory(mHost.DataFolder);
            File.WriteAllText(GeometryPath,
                JsonSerializer.Serialize(new WindowGeometry { Width = Width, Height = Height }));
        }
        catch
        {
        }
    }

    string GeometryPath => Path.Combine(mHost.DataFolder, "mixer-window.json");

    sealed class WindowGeometry
    {
        public double Width { get; set; }
        public double Height { get; set; }
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

            // AUX 条右键 → 删除（一步可撤销；见适配器 RemoveAux 的注释）。
            if (channel.Kind == MixerChannelKind.Aux)
                strip.ContextMenu = BuildAuxMenu(channel);

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

    // AUX 条的右键菜单。目前只有"删除"（重命名 / 换色留到需要时再加）。
    ContextMenu BuildAuxMenu(IMixerChannel channel)
    {
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = L("删除此 AUX", "Delete this AUX") };
        remove.Click += (_, _) => mModel.RemoveAux(channel);
        menu.Items.Add(remove);
        return menu;
    }

    void ShowSendPopup(MixerChannelStrip strip)
    {
        mSendPopup?.Close();
        var popup = new SendPopupWindow(strip.Channel, mModel.Outputs, mHost, mModel.SupportsSends);
        PlacePopup(popup, strip.SendAnchor);
        popup.Show();
        mSendPopup = popup;
    }

    void ShowFxPopup(MixerChannelStrip strip)
    {
        mFxPopup?.Close();
        var popup = new FxPopupWindow(strip.Channel, mModel.InsertFormats, mHost, mModel.SupportsInserts);
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
