using System;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.Foundation;

namespace TuneLab.Audio;

// VST2 编辑器的壳：插件跑在**桥进程**里，窗口也由桥开，宿主这边不持有任何原生句柄。
//
// 【为什么没有独立窗口】跨进程 SetParent 在 Windows 上不可靠（菜单、焦点、拖放、DPI 全错）。
// 所以宿主不发窗口，只发一条消息；桥自己建窗并激活它。
// 这里剩下的职责是"转告用户桥说了什么"，以及"再点一次时把窗口带回来 / 重开"。
//
// 代价：VST2 的编辑器窗口是独立的一扇，不带在宿主窗口里。这是进程边界的必然结果。
public sealed class Vst2EditorShell : IInsertEditorWindow
{
    readonly Vst2Insert mInsert;
    readonly string mPluginName;

    Vst2EditorShell(Vst2Insert insert, string pluginName)
    {
        mInsert = insert;
        mPluginName = pluginName;

        Open();
    }

    public static Vst2EditorShell Create(Vst2Insert insert, string pluginName)
        => new(insert, pluginName);

    public event Action? WindowClosed;

    // VST2 的窗口在桥进程里，我们不知道它什么时候被关掉。所以这里一律当"可能还开着"，
    // 再点一次「界面」就重新发一次 OpenEditor：桥侧对"已开着"是带到前台、对"已关"是重开。
    public bool IsOpen => true;

    public void Activate() => Open();

    void Open()
    {
        // 桥侧：窗口开着就带到前台，没开就新建。返回 ChildWindows = 窗口里挂了几个子窗口，
        // 0 说明插件返回成功却什么也没建（依赖缺失那一类）。
        (bool ok, string? reason, int children) = mInsert.OpenEditor();

        if (!ok)
        {
            Log.Warning($"insert: cannot open the editor of '{mPluginName}' — {reason ?? "the plugin refused"}");
            return;
        }

        if (children == 0)
        {
            Log.Warning($"insert: '{mPluginName}' created no GUI "
                      + "(the bridge window is empty; usually a missing runtime dependency)");
            return;
        }

        // 桥侧自己把窗口带到前台（SetForegroundWindow 只能由前台进程调）。
        mInsert.TryActivateEditor();
    }

    public void DropEditor()
    {
        // 桥侧的窗口随插件实例一起关：InsertInstanceFactory.Release → 桥 unload → 编辑器关。
    }

    public void Close()
    {
        WindowClosed?.Invoke();
    }

    public void Show() { }
}
