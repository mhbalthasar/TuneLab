using System;
using System.Collections.Generic;
using TuneLab.ExperimentalPatch.MixerEngine;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 一个 VST3 插入的**宿主侧统一面**。宿主（混音台那一行、参数面板、编辑器壳）只认这个接口。
//
// 【现在只有一种活法】VST3 一律在桥进程里跑（VST3-BRIDGE.md）。实现方只有
// Vst3BridgeInsert 一个 —— 插件代码不再进宿主进程，所以"进程内 / 进程外"的分叉消失了。
// 桥进程里有它自己的那份实现（VstBridge/InProcess/Vst3Insert），那是**另一个进程**里的事，
// 宿主看不到也不需要看。
//
// 【哪些成员是 VST3 专有】VST3 的参数是一个"编辑控制器"的模型（id + 归一化值 +
// 默认值 + flush），VST2 的 getParameter 没有这些，所以不进 IMixInsert。
// 编辑器也一样：VST3 的窗口由插件自己建，VST2 的由桥建，形状不同，各自成面。
public interface IVst3Insert : IMixInsert
{
    bool HasEditor { get; }

    // 界面是否必须 OpenGL 1.3+（DPF / NanoVG）。桥模式由桥侧判，宿主据此决定是否退参数面板。
    bool NeedsOpenGl { get; }

    // 插件自己报的首选尺寸；null = 用宿主默认大小。
    (int Width, int Height)? EditorSize { get; }

    /// <summary>编辑控制器暴露的全部参数。</summary>
    IReadOnlyList<InsertParameter> Parameters { get; }

    double GetParameter(int id);

    void SetParameter(int id, double value);

    /// <summary>
    /// 把排队的参数改动"冲"进处理器。VST3 的参数改动要等一次 process() 才被消费，
    /// 停止播放时没人 process，改动会一直排在队列里（表现为"调完几秒后自动变回去"）。
    /// </summary>
    void FlushParameterChanges();

    byte[]? SaveState();

    bool LoadState(byte[] data);

    // ── 编辑器 ──
    //
    // 窗口由插件建在**桥进程**里，桥那边用一条 STA 线程自己泵；宿主这边 Open/Close
    // 翻成桥消息，FindEditorWindowSince 返回桥回传的 HWND，好让编辑器壳照常置前。
    bool OpenEditor(IntPtr parent);

    bool IsEditorOpen { get; }

    void PumpEditorMessages();

    void CloseEditor();

    void ResizeEditor(int width, int height);

    // 桥模式下返回**桥回传的**句柄（窗口不在这个进程里，
    // 但置前 / 可见性查询都能跨进程用），SnapshotTopLevelWindows 则回空集。
    HashSet<IntPtr> SnapshotTopLevelWindows();

    IntPtr FindEditorWindowSince(HashSet<IntPtr> before);
}
