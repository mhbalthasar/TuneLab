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
// 【编辑器】P2：桥侧开插件 GUI。VST3 的窗口由插件自己建，消息泵必须待在**建窗的那条线程**
// 上，而桥的主线程正阻塞读管道 —— 所以 Vst3EditorWindow 单起一条 STA 线程：开窗 → 泵消息 →
// 关窗，并把插件窗口的 HWND 经协议回传宿主（宿主用它置前 / 判"用户是否已经关掉"）。
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

    public (bool Ok, string? Reason, string? Name, string? Vendor, int LatencySamples, bool IsSynth,
        bool HasEditor, bool NeedsOpenGl) Load(
        LoadPluginMessage request, out int inputChannels, out int outputChannels)
    {
        inputChannels = 0;
        outputChannels = 0;

        if (mSlots.ContainsKey(request.Slot))
            Unload(request.Slot);

        if (!IsSupported)
            return (false, "the VST3 host library is missing in this bridge", null, null, 0, false, false, false);

        if (!File.Exists(request.Path))
            return (false, "plugin file not found", null, null, 0, false, false, false);

        mBackend ??= new Vst3Backend();
        if (!mBackend.IsAvailable)
            return (false, mBackend.UnavailableReason ?? "the VST3 host library is missing", null, null, 0, false, false, false);

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
                return (false, reason ?? "the plugin could not be created", null, null, 0, false, false, false);

            insert = created;
        }
        catch (Exception ex)
        {
            return (false, "unexpected error while loading the plugin: " + ex.Message, null, null, 0, false, false, false);
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

        // [P2] 桥侧开插件 GUI：HasEditor / NeedsOpenGl 如实回报，宿主据此决定
        // "开 GUI 还是退回参数面板"（NeedsOpenGl 为真而机器没有 GL 时退面板）。
        bool hasEditor;
        bool needsOpenGl;
        try
        {
            hasEditor = insert.HasEditor;
            needsOpenGl = insert.NeedsOpenGl;
        }
        catch (Exception)
        {
            hasEditor = false;
            needsOpenGl = false;
        }

        return (true, null, insert.Name, insert.Vendor, insert.LatencySamples, false, hasEditor, needsOpenGl);
    }

    public bool Unload(int slotNumber)
    {
        if (!mSlots.Remove(slotNumber, out var slot))
            return false;

        // 编辑器窗口挂着插件自己的 view：必须在 insert.Dispose()（FreeLibrary）之前摘掉，
        // 否则关窗时插件会碰已经释放的组件（与 VST2 那条路同一个顺序纪律）。
        slot.Editor?.Dispose();
        slot.Editor = null;

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

    // [P2] 开插件 GUI：插件自己开的顶层窗在桥进程里，这里起一条 STA 线程开窗 + 泵消息，
    // 把 HWND 经协议回传宿主（宿主用它置前 / 判"用户是否已经关掉"）。
    // 失败原因（没有界面 / 拒绝打开）原样带回去，界面据此退回参数面板。
    public (bool Ok, string? Reason, int ChildWindows, IntPtr Window) OpenEditor(int slotNumber, string title)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return (false, "slot not found", 0, IntPtr.Zero);

        bool hasEditor;
        try
        {
            hasEditor = slot.Insert.HasEditor;
        }
        catch (Exception)
        {
            hasEditor = false;
        }

        if (!hasEditor)
            return (false, "the plugin has no editor", 0, IntPtr.Zero);

        // 已经开着：带回前台，不重建（重建会丢插件窗口里的临时状态）。
        if (slot.Editor != null)
        {
            if (slot.Editor.IsAlive)
            {
                slot.Editor.BringToFront();
                return (true, null, slot.Editor.ChildWindowCount, slot.Editor.Handle);
            }

            // 用户已经点 ✕ 关掉了：窗口与消息泵都没了，当"没开"重来。
            slot.Editor.Dispose();
            slot.Editor = null;
        }

        if (!Vst3EditorWindow.TryOpen(slot.Insert, out var editor) || editor == null)
            return (false, "the plugin refused to open its editor", 0, IntPtr.Zero);

        slot.Editor = editor;
        return (true, null, editor.ChildWindowCount, editor.Handle);
    }

    public bool CloseEditor(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        slot.Editor?.Dispose();
        slot.Editor = null;
        return true;
    }

    sealed class Slot
    {
        public Vst3Insert Insert = null!;

        // [P2] 这个槽位当前开着的编辑器窗口（没开 = null）。
        public Vst3EditorWindow? Editor;

        public int Frames;
        public bool Bypassed;

        // 每块搬运用的预分配缓冲（不随块分配）。
        public float[] Interleaved = [];
        public float[] Sidechain = [];

        // 发布给宿主的 2 条平面视图。
        public float[][] PublishPlanes = [];
    }
}
