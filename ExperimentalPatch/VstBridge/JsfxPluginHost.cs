using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.VstBridge;

// 桥进程里的 JSFX 宿主：加载 .jsfx、跑音频、滑块参数、状态保存。
//
// 【边界】宿主侧只做文本扫描（desc / 路径），EEL2 的解析与执行全部交给 ysfx。
// 这个类只做三件事：把共享环的平面缓冲交给 ysfx、把 slider 映射成参数、
// 把 ysfx_state_t 编成桥协议里的 byte[]。
//
// 【线程】桥是单线程的（见 ClapPluginHost 的同一段说明）。ysfx 的实例不会跨线程。
internal sealed unsafe class JsfxPluginHost : IBridgePluginHost
{
    readonly Dictionary<int, Slot> mSlots = [];
    SharedAudioRing? mIn;
    SharedAudioRing? mOut;

    public static bool IsSupported => JsfxNative.IsSupported;

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
        // 加载 / 编译期间收集 ysfx 自己的错误信息（见 OnYsfxLog）。finally 兜住所有返回路径：
        // 插件跑起来之后每一块出的错不该再进托管回调。
        sLastLog = null;
        sCapturing = true;

        try
        {
            return LoadCore(request, out inputChannels, out outputChannels);
        }
        finally
        {
            sCapturing = false;
        }
    }

    // 桥是单线程的（见类注释），这些静态字段不需要线程同步。
    static readonly JsfxNative.LogReporter sLogReporter = OnYsfxLog;
    static volatile bool sCapturing;
    static string? sLastLog;

    // ysfx 日志回调。只留 error：warning 里有"缺 desc"这类噪音，会把真正的失败原因挤掉。
    static void OnYsfxLog(IntPtr userdata, int level, IntPtr message)
    {
        try
        {
            if (!sCapturing || level < 2 || message == IntPtr.Zero)
                return;

            string? text = Marshal.PtrToStringUTF8(message);

            if (!string.IsNullOrEmpty(text))
                sLastLog = text;
        }
        catch (Exception)
        {
            // 这是从 native 里调过来的：抛出去 = 直接崩桥。日志而已，吞掉。
        }
    }

    // "（ysfx 的原因）"后缀。没有就退化成空串。
    static string LogSuffix() => string.IsNullOrEmpty(sLastLog) ? string.Empty : ": " + sLastLog;

    (bool Ok, string? Reason, string? Name, string? Vendor, int LatencySamples, bool IsSynth,
        bool HasEditor, bool NeedsOpenGl) LoadCore(
        LoadPluginMessage request, out int inputChannels, out int outputChannels)
    {
        inputChannels = 0;
        outputChannels = 0;

        if (mSlots.ContainsKey(request.Slot))
            Unload(request.Slot);

        if (!Environment.Is64BitProcess)
            return Fail("32-bit JSFX hosts are not supported");

        if (!JsfxNative.IsSupported)
            return Fail("the ysfx runtime is not installed in this bridge");

        if (!File.Exists(request.Path))
            return Fail("the JSFX file does not exist");

        IntPtr config = JsfxNative.ysfx_config_new();

        if (config == IntPtr.Zero)
            return Fail("ysfx could not create a configuration");

        IntPtr fx = IntPtr.Zero;

        try
        {
            // 失败原因（缺 import、EEL2 第几行）由 ysfx 自己说出来，比"加载失败"有用得多。
            JsfxNative.ysfx_set_log_reporter(config, sLogReporter);

            // 先按文件位置猜 Data / Effects 根，再用宿主显式给的 import 根覆盖。
            // 这样 @serialize 之外的文件函数仍能按 JSFX 的目录约定找到 Data。
            JsfxNative.ysfx_guess_file_roots(config, request.Path);

            if (!string.IsNullOrWhiteSpace(request.ImportRoot))
                JsfxNative.ysfx_set_import_root(config, request.ImportRoot);

            fx = JsfxNative.ysfx_new(config);

            if (fx == IntPtr.Zero)
            {
                JsfxNative.ysfx_config_free(config);
                return Fail("ysfx could not create an instance");
            }

            if (!JsfxNative.ysfx_load_file(fx, request.Path, 0))
            {
                JsfxNative.ysfx_free(fx);
                JsfxNative.ysfx_config_free(config);
                return Fail("ysfx could not load the JSFX file" + LogSuffix());
            }

            if (!JsfxNative.ysfx_compile(fx, 0))
            {
                JsfxNative.ysfx_free(fx);
                JsfxNative.ysfx_config_free(config);
                return Fail("the JSFX did not compile" + LogSuffix());
            }

            uint inputs = JsfxNative.ysfx_get_num_inputs(fx);
            uint outputs = JsfxNative.ysfx_get_num_outputs(fx);

            if (inputs != 2 || outputs != 2)
            {
                JsfxNative.ysfx_free(fx);
                JsfxNative.ysfx_config_free(config);
                return Fail($"JSFX inserts require 2 inputs and 2 outputs (this one declares {inputs}/{outputs})");
            }

            int maxFrames = Math.Clamp(request.MaxBlock, 32, SharedAudioLayout.MaxBlockFrames);
            double sampleRate = request.SampleRate > 0 ? request.SampleRate : 48000;

            JsfxNative.ysfx_set_sample_rate(fx, sampleRate);
            JsfxNative.ysfx_set_block_size(fx, (uint)maxFrames);

            // 扫描探针：到这里就够了 —— 文件加载得动、EEL2 编译得过、引脚是 2 进 2 出。
            // 有意不跑 ysfx_init：@init 是用户代码（有的在里面读采样文件、开大缓冲），
            // 扫描时挨个执行既慢，又给桥（正跑着用户的音频）添上一个本来没有的崩溃面。
            if (!request.ProbeOnly)
                JsfxNative.ysfx_init(fx);

            bool hasEditor = HasGfxSection(fx);

            var slot = new Slot
            {
                Config = config,
                Fx = fx,
                SampleRate = sampleRate,
                MaxFrames = maxFrames,
                Bypassed = request.Bypass,
                HasEditor = hasEditor,
            };

            mSlots[request.Slot] = slot;

            inputChannels = 2;
            outputChannels = 2;

            string? name = PtrToString(JsfxNative.ysfx_get_name(fx));
            string? author = PtrToString(JsfxNative.ysfx_get_author(fx));
            int latency = ReadLatency(fx);

            return (true, null, string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(request.Path) : name,
                string.IsNullOrWhiteSpace(author) ? string.Empty : author, latency, false, hasEditor, false);
        }
        catch (Exception ex)
        {
            if (fx != IntPtr.Zero)
                JsfxNative.ysfx_free(fx);

            JsfxNative.ysfx_config_free(config);
            return Fail("unexpected error while loading the JSFX: " + ex.Message);
        }
    }

    static (bool, string?, string?, string?, int, bool, bool, bool) Fail(string reason)
        => (false, reason, null, null, 0, false, false, false);

    static int ReadLatency(IntPtr fx)
    {
        try
        {
            double delay = JsfxNative.ysfx_get_pdc_delay(fx);
            return double.IsFinite(delay) && delay > 0 ? (int)Math.Round(delay) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // 旧版 ysfx 没有这个导出；缺导出时只是没有界面，不能把 JSFX 加载打挂。
    static bool HasGfxSection(IntPtr fx)
    {
        try
        {
            return JsfxNative.ysfx_has_section(fx, JsfxNative.SectionGfx);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool Unload(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        // 先关窗口，再释放 ysfx 实例。窗口自己持有一个引用，但让 gfx 线程
        // 先停下来仍然更安全：它不会再访问槽位里的指针。
        CloseEditor(slotNumber);

        mSlots.Remove(slotNumber);
        slot.Dispose();
        return true;
    }

    // ── 音频 ──

    public bool Process(int slotNumber, bool bypass, int tag, out int frames, out int channels)
    {
        frames = 0;
        channels = 0;

        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        // 只认 tag 对得上的输入；超时留下的过期块直接丢弃（见 BlockHeader.Tag）。
        if (mIn == null || !mIn.TryReadMatching(tag, 0, out _, out var inPlanes, out int channelCount,
            out int frameCount))
            return false;

        frames = frameCount;
        channels = channelCount;

        try
        {
            if (bypass || slot.Bypassed)
            {
                mOut?.TryPublish(slotNumber, inPlanes, channelCount, frameCount, 1000, tag);
                return true;
            }

            int n = Math.Min(frameCount, slot.MaxFrames);

            FillInputs(slot, inPlanes, channelCount, n);
            Array.Clear(slot.OutL, 0, n);
            Array.Clear(slot.OutR, 0, n);

            fixed (float* inL = slot.InL)
            fixed (float* inR = slot.InR)
            fixed (float* outL = slot.OutL)
            fixed (float* outR = slot.OutR)
            {
                float** ins = stackalloc float*[2];
                float** outs = stackalloc float*[2];

                ins[0] = inL;
                ins[1] = inR;
                outs[0] = outL;
                outs[1] = outR;

                JsfxNative.ysfx_process_float(slot.Fx, ins, outs, 2, 2, (uint)n);
            }

            for (int i = 0; i < n; i++)
            {
                float left = slot.OutL[i];
                float right = slot.OutR[i];

                slot.PublishPlanes[0][i] = float.IsFinite(left) ? left : 0f;
                slot.PublishPlanes[1][i] = float.IsFinite(right) ? right : 0f;
            }

            mOut?.TryPublish(slotNumber, slot.PublishPlanes, 2, n, 1000, tag);
            return true;
        }
        catch (Exception)
        {
            // JSFX 在 process 里抛错（托管侧不该发生）：这一块直通，桥继续用。
            try
            {
                mOut?.TryPublish(slotNumber, inPlanes, channelCount, frameCount, 1000, tag);
            }
            catch (Exception)
            {
            }

            return true;
        }
        finally
        {
            mIn.Release();
        }
    }

    static void FillInputs(Slot slot, float[][] inPlanes, int channelCount, int frames)
    {
        CopyPlane(slot.InL, inPlanes, 0, channelCount, frames);
        CopyPlane(slot.InR, inPlanes, 1, channelCount, frames);
    }

    static void CopyPlane(float[] destination, float[][] source, int channel, int channelCount, int frames)
    {
        Array.Clear(destination, 0, frames);

        if (source.Length == 0)
            return;

        int index = channel < channelCount ? channel : Math.Max(0, channelCount - 1);
        if (index >= source.Length)
            index = 0;

        var data = source[index];
        int copy = Math.Min(frames, data.Length);

        if (copy > 0)
            Array.Copy(data, destination, copy);
    }

    // ── 状态 ──

    // 状态字节布局（两端必须逐字节一致）：
    //   int32 version, int32 sliderCount, int32 dataSize,
    //   sliderCount × (uint32 index, double value), data[dataSize]
    const int StateVersion = 1;
    const int MaxStateBytes = 8 * 1024 * 1024;
    const int MaxSliders = 64;

    public byte[]? GetState(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return null;

        IntPtr statePointer = JsfxNative.ysfx_save_state(slot.Fx);

        if (statePointer == IntPtr.Zero)
            return null;

        try
        {
            var state = *(JsfxNative.State*)statePointer;

            if (state.SliderCount > MaxSliders || state.DataSize > MaxStateBytes)
                return null;

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            writer.Write(StateVersion);
            writer.Write((int)state.SliderCount);
            writer.Write((int)state.DataSize);

            var sliders = (JsfxNative.StateSlider*)state.Sliders;

            for (int i = 0; i < (int)state.SliderCount; i++)
            {
                writer.Write(sliders[i].Index);
                writer.Write(sliders[i].Value);
            }

            if (state.DataSize > 0)
                writer.Write(new ReadOnlySpan<byte>((void*)state.Data, (int)state.DataSize));

            writer.Flush();
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            JsfxNative.ysfx_state_free(statePointer);
        }
    }

    public bool SetState(int slotNumber, string base64)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
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

        IntPtr slidersPointer = IntPtr.Zero;
        IntPtr dataPointer = IntPtr.Zero;

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(stream);

            if (reader.ReadInt32() != StateVersion)
                return false;

            int count = reader.ReadInt32();

            if (count < 0 || count > MaxSliders)
                return false;

            int dataSize = reader.ReadInt32();

            if (dataSize < 0 || dataSize > MaxStateBytes)
                return false;

            slidersPointer = count > 0
                ? Marshal.AllocHGlobal(count * sizeof(JsfxNative.StateSlider))
                : IntPtr.Zero;

            // ysfx 会把 data 当 std::string 的源；size 为 0 时也要有一个合法指针。
            dataPointer = Marshal.AllocHGlobal(Math.Max(1, dataSize));

            var sliders = (JsfxNative.StateSlider*)slidersPointer;

            for (int i = 0; i < count; i++)
            {
                uint index = reader.ReadUInt32();
                double value = reader.ReadDouble();

                if (index >= MaxSliders)
                    return false;

                sliders[i].Index = index;
                sliders[i].Value = value;
            }

            if (dataSize > 0)
            {
                byte[] serialized = reader.ReadBytes(dataSize);

                if (serialized.Length != dataSize)
                    return false;

                Marshal.Copy(serialized, 0, dataPointer, dataSize);
            }

            var state = new JsfxNative.State
            {
                Sliders = slidersPointer,
                SliderCount = (uint)count,
                Data = dataPointer,
                DataSize = (nuint)dataSize,
            };

            return JsfxNative.ysfx_load_state(slot.Fx, ref state);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (slidersPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(slidersPointer);

            if (dataPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(dataPointer);
        }
    }

    // ── 参数 ──

    public List<ParameterInfo> GetParameters(int slotNumber)
    {
        var list = new List<ParameterInfo>();

        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return list;

        try
        {
            for (uint index = 0; index < (uint)MaxSliders; index++)
            {
                if (!JsfxNative.ysfx_slider_exists(slot.Fx, index))
                    continue;

                string? name = PtrToString(JsfxNative.ysfx_slider_get_name(slot.Fx, index));

                double min = 0;
                double max = 1;
                double defaultValue = 0;

                if (JsfxNative.ysfx_slider_get_curve(slot.Fx, index, out var curve))
                {
                    min = curve.Min;
                    max = curve.Max;
                    defaultValue = curve.Default;
                }

                list.Add(new ParameterInfo
                {
                    Id = index,
                    Name = string.IsNullOrWhiteSpace(name) ? $"slider{index + 1}" : name,
                    Min = min,
                    Max = max,
                    Value = JsfxNative.ysfx_slider_get_value(slot.Fx, index),
                    DefaultValue = defaultValue,
                });
            }
        }
        catch (Exception)
        {
            list.Clear();
        }

        return list;
    }

    public void SetParameter(int slotNumber, long id, double value)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return;

        if (id < 0 || id >= MaxSliders)
            return;

        try
        {
            if (JsfxNative.ysfx_slider_exists(slot.Fx, (uint)id))
                JsfxNative.ysfx_slider_set_value(slot.Fx, (uint)id, value, true);
        }
        catch (Exception)
        {
            // 参数设置失败只影响这一次改动，不把桥拖下水。
        }
    }

    // ── @gfx 编辑器 ──
    //
    // 窗口由 ysfx 自己创建（Win32 / SWELL），所以桥这边只需要一条专用 UI 线程：
    // 创建 → 阻塞跑事件循环 → 关闭后销毁。主线程仍只等宿主请求，音频线程不碰
    // 窗口；ysfx 的 @gfx 与 @block/@sample 本来就被设计成可以并发。

    public (bool Ok, string? Reason, int ChildWindows, IntPtr Window) OpenEditor(int slotNumber, string title)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return (false, "the JSFX slot is not loaded", 0, IntPtr.Zero);

        if (!slot.HasEditor)
            return (false, "the JSFX has no @gfx section", 0, IntPtr.Zero);

        var existing = slot.Editor;

        if (existing != null)
        {
            if (existing.IsAlive && existing.Window != IntPtr.Zero
                && JsfxNative.ysfx_gfx_window_is_open(existing.Window))
            {
                return (true, null, 0, existing.NativeHandle);
            }

            CloseEditor(slotNumber);
        }

        var session = new EditorSession();
        slot.Editor = session;

        var thread = new Thread(() => RunEditor(slot, session, title))
        {
            IsBackground = true,
            Name = "JSFX @gfx window",
        };

        try
        {
            if (OperatingSystem.IsWindows())
                thread.SetApartmentState(ApartmentState.STA);
        }
        catch (PlatformNotSupportedException)
        {
        }

        session.Thread = thread;
        thread.Start();

        if (!session.Ready.Wait(5000))
        {
            slot.Editor = null;
            return (false, "the @gfx window did not finish opening", 0, IntPtr.Zero);
        }

        if (session.Failed || session.Window == IntPtr.Zero)
        {
            string reason = session.Error ?? "the @gfx window could not be created";
            slot.Editor = null;

            if (session.Thread is { IsAlive: true } failedThread)
                failedThread.Join(1000);

            if (!session.IsAlive)
                session.Dispose();

            return (false, reason, 0, IntPtr.Zero);
        }

        return (true, null, 0, session.NativeHandle);
    }

    void RunEditor(Slot slot, EditorSession session, string title)
    {
        IntPtr titlePointer = IntPtr.Zero;
        IntPtr window = IntPtr.Zero;

        try
        {
            uint width = 0;
            uint height = 0;

            try
            {
                var dim = new uint[2];
                if (JsfxNative.ysfx_get_gfx_dim(slot.Fx, dim))
                {
                    width = dim[0];
                    height = dim[1];
                }
            }
            catch (Exception)
            {
                // 没有这个导出时交给窗口的默认尺寸。
            }

            titlePointer = Marshal.StringToCoTaskMemUTF8(title);

            var desc = new JsfxNative.GfxWindowDesc
            {
                Title = titlePointer,
                Width = width,
                Height = height,
                ScaleFactor = 1.0,
                Flags = JsfxNative.GfxWindowFlagResizable,
            };

            window = JsfxNative.ysfx_gfx_window_create(slot.Fx, ref desc);

            if (window == IntPtr.Zero)
            {
                session.Fail("ysfx could not create the @gfx window");
                return;
            }

            IntPtr nativeHandle = JsfxNative.ysfx_gfx_window_get_native_handle(window);
            session.SetWindow(window, nativeHandle);
            session.Ready.Set();

            JsfxNative.ysfx_gfx_window_run(window);
        }
        catch (Exception ex)
        {
            session.Fail("unexpected error while opening the @gfx window: " + ex.Message);
        }
        finally
        {
            if (titlePointer != IntPtr.Zero)
                Marshal.FreeCoTaskMem(titlePointer);

            if (window != IntPtr.Zero)
            {
                try
                {
                    JsfxNative.ysfx_gfx_window_destroy(window);
                }
                catch (Exception)
                {
                }

                session.SetWindow(IntPtr.Zero, IntPtr.Zero);
            }

            session.Ready.Set();
            session.Closed.Set();
        }
    }

    public bool CloseEditor(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        var session = slot.Editor;

        if (session == null)
            return false;

        var window = session.Window;

        if (window != IntPtr.Zero)
        {
            try
            {
                JsfxNative.ysfx_gfx_window_request_close(window);
            }
            catch (Exception)
            {
            }
        }

        session.Closed.Wait(3000);

        if (session.Thread is { IsAlive: true } thread)
            thread.Join(1000);

        if (session.Thread == null || !session.Thread.IsAlive)
            session.Dispose();

        slot.Editor = null;
        return true;
    }

    static string? PtrToString(IntPtr pointer)
        => pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);

    // 一个 @gfx 窗口的 UI 线程状态。窗口句柄只在创建线程里写，主线程通过
    // lock 读取；Ready/Closed 负责把"已经建好"和"已经销毁"这两个时刻同步出去。
    sealed class EditorSession : IDisposable
    {
        readonly object mLock = new();
        IntPtr mWindow;
        IntPtr mNativeHandle;

        public readonly ManualResetEventSlim Ready = new(false);
        public readonly ManualResetEventSlim Closed = new(false);

        public Thread? Thread;
        public string? Error;
        public bool Failed;

        public bool IsAlive => Thread is { IsAlive: true };

        // ysfx_gfx_window_t*，只给 request_close / is_open 用。
        public IntPtr Window
        {
            get
            {
                lock (mLock)
                    return mWindow;
            }
        }

        // 真正的 HWND；宿主用它置前 / 判断窗口是否还在。
        public IntPtr NativeHandle
        {
            get
            {
                lock (mLock)
                    return mNativeHandle;
            }
        }

        public void SetWindow(IntPtr window, IntPtr nativeHandle)
        {
            lock (mLock)
            {
                mWindow = window;
                mNativeHandle = nativeHandle;
            }
        }

        public void Fail(string reason)
        {
            Error = reason;
            Failed = true;
            Ready.Set();
        }

        public void Dispose()
        {
            Ready.Dispose();
            Closed.Dispose();
        }
    }

    sealed class Slot : IDisposable
    {
        public IntPtr Config;
        public IntPtr Fx;
        public double SampleRate;
        public int MaxFrames;
        public bool Bypassed;
        public bool HasEditor;
        public EditorSession? Editor;

        public readonly float[] InL = new float[SharedAudioLayout.MaxBlockFrames];
        public readonly float[] InR = new float[SharedAudioLayout.MaxBlockFrames];
        public readonly float[] OutL = new float[SharedAudioLayout.MaxBlockFrames];
        public readonly float[] OutR = new float[SharedAudioLayout.MaxBlockFrames];

        public readonly float[][] PublishPlanes =
        [
            new float[SharedAudioLayout.MaxBlockFrames],
            new float[SharedAudioLayout.MaxBlockFrames],
        ];

        public void Dispose()
        {
            if (Fx != IntPtr.Zero)
            {
                JsfxNative.ysfx_free(Fx);
                Fx = IntPtr.Zero;
            }

            if (Config != IntPtr.Zero)
            {
                JsfxNative.ysfx_config_free(Config);
                Config = IntPtr.Zero;
            }
        }
    }
}
