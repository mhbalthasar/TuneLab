using System;
using System.Collections.Generic;
using TuneLab.ExperimentalPatch.MixerEngine;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 一个"带参数面板 / 编辑器"的插入在**宿主侧的统一面**。宿主（混音台那一行、参数面板、
// 编辑器壳）只认这个接口，不认格式。
//
// 【为什么叫 IParameterInsert 而不是 IVst3Insert】这个名字是 VST3 起的（当初只有它有
// 参数模型与编辑器）。CLAP 是同一套模型 —— id + 值 + 默认值，改动等到下一次 process ——
// 所以两份实现共用这一个接口。格式相关的东西（窗口怎么建、桥怎么搭）留在各自的实现里。
//
// 【现在有谁实现】Vst3BridgeInsert、ClapBridgeInsert（都在宿主侧，插件代码在桥进程里）；
// 桥进程里另有一份 Vst3Insert（VstBridge/InProcess/），那是**另一个进程**里的事，
// 宿主看不到也不需要看。
//
// 【为什么不进 IMixInsert】VST2 的 getParameter 没有"默认值 / flush"这些概念，硬塞进
// IMixInsert 会让 VST2 实现一堆空方法。编辑器同理：VST2 的窗口由桥建，VST3/CLAP 的
// 由插件自己建，形状不同，各自成面。
public interface IParameterInsert : IMixInsert
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
