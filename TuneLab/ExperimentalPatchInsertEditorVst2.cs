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
    readonly string mPluginName;

    Vst2EditorShell(Vst2Insert insert, string pluginName)
    {
        mPluginName = pluginName;

        // 桥那头窗口已经开出来了。ChildWindows > 0 = 插件真的建出了 GUI；
        // 0 = 插件返回成功却什么都没建（依赖缺失那一类）—— 那是用户报"窗口是空的"的那种。
        (bool ok, string? reason, int children) = insert.OpenEditor();

        if (!ok)
        {
            HostLog?.Invoke($"insert: cannot open the editor of '{pluginName}' — {reason ?? "the plugin refused"}");
            return;
        }

        if (children == 0)
            HostLog?.Invoke($"insert: '{pluginName}' created no GUI (the bridge window is empty; "
                          + "usually a missing runtime dependency)");

        // 桥侧自己把窗口带到前台（SetForegroundWindow 只能由前台进程调）。
        insert.TryActivateEditor();
    }

    // 宿主给的日志出口。桥那边的情况（超时 / 空窗 / 拒绝）都经这里进宿主日志，
    // 因为 Vst2EditorShell 自己不能引用宿主日志类型。
    public static Action<string>? HostLog;

    public static Vst2EditorShell Create(Vst2Insert insert, string pluginName)
        => new(insert, pluginName);

    public event Action? WindowClosed;

    // 已经开着：再点一次「界面」就是把它带到前台（桥那边做）。
    public void Activate() { }

    public void DropEditor()
    {
        // 桥侧的窗口随插件实例一起关：InsertInstanceFactory.Release → 桥 unload → 编辑器关。
    }

    public void Close()
    {
        WindowClosed?.Invoke();
    }

    public void Show() { }

    // 给宿主侧（InsertEditorHost）在日志里用的插件名。
    public string Name => mPluginName;
}
