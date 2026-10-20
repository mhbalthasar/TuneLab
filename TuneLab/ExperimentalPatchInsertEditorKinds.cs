using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.Foundation;

namespace TuneLab.Audio;

// 编辑器窗口的公共接口：上层（InsertEditorHost）只管"按 id 开关"，不管后面是哪种插件。
internal interface IInsertEditorWindow
{
    void Activate();
    void DropEditor();
    void Close();
    void Show();
}
