using System;
using System.Collections.Generic;
using TuneLab.ExperimentalPatch.VstBridge;

// 桥进程里"某个格式的宿主实现"。BridgeSession 只认这个接口，按槽位把消息转发给它，
// 于是"VST2 走 PluginHost、VST3 走 Vst3PluginHost"这件事对这层是透明的。
//
// 【为什么不是把 VST3 塞进 PluginHost】PluginHost 是 VST2 的 AEffect ABI，里面
// 一半代码在算指针偏移。混进 VST3 会让那份必须逐字节正确的 ABI 代码变得没法审。
// 分开之后，两边各自可单测，路由这一层只有字典查找。
//
// 与 PluginHost / BridgeSession 一样在**全局命名空间**：这三者是同一层的内部实现，
// 不对外，放进名字空间只会让彼此多写一行 using。
internal interface IBridgePluginHost
{
    void Attach(SharedAudioRing input, SharedAudioRing output);

    (bool Ok, string? Reason, string? Name, string? Vendor, int LatencySamples, bool IsSynth,
        bool HasEditor, bool NeedsOpenGl) Load(
        LoadPluginMessage request, out int inputChannels, out int outputChannels);

    bool Unload(int slotNumber);

    bool Process(int slotNumber, bool bypass, out int frames, out int channels);

    byte[]? GetState(int slotNumber);

    bool SetState(int slotNumber, string base64);

    List<ParameterInfo> GetParameters(int slotNumber);

    void SetParameter(int slotNumber, long id, double value);

    // 开（或激活）插件编辑器。Window = 插件窗口的 HWND（VST2 由桥自己置前，回 0；
    // VST3 回插件自己那扇顶层窗的句柄，宿主用它置前 / 查可见性）。
    (bool Ok, string? Reason, int ChildWindows, IntPtr Window) OpenEditor(int slotNumber, string title);

    bool CloseEditor(int slotNumber);
}
