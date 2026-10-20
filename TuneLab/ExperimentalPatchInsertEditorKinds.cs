using System;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.Foundation;

namespace TuneLab.Audio;

// 编辑器窗口的公共接口：上层（InsertEditorHost）只管"按 id 开关"，不管后面是哪种插件。
internal interface IInsertEditorWindow
{
    void Activate();
    void DropEditor();
    void Show();
    void Close();

    // 真的关掉时回调一声（用户点了 ✕ / 原生窗口自己没了）。宿主据此注销槽位。
    // 名字故意不叫 Closed：Avalonia 的 TopLevel.Closed 已经是 event EventHandler，
    // 撞名会让实现方"意外实现"接口成员 —— 那正是这一轮修掉的 bug。
    event Action? WindowClosed;
}
