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
        string format = PluginFormats.Vst2);

    bool UnloadBlocking(int slot);

    bool SendProcess(int slot, float[][] inPlanes, float[][] outPlanes, int channels, int frames, bool bypass);

    byte[]? GetStateBlocking(int slot);

    bool SetStateBlocking(int slot, byte[] data);

    IReadOnlyList<ParameterInfo>? GetParametersBlocking(int slot);

    bool SetParameterBlocking(int slot, long id, double value);
}
