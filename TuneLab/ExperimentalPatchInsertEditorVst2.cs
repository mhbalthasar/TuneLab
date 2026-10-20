using System;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.Foundation;

namespace TuneLab.Audio;

// VST2 编辑器的壳：插件跑在**桥进程**里，窗口也由桥开，宿主这边不持有任何原生句柄。
//
// 【为什么没有独立窗口】跨进程 SetParent 在 Windows 上不可靠（菜单、焦点、拖放、DPI 全错）。
// 所以宿主不发窗口，只发一条消息；桥自己建窗并激活它。
// 这里剩下的唯一职责是"转告用户桥说了什么"——失败时给一句明话，
// 而不是让那个「界面」键按下去毫无反应（DESIGN.md §5.5）。
//
// 代价：VST2 的编辑器窗口是独立的一扇，不带在一个宿主窗口里。这是进程边界的必然结果。
public sealed class Vst2EditorShell : IInsertEditorWindow
{
    Vst2EditorShell(Vst2Insert insert, string pluginName)
    {
        // 桥那头窗口已经开出来了；带前台由桥自己做（SetForegroundWindow 只能由前台进程调）。
        if (!insert.TryActivateEditor())
        {
            Log.Warning($"insert: the editor of '{pluginName}' could not be raised");
            return;
        }
    }

    public static Vst2EditorShell Create(Vst2Insert insert, string pluginName)
        => new(insert, pluginName);

    // 已经开着：再点一次「界面」就是把它带到前台（桥那边做）。
    public void Activate() { }

    public void DropEditor()
    {
        // 桥侧的窗口随插件实例一起关：InsertInstanceFactory.Release → 桥 unload → 编辑器关。
    }

    public void Close() { }

    public void Show() { }
}
