using System.Collections.Generic;
using TuneLab.ExperimentalPatch.VstBridge;

namespace TuneLab.ExperimentalPatch.InsertHost;

// Vst3BridgeInsert 需要的桥通道面（VstBridgeClient 的子集）。
//
// 【为什么要抽】Vst3BridgeInsert 的职责是"把 IVst3Insert 的调用翻成桥消息"，
// 不该绑死在一个会起真实进程的 VstBridgeClient 上 —— 否则单测要么起真桥、
// 要么根本没有。抽成接口后，测试给一个记账用的假通道就能覆盖
// "load 失败 / 桥崩 / state 回灌 / 参数往返"这些分支，不碰任何原生代码。
//
// 只在 InsertHost 内部用（internal）：宿主看不到它，看到的是 IVst3Insert。
internal interface IBridgeChannel
{
    string? LastBridgeError { get; }

    void ClearBridgeError();

    // 桥自报能承载的格式（x64 桥含 vst3）。null = 还没握手 / 旧桥。
    string[]? BridgeFormats { get; }

    bool EnsureStartedBlocking();

    int AllocateSlot();

    LoadPluginResultMessage? LoadBlocking(int slot, string path, double sampleRate, int maxBlock, bool bypass,
        string format = PluginFormats.Vst2, string? pluginId = null);

    bool UnloadBlocking(int slot);

    bool SendProcess(int slot, float[][] inPlanes, float[][] outPlanes, int channels, int frames, bool bypass);

    // [P4] 桥的音频路是否已停用（连续失败过多，见 AudioStallWatch）。真实的 VstBridgeClient
    // 会在一秒左右判死；宿主据此把这一个插入标红（运行期挂起，不是建实例失败）。
    bool AudioStalled { get; }

    byte[]? GetStateBlocking(int slot);

    bool SetStateBlocking(int slot, byte[] data);

    IReadOnlyList<ParameterInfo>? GetParametersBlocking(int slot);

    bool SetParameterBlocking(int slot, long id, double value);

    // [P2] 编辑器。开 = 让桥开（或激活）插件窗口；WindowHandle = 插件窗口的 HWND
    // （VST2 回 0，VST3 回插件自己那扇顶层窗的句柄）。
    (bool Ok, string? Reason, int ChildWindows, long WindowHandle) OpenEditorBlocking(int slot, string title);

    bool CloseEditorBlocking(int slot);
}
