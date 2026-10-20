using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using TuneLab.ExperimentalPatch.InsertHost;
using HostLog = TuneLab.Foundation.Log;

namespace TuneLab.Audio;

// VST3 插件编辑器的宿主窗口：真正把插件 GUI 嵌进 Avalonia 窗口的那一层。
//
// 【插件要什么】VST3 的 IPlugView 要点进一个宿主提供的原生窗口（Windows 上就是 HWND）。
// 于是我们建一个 Avalonia Window，把它的平台句柄交给插件，再由插件在里面创建自己的
// 子窗口。这跟 REAPER / Cubase 的做法一样 —— 插件 GUI 是原生控件，不由我们自己绘制。
//
// 【尺寸】插件自己报一个首选尺寸（IPlugView::getSize）。尊重它：硬要塞进我们定的尺寸
// 会让插件画出滚动条，那比窗口大一点糟得多。用户自己还能拖。
//
// 【关闭】关窗前先调 CloseEditor：插件要在窗口销毁之前把 view 摘掉（否则它关窗时会碰到
// 已经释放的组件，实测会崩）。顺序写死在 DropEditor 里，别调换。
public sealed class Vst3EditorWindow : Window, IInsertEditorWindow
{
    readonly Vst3Insert mInsert;
    IntPtr mParent;

    Vst3EditorWindow(Vst3Insert insert, string pluginName)
    {
        mInsert = insert;

        Title = pluginName;

        // 一定要给个初始尺寸：Win32 的 CreateWindowEx 不接受 0 尺寸的窗口，
        // 而插件句柄是在窗口第一次呈现时才拿得到的。
        var size = insert.EditorSize;
        Width = size?.Width > 0 ? size.Value.Width : 640;
        Height = size?.Height > 0 ? size.Value.Height : 480;

        MinWidth = 200;
        MinHeight = 120;

        Background = new SolidColorBrush(Color.FromRgb(24, 24, 26));

        Content = new TextBlock
        {
            Text = pluginName,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 164)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 窗口进进出出的两件事：打开时挂编辑器，关闭时摘编辑器。
        Opened += OnOpened;
        Closing += OnClosing;
        Resized += OnResized;
    }

    public static Vst3EditorWindow Create(Vst3Insert insert, string pluginName)
        => new(insert, pluginName);

    public bool HasLiveEditor => mInsert.HasEditor;

    void OnOpened(object? sender, EventArgs e)
    {
        IPlatformHandle? handle = TryGetPlatformHandle();
        if (handle == null)
        {
            // 拿不到句柄（非 Windows / 没渲染器）就没什么可做的了。
            // 窗口里那段提示文字留着，至少不显得"点了没反应"。
            HostLog.Warning($"insert: cannot get a window handle for the editor of '{mInsert.Name}'");
            return;
        }

        mParent = handle.Handle;

        if (!mInsert.OpenEditor(mParent))
        {
            HostLog.Warning($"insert: '{mInsert.Name}' refused to open its editor");
            return;
        }

        // 插件报告首选尺寸（打开之后才报得准的那些插件走这条）：
        // 按它调整一次，用户之后自己拖。
        if (mInsert.EditorSize is { } size && size.Width > 0 && size.Height > 0)
        {
            Width = size.Width;
            Height = size.Height;
        }
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 必须在窗口销毁之前摘掉 view（见类注释）。
        DropEditor();
    }

    void OnResized(object? sender, WindowResizedEventArgs e)
    {
        if (mParent == IntPtr.Zero)
            return;

        mInsert.ResizeEditor((int)e.ClientSize.Width, (int)e.ClientSize.Height);
    }

    // 先摘插件 view，再让窗口消失。调用方（InsertEditorHost）在关插件实例前也会调一次 ——
    // 重复调用是安全的，CloseEditor 自带"没开过就算了"。
    public void DropEditor() => mInsert.CloseEditor();

    void IInsertEditorWindow.Activate() => Activate();
}
