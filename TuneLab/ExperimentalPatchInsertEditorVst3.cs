using System;
using System.Collections.Generic;
using Avalonia.Threading;
using TuneLab.ExperimentalPatch.InsertHost;
using HostLog = TuneLab.Foundation.Log;

namespace TuneLab.Audio;

// VST3 编辑器的壳：窗口由**插件/native 库自己建**（见 Vst3Insert.OpenEditor 的实测记录），
// 我们这边不持有任何原生窗口句柄。
//
// 【它为什么存在】调音台那一行「界面」键点下去要到三件事：
//   1. 让插件把窗口开出来（OpenEditor 传 0）；
//   2. 之后持续泵插件自己的消息队列（不泵，VSTGUI 那类插件不画第一帧 / 不响应）；
//   3. 关掉时按正确顺序摘（CloseEditor 早于实例销毁）。
// 这些原本是给宿主窗口写的，现在窗口归插件，剩下的就是这三件事 —— 一个壳。
//
// 【与 VST2 那条路的区别】VST2 的窗口开在**桥进程**里（跨进程），VST3 开在**本进程**。
// 两条路都"不持有 HWND"，所以对外形状一致，只是 pump 的归属不同：
// VST3 要我们自己泵（本进程的消息队列），VST2 由桥那条线程自带循环。
//
// 【线程】全部只在 UI 线程调。
public sealed class Vst3EditorShell : IInsertEditorWindow
{
    readonly Vst3Insert mInsert;
    readonly string mPluginName;

    // 泵插件消息的定时器。插件窗口活着它就活着；关掉编辑器 / 实例就停。
    DispatcherTimer? mTimer;

    // 打开编辑器之前记下的本进程顶层窗集合；用来"差"出插件那扇窗。
    HashSet<IntPtr>? mWindowBefore;

    // 已经把它带到前台过一次了（窗口可能晚出现，所以要在定时器里重试）。
    bool mRaised;

    Vst3EditorShell(Vst3Insert insert, string pluginName)
    {
        mInsert = insert;
        mPluginName = pluginName;
    }

    public static Vst3EditorShell Create(Vst3Insert insert, string pluginName)
        => new(insert, pluginName);

    /// <summary>
    /// 打开插件自己那扇窗。返回 false = 插件拒绝 / 没有界面；提示由调用方负责显示，
    /// 这里只把原因说清楚（日志里能直接看到是哪种失败）。
    /// </summary>
    public bool TryOpen()
    {
        if (!mInsert.HasEditor)
        {
            HostLog.Warning($"insert: '{mPluginName}' reports no editor");
            return false;
        }

        // 先记下"打开前"的本进程顶层窗，打开后取差集 = 插件那扇窗。
        // 它自己不会主动置前（用户报"打开了但藏在别的窗口下面"），我们要拿到句柄才能置顶。
        var before = mInsert.SnapshotTopLevelWindows();

        if (!mInsert.OpenEditor(IntPtr.Zero))
        {
            HostLog.Warning($"insert: '{mPluginName}' refused to open its editor");
            return false;
        }

        // 插件窗口在自己手里，我们不知道它什么时候才算"画完了"。用 native 自己的
        // IsEditorOpen 判断，比数我们的窗口可靠（实测：数我们的子窗口恒为 0 且会误导人）。
        if (!mInsert.IsEditorOpen)
        {
            HostLog.Warning($"insert: '{mPluginName}' reported success but IsEditorOpen is false");
            return false;
        }

        StartPump();

        // 窗口可能刚建好还没显示，所以**第一次泵消息时**再取差集并置顶（见 StartPump）。
        mWindowBefore = before;
        TryRaise();

        return true;
    }

    // 把插件窗口带到前台并置顶。窗口是插件建的，可能要晚几十毫秒才可见，
    // 所以泵消息的定时器会重试几次，找到一次就够了。
    void TryRaise()
    {
        if (mRaised || mWindowBefore == null)
            return;

        IntPtr hwnd = mInsert.FindEditorWindowSince(mWindowBefore);
        if (hwnd == IntPtr.Zero)
            return;

        Vst3Insert.BringToFront(hwnd);
        mRaised = true;

        HostLog.Info($"insert: raised the editor window of '{mPluginName}' (0x{hwnd:x})");
    }

    public bool HasOpenEditor => mInsert.IsEditorOpen;

    // 插件窗口还开着吗？宿主用它决定"带到前台"还是"重新开"。
    public bool IsOpen => mInsert.IsEditorOpen;

    // 每 20ms 泵一次插件自己的消息队列。VSTGUI 那类实现不泵就不画第一帧、也不响应点击。
    void StartPump()
    {
        if (mTimer != null)
            return;

        mTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };

        mTimer.Tick += (_, _) =>
        {
            if (!mInsert.IsEditorOpen)
            {
                // 插件窗口被用户关掉了：停泵，让宿主也有机会注销这个壳。
                // （下次点「界面」会新建一个壳 —— 不注销就会复用一个已经停泵的旧壳，
                //   表现是"第一次能开、之后再点没反应"。）
                StopPump();
                WindowClosed?.Invoke();
                return;
            }

            mInsert.PumpEditorMessages();

            // 窗口可能比 CreateEditor 晚几十毫秒才可见：每次泵都试一次，找到即置顶。
            TryRaise();
        };

        mTimer.Start();
    }

    void StopPump()
    {
        if (mTimer == null)
            return;

        mTimer.Stop();
        mTimer = null;
    }

    public event Action? WindowClosed;

    // 插件自己那扇窗已经在最前面（native 库建窗时激活它）。
    // 再点一次「界面」就是把它带到前台：Implement 里没有专门的 raise，
    // 所以这里只能重开一次（插件侧对"已开着"的实现是直接返回 true）。
    public void Activate()
    {
        if (!mInsert.IsEditorOpen)
            return;

        mInsert.PumpEditorMessages();

        // 再点一次「界面」= 把它重新带到前台（用户可能把它挪到别的窗口后面了）。
        mRaised = false;
        TryRaise();
    }

    public void DropEditor()
    {
        StopPump();

        // 顺序要紧：先摘插件的 view，再让实例走销毁流程（见 Vst3Insert.CloseEditor 的注释）。
        mInsert.CloseEditor();
    }

    public void Close()
    {
        DropEditor();
        WindowClosed?.Invoke();
    }

    public void Show()
    {
        // 壳没有窗口；真正开窗在 Create 里的 TryOpen（见 InsertEditorHost.Show）。
    }
}








