using System;
using System.Collections.Generic;
using System.IO;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.VstBridge;

// 桥进程里的 VST3 宿主：把 VST3 的加载 / 处理 / 状态 / 参数搬进桥进程，
// 插件崩或挂都只死桥（VST3-BRIDGE.md）。
//
// 【为什么不自己写一遍 wrapper】InsertHost 里已经有完整的 Vst3Backend + Vst3Insert
// （vendored OwnAudioVST3、声道/侧链、NaN 清理、state/参数）。桥这边只是"另一个进程里
// 的同一段装配"：复用它们，隔离这件事就只剩协议与共享环的搬运，而不是把 VST3 逻辑抄第二遍。
//
// 【编辑器】P1 不开插件 GUI：VST3 的窗口由插件自己建，消息泵必须待在**建窗的那条线程**上，
// 而桥的主线程正阻塞读管道。把它做对需要独立的 STA 线程 + Win32 消息循环（P2）。
// 在此之前 HasEditor 一律回 false，宿主自然走参数面板，不会给用户一个按下去没反应的按钮。
//
// 【格式可用性】只有把 ownvst3.tlhost.dll 发到身边的桥才支持 VST3。x64 桥有，x86 桥没有；
// Host 通过 HelloAck.Formats 看到这一点，不会把 VST3 发给一个不支持的桥。
internal sealed class Vst3PluginHost : IBridgePluginHost
{
    static readonly string? sNativePath = FindNativeLibrary();

    // 这个桥能不能承载 VST3。x86 桥里为 false（没有 win-x64 的 native）。
    public static bool IsSupported => sNativePath != null;

    readonly Dictionary<int, Slot> mSlots = [];
    SharedAudioRing? mIn;
    SharedAudioRing? mOut;
    Vst3Backend? mBackend;

    // native 库与 Vst3Backend 的查找规则一致：exe 同目录的 ownvst3.tlhost.dll。
    // 只读文件是否存在，不加载 —— 探测本身不该有任何副作用。
    static string? FindNativeLibrary()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return null;

        string beside = Path.Combine(AppContext.BaseDirectory, Vst3Backend.NativeFileName);
        return File.Exists(beside) ? beside : null;
    }

    public void Attach(SharedAudioRing input, SharedAudioRing output)
    {
        mIn = input;
        mOut = output;
    }

    public (bool Ok, string? Reason, string? Name, string? Vendor, int LatencySamples, bool IsSynth, bool HasEditor) Load(
        LoadPluginMessage request, out int inputChannels, out int outputChannels)
    {
        inputChannels = 0;
        outputChannels = 0;

        if (mSlots.ContainsKey(request.Slot))
            Unload(request.Slot);

        if (!IsSupported)
            return (false, "the VST3 host library is missing in this bridge", null, null, 0, false, false);

        if (!File.Exists(request.Path))
            return (false, "plugin file not found", null, null, 0, false, false);

        mBackend ??= new Vst3Backend();
        if (!mBackend.IsAvailable)
            return (false, mBackend.UnavailableReason ?? "the VST3 host library is missing", null, null, 0, false, false);

        // 桥只认路径；名字 / 厂商由插件自己报。用文件名占位，真实名字在 Create 之后从 wrapper 读不到，
        // 但 Vst3Insert.Name 就是插件名（后端把 InsertPluginInfo.Name 带进去）——见下。
        var info = new InsertPluginInfo
        {
            Format = "VST3",
            PluginId = "vst3:" + Path.GetFileNameWithoutExtension(request.Path),
            Name = Path.GetFileNameWithoutExtension(request.Path),
            Locator = request.Path,
        };

        // 块长不能超过共享环单块容量：超了 TryPublish 直接失败、那一块白直通。
        int block = Math.Clamp(request.MaxBlock, 32, SharedAudioLayout.MaxBlockFrames);

        var settings = new InsertSettings
        {
            Bypass = request.Bypass,
            SampleRate = request.SampleRate > 0 ? request.SampleRate : 48000,
            MaxBlockSize = block,
        };

        Vst3Insert insert;
        try
        {
            if (mBackend.Create(info, settings, out string? reason) is not Vst3Insert created)
                return (false, reason ?? "the plugin could not be created", null, null, 0, false, false);

            insert = created;
        }
        catch (Exception ex)
        {
            return (false, "unexpected error while loading the plugin: " + ex.Message, null, null, 0, false, false);
        }

        var slot = new Slot
        {
            Insert = insert,
            Frames = block,
            Bypassed = request.Bypass,
            Interleaved = new float[block * 2],
            Sidechain = new float[block * 2],
            PublishPlanes = new float[2][],
        };

        for (int c = 0; c < 2; c++)
            slot.PublishPlanes[c] = new float[block];

        mSlots[request.Slot] = slot;

        inputChannels = insert.InputChannels;
        outputChannels = 2;

        // HasEditor 回 false：这个版本不开插件 GUI（见文件头）。宿主据此走参数面板。
        return (true, null, insert.Name, insert.Vendor, insert.LatencySamples, false, false);
    }

    public bool Unload(int slotNumber)
    {
        if (!mSlots.Remove(slotNumber, out var slot))
            return false;

        try
        {
            slot.Insert.Dispose();
        }
        catch (Exception)
        {
            // 卸载阶段插件抛错：句柄马上要没，吞掉。
        }

        return true;
    }

    public bool Process(int slotNumber, bool bypass, out int frames, out int channels)
    {
        frames = 0;
        channels = 0;

        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        if (mIn == null || !mIn.TryRead(0, out _, out var inPlanes, out int ch, out int fr))
            return false;

        frames = fr;
        channels = ch;

        try
        {
            if (bypass || slot.Bypassed)
            {
                // 直通：输入原样发回去。
                mOut?.TryPublish(slotNumber, inPlanes, ch, fr, 1000);
                return true;
            }

            int n = Math.Min(fr, slot.Frames);

            // 平面 → 交织（Vst3Insert 的 Process 要的是交织立体声）。
            var interleaved = slot.Interleaved;
            float[] left = inPlanes[0];
            float[] right = inPlanes.Length > 1 ? inPlanes[1] : inPlanes[0];
            for (int i = 0; i < n; i++)
            {
                interleaved[i * 2] = left[i];
                interleaved[i * 2 + 1] = right[i];
            }

            // 侧链：第 3/4 条平面重建成交织键，交给 Vst3Insert（它按自己的声明宽度决定用不用）。
            ReadOnlySpan<float> sidechain = default;
            if (ch > 2 && slot.Insert.SidechainChannels > 0)
            {
                var sc = slot.Sidechain;
                float[] scL = ch > 2 && inPlanes.Length > 2 ? inPlanes[2] : inPlanes[0];
                float[] scR = ch > 3 && inPlanes.Length > 3 ? inPlanes[3] : scL;
                for (int i = 0; i < n; i++)
                {
                    sc[i * 2] = scL[i];
                    sc[i * 2 + 1] = scR[i];
                }

                sidechain = new ReadOnlySpan<float>(sc, 0, n * 2);
            }

            bool ok;
            try
            {
                slot.Insert.Process(new Span<float>(interleaved, 0, n * 2), 0, n, sidechain);
                ok = true;
            }
            catch (Exception)
            {
                ok = false;
            }

            if (!ok)
            {
                // 插件炸了：这一块直通，用户至少还听得见原来的东西。
                mOut?.TryPublish(slotNumber, inPlanes, ch, fr, 1000);
                return false;
            }

            // 交织 → 平面（VST3 输出固定立体声；NaN/Inf 就地清零，见 Vst3Insert 的同一处注释）。
            for (int i = 0; i < n; i++)
            {
                float lo = interleaved[i * 2];
                float ro = interleaved[i * 2 + 1];
                slot.PublishPlanes[0][i] = float.IsFinite(lo) ? lo : 0f;
                slot.PublishPlanes[1][i] = float.IsFinite(ro) ? ro : 0f;
            }

            mOut?.TryPublish(slotNumber, slot.PublishPlanes, 2, n, 1000);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            mIn.Release();
        }
    }

    public byte[]? GetState(int slotNumber)
        => mSlots.TryGetValue(slotNumber, out var slot) ? slot.Insert.SaveState() : null;

    public bool SetState(int slotNumber, string base64)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        byte[]? data;
        try
        {
            data = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return false;
        }

        return slot.Insert.LoadState(data);
    }

    public List<ParameterInfo> GetParameters(int slotNumber)
    {
        var list = new List<ParameterInfo>();

        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return list;

        try
        {
            foreach (var parameter in slot.Insert.Parameters)
            {
                list.Add(new ParameterInfo
                {
                    Id = parameter.Id,
                    Name = parameter.Name ?? string.Empty,
                    Min = parameter.MinValue,
                    Max = parameter.MaxValue,
                    Value = parameter.CurrentValue,
                    DefaultValue = parameter.DefaultValue,
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
        if (mSlots.TryGetValue(slotNumber, out var slot))
            slot.Insert.SetParameter((int)id, value);
    }

    // P1：不开插件 GUI（见文件头）。宿主收到 false 会退回参数面板。
    public (bool Ok, string? Reason, int ChildWindows) OpenEditor(int slotNumber, string title)
        => (false, "the isolated VST3 host does not open plugin GUIs yet", 0);

    public bool CloseEditor(int slotNumber) => false;

    sealed class Slot
    {
        public Vst3Insert Insert = null!;
        public int Frames;
        public bool Bypassed;

        // 每块搬运用的预分配缓冲（不随块分配）。
        public float[] Interleaved = [];
        public float[] Sidechain = [];

        // 发布给宿主的 2 条平面视图。
        public float[][] PublishPlanes = [];
    }
}
