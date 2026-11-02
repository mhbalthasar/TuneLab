using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.VstBridge;

// 桥进程里的 CLAP 宿主：加载 / 音频 / 状态 / 参数全部关在这个进程里。
//
// 【为什么这么薄】CLAP 是 C ABI，没有 native 宿主库要 vendor（与 Vst3PluginHost 相反）：
// 这个类只做三件事 ——
//   1. 把共享环里的平面缓冲摆成 clap_audio_buffer_t；
//   2. 把宿主的参数改动攒成 clap_event_param_value 交给下一次 process；
//   3. 调插件。
//
// 【线程模型：桥是单线程的】收一条消息、办一件事。所以"音频线程"与"主线程"是同一个
// 线程，clap_host_thread_check 的两个问题都答"是"。这也是 CLAP 规范允许的一种宿主：
// 插件不能假设 process 与 on_main_thread 在两条线程上。
// 谁哪天给桥加线程，这个前提（以及 ClapLibrary 的免锁假设）要一起改。
//
// 【编辑器】P-C3 才做（X11 / Win32 嵌入见 CLAP.md §6）。现在 HasEditor 一律 false，
// 界面自动退到参数面板 —— 这与 VST3 在桥不带原生库时的降级是同一条路。
internal sealed unsafe class ClapPluginHost : IBridgePluginHost
{
    readonly Dictionary<int, Slot> mSlots = [];
    SharedAudioRing? mIn;
    SharedAudioRing? mOut;

    // 插件在 clap_host 里看到的名字。非托管分配：插件会长期持有这些指针，托管数组会移动。
    static readonly IntPtr sHostName = Utf8HGlobal("TuneLab");
    static readonly IntPtr sHostVendor = Utf8HGlobal("TuneLab");
    static readonly IntPtr sHostUrl = Utf8HGlobal("https://tunelab.app");
    static readonly IntPtr sHostVersion = Utf8HGlobal("2.0");

    // clap_host_thread_check 的 vtable：内容不变，全进程一份。
    static readonly IntPtr sThreadCheck = BuildThreadCheck();

    static IntPtr Utf8HGlobal(string text)
    {
        byte[] bytes = ClapNative.Utf8(text);
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    static IntPtr BuildThreadCheck()
    {
        IntPtr memory = Marshal.AllocHGlobal(sizeof(ClapNative.HostThreadCheck));
        var check = (ClapNative.HostThreadCheck*)memory;
        check->IsMainThread = &HostIsMainThread;
        check->IsAudioThread = &HostIsAudioThread;
        return memory;
    }

    public void Attach(SharedAudioRing input, SharedAudioRing output)
    {
        mIn = input;
        mOut = output;
    }

    // ── 加载 / 卸载 ──

    public (bool Ok, string? Reason, string? Name, string? Vendor, int LatencySamples, bool IsSynth,
        bool HasEditor, bool NeedsOpenGl) Load(
        LoadPluginMessage request, out int inputChannels, out int outputChannels)
    {
        inputChannels = 0;
        outputChannels = 0;

        if (mSlots.ContainsKey(request.Slot))
            Unload(request.Slot);

        if (!Environment.Is64BitProcess)
            return Fail("32-bit CLAP plugins are not supported");

        if (string.IsNullOrWhiteSpace(request.PluginId))
            return Fail("the load request carries no CLAP plugin id");

        var lease = ClapLibrary.Acquire(request.Path, out string? reason);

        if (lease == null)
            return Fail(reason ?? "the plugin could not be loaded");

        var slot = new Slot
        {
            Lease = lease,
            SampleRate = request.SampleRate > 0 ? request.SampleRate : 48000,
            MaxFrames = (uint)Math.Clamp(request.MaxBlock, 32, SharedAudioLayout.MaxBlockFrames),
            Bypassed = request.Bypass,
        };

        try
        {
            BuildHost(slot);

            if (!CreateInstance(slot, request.PluginId))
            {
                slot.Dispose();
                return Fail($"the plugin factory refused to create '{request.PluginId}'");
            }

            if (slot.Plugin->Init == null || slot.Plugin->Init(slot.Plugin) == 0)
            {
                slot.Dispose();
                return Fail("the plugin refused to initialize");
            }

            slot.Initialized = true;

            ReadExtensions(slot);

            if (!BuildPorts(slot, out string? portReason))
            {
                slot.Dispose();
                return Fail(portReason ?? "the plugin has no usable audio ports");
            }

            // 先激活、再开始处理。min=1：宿主会在块的尾巴上送小于 32 帧的块。
            if (slot.Plugin->Activate != null
                && slot.Plugin->Activate(slot.Plugin, slot.SampleRate, 1, slot.MaxFrames) == 0)
            {
                slot.Dispose();
                return Fail("the plugin refused to activate");
            }

            slot.Activated = true;

            if (slot.Plugin->StartProcessing != null && slot.Plugin->StartProcessing(slot.Plugin) == 0)
            {
                slot.Dispose();
                return Fail("the plugin refused to start processing");
            }

            slot.Processing = true;
            slot.LatencySamples = ReadLatency(slot);

            // 描述符里的名字 / 厂商（描述指针在库被卸载前一直有效，这里立刻复制）。
            var descriptor = slot.Plugin->Desc;
            string? name = descriptor == null ? null : ClapNative.StringAt(descriptor->Name);
            string? vendor = descriptor == null ? null : ClapNative.StringAt(descriptor->Vendor);
            bool isSynth = descriptor != null && ClapScanner.LooksLikeInstrument(
                ClapNative.FeaturesAt(descriptor->Features));

            mSlots[request.Slot] = slot;

            inputChannels = slot.InputChannels;
            outputChannels = slot.OutputChannels;

            return (true, null, name, vendor, slot.LatencySamples, isSynth, false, false);
        }
        catch (Exception ex)
        {
            slot.Dispose();
            return Fail("unexpected error while loading the plugin: " + ex.Message);
        }
    }

    static (bool, string?, string?, string?, int, bool, bool, bool) Fail(string reason)
        => (false, reason, null, null, 0, false, false, false);

    // host 结构必须活在**非托管内存**里：插件会长期持有它并随时回调。
    static void BuildHost(Slot slot)
    {
        slot.HostHandle = GCHandle.Alloc(slot);
        slot.HostMemory = Marshal.AllocHGlobal(sizeof(ClapNative.Host));

        var host = (ClapNative.Host*)slot.HostMemory;
        host->ClapVersion = ClapNative.ClapVersion.Host;
        host->HostData = GCHandle.ToIntPtr(slot.HostHandle);
        host->Name = (byte*)sHostName;
        host->Vendor = (byte*)sHostVendor;
        host->Url = (byte*)sHostUrl;
        host->HostVersion = (byte*)sHostVersion;
        host->GetExtension = &HostGetExtension;
        host->RequestRestart = &HostRequestRestart;
        host->RequestProcess = &HostRequestProcess;
        host->RequestCallback = &HostRequestCallback;
    }

    static bool CreateInstance(Slot slot, string pluginId)
    {
        // 宿主这一层存的是 "clap:<descriptor id>"，插件工厂要的是裸 descriptor id。
        const string prefix = "clap:";
        string id = pluginId.StartsWith(prefix, StringComparison.Ordinal)
            ? pluginId[prefix.Length..]
            : pluginId;

        byte[] idBytes = ClapNative.Utf8(id);

        fixed (byte* idPointer = idBytes)
        {
            IntPtr plugin = slot.Lease.Factory->CreatePlugin(
                slot.Lease.Factory, slot.HostMemory, idPointer);

            if (plugin == IntPtr.Zero)
                return false;

            slot.Plugin = (ClapNative.Plugin*)plugin;
            return true;
        }
    }

    static void ReadExtensions(Slot slot)
    {
        slot.AudioPorts = (ClapNative.PluginAudioPorts*)GetPluginExtension(slot, ClapNative.AudioPortsExtension);
        slot.Params = (ClapNative.PluginParams*)GetPluginExtension(slot, ClapNative.ParamsExtension);
        slot.State = (ClapNative.PluginState*)GetPluginExtension(slot, ClapNative.StateExtension);
        slot.Latency = (ClapNative.PluginLatency*)GetPluginExtension(slot, ClapNative.LatencyExtension);
    }

    static IntPtr GetPluginExtension(Slot slot, string id)
    {
        if (slot.Plugin == null || slot.Plugin->GetExtension == null)
            return IntPtr.Zero;

        byte[] bytes = ClapNative.Utf8(id);
        IntPtr result;

        fixed (byte* pointer = bytes)
            result = slot.Plugin->GetExtension(slot.Plugin, pointer);

        return result;
    }

    static int ReadLatency(Slot slot)
        => slot.Latency != null && slot.Latency->Get != null
            ? (int)Math.Min(slot.Latency->Get(slot.Plugin), int.MaxValue)
            : 0;
    // 端口在加载时问一次、缓冲也在加载时分配一次：process 路径上不分配（实时纪律）。
    static bool BuildPorts(Slot slot, out string? reason)
    {
        reason = null;

        if (slot.AudioPorts == null || slot.AudioPorts->Count == null || slot.AudioPorts->Get == null)
        {
            // 没有音频端口扩展的插件按"一个立体声输入 + 一个立体声输出"处理（旧插件常见）。
            slot.Inputs.Add(CreatePort(2, (int)slot.MaxFrames, isMain: true));
            slot.Outputs.Add(CreatePort(2, (int)slot.MaxFrames, isMain: true));
        }
        else
        {
            CollectPorts(slot, isInput: true);
            CollectPorts(slot, isInput: false);
        }

        slot.MainInput = FindMain(slot.Inputs);
        slot.MainOutput = FindMain(slot.Outputs);

        if (slot.Outputs.Count == 0 || slot.MainOutput < 0)
        {
            reason = "the plugin has no audio output";
            return false;
        }

        slot.OutputChannels = slot.Outputs[slot.MainOutput].Channels;
        slot.InputChannels = slot.MainInput >= 0 ? slot.Inputs[slot.MainInput].Channels : 0;

        if (slot.OutputChannels <= 0)
        {
            reason = "the plugin's main output has no channels";
            return false;
        }

        slot.AllocateBuffers();
        return true;
    }

    static int FindMain(List<Port> ports)
    {
        for (int i = 0; i < ports.Count; i++)
        {
            if (ports[i].IsMain)
                return i;
        }

        return ports.Count > 0 ? 0 : -1;
    }

    static void CollectPorts(Slot slot, bool isInput)
    {
        var ports = isInput ? slot.Inputs : slot.Outputs;

        uint count = slot.AudioPorts->Count(slot.Plugin, isInput ? (byte)1 : (byte)0);

        // 上限：真实插件没有几十个端口，读坏了不能一路分配下去。
        if (count > 32)
            count = 32;

        for (uint index = 0; index < count; index++)
        {
            var info = default(ClapNative.AudioPortInfo);

            if (slot.AudioPorts->Get(slot.Plugin, index, isInput ? (byte)1 : (byte)0, &info) == 0)
                continue;

            int channels = (int)Math.Min(info.ChannelCount, 8);

            if (channels <= 0)
                continue;

            bool isMain = (info.Flags & ClapNative.AudioPortIsMain) != 0;

            ports.Add(CreatePort(channels, (int)slot.MaxFrames, isMain));
        }
    }

    static Port CreatePort(int channels, int frames, bool isMain)
    {
        var port = new Port
        {
            Channels = channels,
            IsMain = isMain,
            Frames = frames,
        };

        port.Planes = Marshal.AllocHGlobal(channels * frames * sizeof(float));
        port.Data = Marshal.AllocHGlobal(channels * sizeof(IntPtr));

        var data = (float**)port.Data;

        for (int channel = 0; channel < channels; channel++)
            data[channel] = (float*)port.Planes + (long)channel * frames;

        port.Buffer = Marshal.AllocHGlobal(sizeof(ClapNative.AudioBuffer));

        var buffer = (ClapNative.AudioBuffer*)port.Buffer;
        buffer->Data32 = data;
        buffer->Data64 = null;
        buffer->ChannelCount = (uint)channels;
        buffer->Latency = 0;
        buffer->ConstantMask = 0;

        return port;
    }

    public bool Unload(int slotNumber)
    {
        if (!mSlots.Remove(slotNumber, out var slot))
            return false;

        slot.Dispose();
        return true;
    }

    // ── 音频 ──

    public bool Process(int slotNumber, bool bypass, out int frames, out int channels)
    {
        frames = 0;
        channels = 0;

        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        if (mIn == null || !mIn.TryRead(0, out _, out var inPlanes, out int channelCount, out int frameCount))
            return false;

        frames = frameCount;
        channels = channelCount;

        try
        {
            DrainCallback(slot);

            if (slot.RestartRequested)
                Restart(slot);

            if (bypass || slot.Bypassed || slot.Broken)
            {
                // 直通：输入原样发回去。
                mOut?.TryPublish(slotNumber, inPlanes, channelCount, frameCount, 1000);
                return true;
            }

            int n = Math.Min(frameCount, (int)slot.MaxFrames);

            FillInputs(slot, inPlanes, channelCount, n);
            ClearOutputs(slot, n);

            var process = slot.ProcessPtr;
            process->SteadyTime = slot.SteadyTime;
            process->FramesCount = (uint)n;
            process->Transport = IntPtr.Zero;
            process->AudioInputs = (ClapNative.AudioBuffer*)slot.InputBuffers;
            process->AudioOutputs = (ClapNative.AudioBuffer*)slot.OutputBuffers;
            process->AudioInputsCount = (uint)slot.Inputs.Count;
            process->AudioOutputsCount = (uint)slot.Outputs.Count;
            process->InEvents = slot.InEventsList;
            process->OutEvents = slot.OutEventsList;

            int status = slot.Plugin->Process == null
                ? ClapNative.ProcessContinue
                : slot.Plugin->Process(slot.Plugin, process);

            // 事件只交付一次：用没用上都不再重复送（插件没读等于丢了这条自动化，
            // 比重复应用同一个值安全）。
            slot.PendingCount = 0;

            slot.SteadyTime += n;

            if (status == ClapNative.ProcessError)
            {
                // 插件说"这一块我搞砸了"：这一块直通，但**不算宿主故障** ——
                // 别让 AudioStallWatch 把它记成"插件挂了"。
                slot.PendingCount = 0;
                mOut?.TryPublish(slotNumber, inPlanes, channelCount, frameCount, 1000);
                return true;
            }

            PublishMainOutput(slot, slotNumber, n);
            return true;
        }
        catch (Exception)
        {
            // 插件抛错（托管侧不该发生，但桥里挂了不影响宿主）：这一块直通。
            try
            {
                mOut?.TryPublish(slotNumber, inPlanes, channelCount, frameCount, 1000);
            }
            catch (Exception)
            {
                // 发布也失败：调用方会把这一块当直通处理。
            }

            return true;
        }
        finally
        {
            mIn.Release();
        }
    }

    // 把共享环的交织平面搬进插件的第一个输入端口；其余输入端口清零（侧链留给将来）。
    static void FillInputs(Slot slot, float[][] inPlanes, int channelCount, int frames)
    {
        for (int port = 0; port < slot.Inputs.Count; port++)
        {
            var target = slot.Inputs[port];
            int channels = target.Channels;

            for (int channel = 0; channel < channels; channel++)
            {
                var destination = new Span<float>((float*)target.Planes + (long)channel * target.Frames, frames);
                destination.Clear();

                if (port != slot.MainInput || inPlanes.Length == 0)
                    continue;

                // 宿主给的声道数可能少于插件的（单声道插件 / 没有侧链）：缺的复制第 0 条。
                int source = Math.Min(channel, channelCount - 1);

                if (source < 0)
                    source = 0;

                var data = inPlanes.Length > source ? inPlanes[source] : inPlanes[0];
                int copy = Math.Min(frames, data.Length);

                data.AsSpan(0, copy).CopyTo(destination[..copy]);
            }
        }
    }

    static void ClearOutputs(Slot slot, int frames)
    {
        foreach (var port in slot.Outputs)
        {
            for (int channel = 0; channel < port.Channels; channel++)
                new Span<float>((float*)port.Planes + (long)channel * port.Frames, frames).Clear();
        }
    }

    void PublishMainOutput(Slot slot, int slotNumber, int frames)
    {
        var main = slot.Outputs[slot.MainOutput];

        var left = new Span<float>((float*)main.Planes, frames);
        var right = main.Channels > 1
            ? new Span<float>((float*)main.Planes + main.Frames, frames)
            : left;

        for (int i = 0; i < frames; i++)
        {
            float l = left[i];
            float r = right[i];

            slot.PublishPlanes[0][i] = float.IsFinite(l) ? l : 0f;
            slot.PublishPlanes[1][i] = float.IsFinite(r) ? r : 0f;
        }

        mOut?.TryPublish(slotNumber, slot.PublishPlanes, 2, frames, 1000);
    }

    // ── 状态 ──

    public byte[]? GetState(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return null;

        if (slot.State == null || slot.State->Save == null)
            return null;

        var stream = new MemoryStream();
        var handle = GCHandle.Alloc(stream);

        try
        {
            var output = new ClapNative.OStream
            {
                Ctx = GCHandle.ToIntPtr(handle),
                Write = &StateWrite,
            };

            return slot.State->Save(slot.Plugin, &output) != 0 ? stream.ToArray() : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            handle.Free();
        }
    }

    public bool SetState(int slotNumber, string base64)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        if (slot.State == null || slot.State->Load == null)
            return false;

        byte[] data;

        try
        {
            data = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return false;
        }

        var stream = new MemoryStream(data, writable: false);
        var handle = GCHandle.Alloc(stream);

        try
        {
            var input = new ClapNative.IStream
            {
                Ctx = GCHandle.ToIntPtr(handle),
                Read = &StateRead,
            };

            return slot.State->Load(slot.Plugin, &input) != 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            handle.Free();
        }
    }

    // ── 参数 ──

    public List<ParameterInfo> GetParameters(int slotNumber)
    {
        var list = new List<ParameterInfo>();

        if (!mSlots.TryGetValue(slotNumber, out var slot) || slot.Params == null)
            return list;

        DrainCallback(slot);

        try
        {
            if (slot.Params->Count == null || slot.Params->GetInfo == null)
                return list;

            uint count = slot.Params->Count(slot.Plugin);

            // 读坏了的时候这里会是天文数字：不设上限就是越界读 + 卡死界面。
            if (count > 4096)
                count = 4096;

            for (uint index = 0; index < count; index++)
            {
                var info = default(ClapNative.ParamInfo);

                if (slot.Params->GetInfo(slot.Plugin, index, &info) == 0)
                    continue;

                slot.Cookies[(int)info.Id] = info.Cookie;

                double value = info.DefaultValue;

                if (slot.Params->GetValue != null)
                    slot.Params->GetValue(slot.Plugin, info.Id, &value);

                list.Add(new ParameterInfo
                {
                    Id = info.Id,
                    Name = ClapNative.FixedString(info.Name, 256),
                    Min = info.MinValue,
                    Max = info.MaxValue,
                    Value = value,
                    DefaultValue = info.DefaultValue,
                });
            }
        }
        catch (Exception)
        {
            // 插件在列参数时没了：回空表，别把异常带到协议层。
            list.Clear();
        }

        return list;
    }

    public void SetParameter(int slotNumber, long id, double value)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return;

        if (slot.Params == null || slot.PendingCount >= ClapNative.MaxEventsPerBlock)
            return;

        if (!slot.Cookies.TryGetValue((int)id, out IntPtr cookie))
            cookie = IntPtr.Zero;

        var target = &slot.Events[slot.PendingCount++];
        target->Header.Size = (uint)sizeof(ClapNative.EventParamValue);
        target->Header.Time = 0;
        target->Header.SpaceId = ClapNative.EventSpacePlugin;
        target->Header.Type = ClapNative.EventTypeParamValue;
        target->Header.Flags = 0;
        target->ParamId = (uint)id;
        target->Cookie = cookie;
        target->NoteId = -1;
        target->PortIndex = -1;
        target->Channel = -1;
        target->Key = -1;
        target->Value = value;
    }

    // ── 编辑器（P-C3）──

    public (bool Ok, string? Reason, int ChildWindows, IntPtr Window) OpenEditor(int slotNumber, string title)
        => (false, "the CLAP editor is not wired yet (this build has the parameter panel only)", 0, IntPtr.Zero);

    public bool CloseEditor(int slotNumber) => false;

    // ── 插件回调 ──

    // request_callback：插件要求"下个合适的时候调我一下 on_main_thread"。
    // 桥是单线程的，所以在下一次进槽位时兑现（参数 / 状态 / 音频都算合适的时候）。
    static void DrainCallback(Slot slot)
    {
        if (!slot.CallbackRequested)
            return;

        slot.CallbackRequested = false;

        try
        {
            if (slot.Plugin != null && slot.Plugin->OnMainThread != null)
                slot.Plugin->OnMainThread(slot.Plugin);
        }
        catch (Exception)
        {
            // 插件在自己的回调里炸了：不影响宿主，下一次音频照常。
        }
    }

    // request_restart：插件说"延迟 / 端口之类的配置变了，重启我才生效"。
    // 按规范停 → 撤 → 激活 → 开始；失败就把这一行标成坏了（直通），不把桥拖下水。
    static void Restart(Slot slot)
    {
        slot.RestartRequested = false;

        if (slot.Plugin == null)
            return;

        try
        {
            if (slot.Processing && slot.Plugin->StopProcessing != null)
                slot.Plugin->StopProcessing(slot.Plugin);

            slot.Processing = false;

            if (slot.Activated && slot.Plugin->Deactivate != null)
                slot.Plugin->Deactivate(slot.Plugin);

            slot.Activated = false;

            if (slot.Plugin->Activate == null
                || slot.Plugin->Activate(slot.Plugin, slot.SampleRate, 1, slot.MaxFrames) == 0)
            {
                slot.Broken = true;
                return;
            }

            slot.Activated = true;

            if (slot.Plugin->StartProcessing != null && slot.Plugin->StartProcessing(slot.Plugin) == 0)
            {
                slot.Broken = true;
                return;
            }

            slot.Processing = true;
            slot.LatencySamples = ReadLatency(slot);
        }
        catch (Exception)
        {
            slot.Broken = true;
        }
    }

    static Slot? SlotOf(ClapNative.Host* host)
    {
        if (host == null || host->HostData == IntPtr.Zero)
            return null;

        return GCHandle.FromIntPtr(host->HostData).Target as Slot;
    }

    // 反向 P/Invoke（插件 → 宿主）：**异常绝不能穿出去**，那会直接终止进程。
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static IntPtr HostGetExtension(ClapNative.Host* host, byte* id)
    {
        try
        {
            if (id == null)
                return IntPtr.Zero;

            string? name = Marshal.PtrToStringUTF8((IntPtr)id);

            if (string.Equals(name, ClapNative.ThreadCheckExtension, StringComparison.Ordinal))
                return sThreadCheck;

            return IntPtr.Zero;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void HostRequestRestart(ClapNative.Host* host)
    {
        try
        {
            var slot = SlotOf(host);
            if (slot != null)
                slot.RestartRequested = true;
        }
        catch (Exception)
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void HostRequestProcess(ClapNative.Host* host)
    {
        // 桥的音频是宿主推过来的（有音频才有 process）。插件要求"再跑一块"时没有额外动作：
        // 下一块马上会来。留着这个字段是为了 P-C3 的离线预览。
        try
        {
            var slot = SlotOf(host);
            if (slot != null)
                slot.ProcessRequested = true;
        }
        catch (Exception)
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void HostRequestCallback(ClapNative.Host* host)
    {
        try
        {
            var slot = SlotOf(host);
            if (slot != null)
                slot.CallbackRequested = true;
        }
        catch (Exception)
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static byte HostIsMainThread(ClapNative.Host* host) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static byte HostIsAudioThread(ClapNative.Host* host) => 1;

    // clap_ostream 的 write：插件把 state 交给我们。
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static long StateWrite(ClapNative.OStream* stream, void* buffer, ulong size)
    {
        try
        {
            if (stream == null || stream->Ctx == IntPtr.Zero)
                return -1;

            if (GCHandle.FromIntPtr(stream->Ctx).Target is not MemoryStream output)
                return -1;

            var span = new ReadOnlySpan<byte>(buffer, (int)Math.Min(size, int.MaxValue));
            output.Write(span);
            return span.Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    // clap_istream 的 read：插件从我们这儿取 state。0 = EOF（规范如此，不是失败）。
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static long StateRead(ClapNative.IStream* stream, void* buffer, ulong size)
    {
        try
        {
            if (stream == null || stream->Ctx == IntPtr.Zero)
                return -1;

            if (GCHandle.FromIntPtr(stream->Ctx).Target is not MemoryStream input)
                return -1;

            long remaining = input.Length - input.Position;

            if (remaining <= 0)
                return 0;

            int wanted = (int)Math.Min((ulong)Math.Min(remaining, int.MaxValue), size);
            var span = new Span<byte>(buffer, wanted);
            int read = input.Read(span);
            return read;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    // ── 槽位 ──

    sealed class Port
    {
        public int Channels;
        public int Frames;
        public bool IsMain;

        // float[channels][frames]（非托管）：CLAP 要的是每声道一条 float*。
        public IntPtr Planes;
        public IntPtr Data;

        // 指向上面这两块的 clap_audio_buffer_t（非托管，指针稳定）。
        public IntPtr Buffer;

        public void Release()
        {
            if (Planes != IntPtr.Zero)
                Marshal.FreeHGlobal(Planes);

            if (Data != IntPtr.Zero)
                Marshal.FreeHGlobal(Data);

            if (Buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(Buffer);

            Planes = IntPtr.Zero;
            Data = IntPtr.Zero;
            Buffer = IntPtr.Zero;
        }
    }

    sealed class Slot : IDisposable
    {
        public ClapLibrary.Lease Lease = null!;
        public ClapNative.Plugin* Plugin;

        public IntPtr HostMemory;
        public GCHandle HostHandle;

        public ClapNative.PluginAudioPorts* AudioPorts;
        public ClapNative.PluginParams* Params;
        public ClapNative.PluginState* State;
        public ClapNative.PluginLatency* Latency;

        public readonly List<Port> Inputs = [];
        public readonly List<Port> Outputs = [];
        public int MainInput = -1;
        public int MainOutput = -1;
        public int InputChannels;
        public int OutputChannels;

        public uint MaxFrames;
        public double SampleRate;
        public bool Bypassed;
        public bool Initialized;
        public bool Activated;
        public bool Processing;

        // 插件坏了 / 重启失败：这个槽位从此直通（但实例还在，还能改旁通 / 移除）。
        public bool Broken;

        public bool RestartRequested;
        public bool CallbackRequested;
        public bool ProcessRequested;

        public int LatencySamples;
        public long SteadyTime;

        // 发布给宿主的两条平面视图（宿主只关心主输出的 L/R）。
        public float[][] PublishPlanes = [new float[SharedAudioLayout.MaxBlockFrames], new float[SharedAudioLayout.MaxBlockFrames]];

        // 端口数组：clap_process 要的是连续的 clap_audio_buffer_t 数组。
        public IntPtr InputBuffers;
        public IntPtr OutputBuffers;

        // 每块要交给插件的参数事件（非托管，预分配）。
        public IntPtr EventMemory;
        public ClapNative.EventParamValue* Events;
        public int PendingCount;

        public IntPtr InEventsMemory;
        public ClapNative.InputEvents* InEventsList;
        public IntPtr OutEventsMemory;
        public ClapNative.OutputEvents* OutEventsList;

        public IntPtr ProcessMemory;
        public ClapNative.Process* ProcessPtr;

        // 事件表的 ctx = 本槽位（见 AllocateBuffers）。
        public GCHandle EventsHandle;

        // 参数 id → cookie（插件在 get_info 里给的回调凭证，set 参数时要原样带回）。
        public readonly Dictionary<int, IntPtr> Cookies = [];

        public void AllocateBuffers()
        {
            InputBuffers = AllocateBufferArray(Inputs);
            OutputBuffers = AllocateBufferArray(Outputs);

            EventMemory = Marshal.AllocHGlobal(ClapNative.MaxEventsPerBlock * sizeof(ClapNative.EventParamValue));
            Events = (ClapNative.EventParamValue*)EventMemory;

            InEventsMemory = Marshal.AllocHGlobal(sizeof(ClapNative.InputEvents));
            InEventsList = (ClapNative.InputEvents*)InEventsMemory;
            InEventsList->Size = &InputEventsSize;
            InEventsList->Get = &InputEventsGet;

            OutEventsMemory = Marshal.AllocHGlobal(sizeof(ClapNative.OutputEvents));
            OutEventsList = (ClapNative.OutputEvents*)OutEventsMemory;
            OutEventsList->TryPush = &OutputEventsTryPush;

            // 事件表要知道"问哪个槽位要事件"。事件的 ctx 指向槽位本身（GC 保留，桥里长期有效）。
            EventsHandle = GCHandle.Alloc(this);
            InEventsList->Ctx = GCHandle.ToIntPtr(EventsHandle);
            OutEventsList->Ctx = GCHandle.ToIntPtr(EventsHandle);

            ProcessMemory = Marshal.AllocHGlobal(sizeof(ClapNative.Process));
            ProcessPtr = (ClapNative.Process*)ProcessMemory;
        }

        static IntPtr AllocateBufferArray(List<Port> ports)
        {
            IntPtr memory = Marshal.AllocHGlobal(Math.Max(1, ports.Count) * sizeof(ClapNative.AudioBuffer));
            var buffers = (ClapNative.AudioBuffer*)memory;

            for (int i = 0; i < ports.Count; i++)
                buffers[i] = *(ClapNative.AudioBuffer*)ports[i].Buffer;

            return memory;
        }

        public void Dispose()
        {
            var plugin = Plugin;

            if (plugin != null)
            {
                try
                {
                    if (Processing && plugin->StopProcessing != null)
                        plugin->StopProcessing(plugin);
                }
                catch (Exception)
                {
                }

                try
                {
                    if (Activated && plugin->Deactivate != null)
                        plugin->Deactivate(plugin);
                }
                catch (Exception)
                {
                }

                try
                {
                    if (plugin->Destroy != null)
                        plugin->Destroy(plugin);
                }
                catch (Exception)
                {
                }
            }

            Plugin = null;

            // 顺序：先释放点子，再解引用库（destroy 之后插件不再碰这些）。
            foreach (var port in Inputs)
                port.Release();

            foreach (var port in Outputs)
                port.Release();

            Inputs.Clear();
            Outputs.Clear();

            Free(ref InputBuffers);
            Free(ref OutputBuffers);
            Free(ref EventMemory);
            Free(ref InEventsMemory);
            Free(ref OutEventsMemory);
            Free(ref ProcessMemory);
            Free(ref HostMemory);

            if (HostHandle.IsAllocated)
                HostHandle.Free();

            if (EventsHandle.IsAllocated)
                EventsHandle.Free();

            Lease?.Dispose();
            Lease = null!;
        }

        static void Free(ref IntPtr memory)
        {
            if (memory != IntPtr.Zero)
                Marshal.FreeHGlobal(memory);

            memory = IntPtr.Zero;
        }
    }

    // 输入事件表：把槽位里攒下的参数事件交给插件。
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static uint InputEventsSize(ClapNative.InputEvents* list)
    {
        try
        {
            var slot = list->Ctx == IntPtr.Zero ? null : GCHandle.FromIntPtr(list->Ctx).Target as Slot;
            return (uint)(slot?.PendingCount ?? 0);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static ClapNative.EventHeader* InputEventsGet(ClapNative.InputEvents* list, uint index)
    {
        try
        {
            var slot = list->Ctx == IntPtr.Zero ? null : GCHandle.FromIntPtr(list->Ctx).Target as Slot;

            if (slot == null || index >= (uint)slot.PendingCount)
                return null;

            return (ClapNative.EventHeader*)&slot.Events[index];
        }
        catch (Exception)
        {
            return null;
        }
    }

    // 输出事件：插件上报的参数变化 / 音符。P-C2 不需要（宿主不跑自动化），收下丢掉。
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static byte OutputEventsTryPush(ClapNative.OutputEvents* list, ClapNative.EventHeader* header) => 1;
}
