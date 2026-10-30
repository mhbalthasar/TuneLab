using System;
using System.Collections.Generic;
using OwnVST3Host;
using TuneLab.ExperimentalPatch.MixerEngine;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 一个 VST3 插入的**宿主侧统一面**：进程内的 Vst3Insert 与过桥的 Vst3BridgeInsert
// 都实现它。宿主（混音台那一行、参数面板、编辑器壳）只认这个接口。
//
// 【为什么要抽出来】VST3 以前只有进程内一种活法，宿主里到处 `is Vst3Insert`。
// 隔离来了之后多了"插件跑在桥进程里"这一种，宿主不该在每处都分叉成两段。
// 抽出接口后，宿主的分叉点只剩一个：谁来建实例（Vst3Backend.Create）。
//
// 【哪些成员是 VST3 专有】VST3 的参数是一个"编辑控制器"的模型（id + 归一化值 +
// 默认值 + flush），VST2 的 getParameter 没有这些，所以不进 IMixInsert。
// 编辑器也一样：VST3 的窗口由插件自己建，VST2 的由桥建，形状不同，各自成面。
public interface IVst3Insert : IMixInsert
{
    // 这个实例是不是真的跑在桥进程里。false = 进程内（含"要求隔离但回退了"的情况）。
    // 宿主据此记日志 / 在界面上标示"未隔离"（P3）。
    bool IsIsolated { get; }

    bool HasEditor { get; }

    // 界面是否必须 OpenGL 1.3+（DPF / NanoVG）。桥模式由桥侧判，宿主据此决定是否退参数面板。
    bool NeedsOpenGl { get; }

    // 插件自己报的首选尺寸；null = 用宿主默认大小。
    (int Width, int Height)? EditorSize { get; }

    /// <summary>编辑控制器暴露的全部参数。</summary>
    IReadOnlyList<VST3Parameter> Parameters { get; }

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
    // 进程内：窗口由插件建在**本进程**，实现方自己开、自己泵（宿主用定时器驱动 Pump）。
    // 桥模式：窗口由插件建在**桥进程**，实现方的这些方法基本是 no-op（P2 才会真开窗）。
    bool OpenEditor(IntPtr parent);

    bool IsEditorOpen { get; }

    void PumpEditorMessages();

    void CloseEditor();

    void ResizeEditor(int width, int height);

    // 找插件自己那扇顶层窗（本进程内）。桥模式下返回空集 / 0 —— 窗口不在这个进程里，
    // 宿主改成用桥回传的 HWND 做置前（P2）。
    HashSet<IntPtr> SnapshotTopLevelWindows();

    IntPtr FindEditorWindowSince(HashSet<IntPtr> before);
}
