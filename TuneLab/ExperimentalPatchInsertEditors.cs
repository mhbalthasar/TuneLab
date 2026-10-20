using System;
using System.Collections.Generic;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.Foundation;

namespace TuneLab.Audio;

// 插件原生编辑器的入口（插入效果那一行的「界面」键，DESIGN.md §5.5）。
//
// 【两种插件的窗口归谁】都不归我们 —— 这是实测出来的边界，不是设计选择：
//   · VST3：把宿主窗口句柄交给 native 库，它返回成功、IsEditorOpen 为真、GUI 也能
//     被瞥见，但 GUI 从来不挂进我们给的 HWND（窗口树下 0 个子窗口，STA 同线程也一样）。
//     所以改由插件自己开顶层窗，我们只负责开关与泵消息。
//   · VST2：窗口由桥进程创建（插件在哪个进程加载，窗口就只能在哪个进程）。
// 对上（混音台的浮窗）只暴露一个入口，所以这里做委托，别让界面去分格式。
//
// 【生命周期】窗口按槽位注册。实例被释放（换插件 / 卸插件 / 关工程）时，
// InsertInstanceFactory.Release 会先来这里关窗：窗口里挂的是插件自己的 view，
// 必须在 effClose / FreeLibrary 之前摘下来，否则关窗时插件会碰已释放的组件。
//
// 【线程】只从 UI 线程调（Show / Close / 事件处理器）。
internal static class InsertEditorHost
{
    static readonly Dictionary<string, IInsertEditorWindow> sWindows = [];

    public static void Show(string slotId, string pluginName, IMixInsert insert)
    {
        // 已开着的窗口只带到前面，不重建 —— 重建会丢插件窗口里的临时状态。
        // 但如果插件窗口已经被用户关掉了（IsOpen 为 false），就注销旧的、重新开一扇；
        // 复用一个死掉的壳正是"关掉之后再点没反应"的原因。
        if (sWindows.TryGetValue(slotId, out var existing))
        {
            if (existing.IsOpen)
            {
                existing.Activate();
                return;
            }

            sWindows.Remove(slotId);
        }


        // 插件窗口是它自己的（VST3 在本进程、VST2 在桥进程）：用户点了那个窗的 ✕
        // 之后壳必须注销，否则再点「界面」只会复用一个已经停掉消息泵的旧壳
        // —— 表现是"第一次能开，之后再点没反应"。

        if (insert is Vst3Insert vst3)
        {
            var shell = Vst3EditorShell.Create(vst3, pluginName);

            if (!shell.TryOpen())
            {
                Log.Warning($"insert: '{pluginName}' has no usable editor; the button does nothing");
                return;
            }

            Register(slotId, shell);
            return;
        }

        if (insert is Vst2Insert vst2)
        {
            Register(slotId, Vst2EditorShell.Create(vst2, pluginName));
            return;
        }

        Log.Warning($"insert: '{pluginName}' has an unhandled format; cannot open an editor");
    }

    // ── TuneLab 接管的参数面板 ──
    //
    // 与插件 GUI 完全独立的一条路：不碰插件的窗口，只读写它的参数。
    // 同一个槽位只留一扇，再点就激活已有的那扇。
    static readonly Dictionary<string, InsertParameterWindow> sParameterWindows = [];

    public static void ShowParameters(string slotId, string pluginName, IMixInsert insert)
    {
        if (sParameterWindows.TryGetValue(slotId, out var existing) && !existing.IsVisible)
        {
            sParameterWindows.Remove(slotId);
            existing = null;
        }

        if (existing != null)
        {
            existing.Activate();
            return;
        }

        var window = InsertParameterWindow.Create(pluginName, insert);
        sParameterWindows[slotId] = window;

        window.Closed += (_, _) => sParameterWindows.Remove(slotId);

        window.Show();
    }

    static void Register(string slotId, IInsertEditorWindow shell)
    {
        shell.WindowClosed += () => sWindows.Remove(slotId);
        sWindows[slotId] = shell;
    }

    public static void Close(string slotId)
    {
        if (!sWindows.Remove(slotId, out var window))
            return;

        try
        {
            window.DropEditor();
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Warning("insert: failed to close an editor window: " + ex.Message);
        }
    }

    public static void CloseAll()
    {
        foreach (var slotId in sWindows.Keys)
        {
            if (!sWindows.TryGetValue(slotId, out var window))
                continue;

            try
            {
                window.DropEditor();
                window.Close();
            }
            catch (Exception)
            {
                // 逐个关，一个坏窗口不该挡住其余的收拾。
            }
        }

        sWindows.Clear();
    }
}






