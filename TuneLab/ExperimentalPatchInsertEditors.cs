using System;
using System.Collections.Generic;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.Foundation;

namespace TuneLab.Audio;

// 插件原生编辑器的入口（插入效果那一行的「界面」键，DESIGN.md §5.5）。
//
// 【为什么要有这么一个中间层】两种插件的开窗方式完全不同，且都只能在各自的进程里做：
//   · VST3 是进程内加载，插件把 GUI 画进我们给它的 HWND；
//   · VST2 跑在**桥进程**里，窗口也由桥创建，宿主只发一条消息。
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
        if (sWindows.TryGetValue(slotId, out var existing))
        {
            existing.Activate();
            return;
        }

        IInsertEditorWindow? window = insert switch
        {
            Vst3Insert vst3 => Vst3EditorWindow.Create(vst3, pluginName),
            Vst2Insert vst2 => Vst2EditorShell.Create(vst2, pluginName),
            _ => null,
        };

        if (window == null)
            return;

        sWindows[slotId] = window;
        window.Show();
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
