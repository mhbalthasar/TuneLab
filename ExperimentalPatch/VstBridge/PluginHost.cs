using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using TuneLab.ExperimentalPatch.VstBridge;

// 桥进程里的插件宿主：把 32 位 VST2 插件装进来、喂音频、收音频。
//
// 【VST2 是什么】一个导出 `VSTPluginMain` 的 32 位 dll，那个函数返回一个
// AEffect*——一大块结构体，一半是字段、一半是函数指针（回调进宿主）。
// 音频走：宿主备好平面缓冲 → 调 effProcess / effProcessReplacing → 插件把结果写回缓冲。
//
// 【为什么是 P/Invoke 而不是 VST.NET2】VST.NET2 的 NuGet 包要 net10 + Windows Desktop，
// 跟宿主 net8 对不上；而且它自带的桥是另一套协议。这里只实现 VST2 的子集
// （effOpen/Close/ProcessReplacing/GetChunk/SetChunk/EditOpen 不接），
// 大约两百行 P/Invoke —— 可控、可读、可单测（协议那半不需要真插件）。
//
// 【线程】本类只在桥的主线程用（管道的读写在同一个线程上）。
// 没有"音频线程"这一说：宿主每块发一个请求，我们同步处理完再回。
internal sealed class PluginHost
{
    // VST2 的 magic：AEffect 前 4 字节，用来校验"拿到的指针真的是 AEffect"。
    const int VstMagic = ('V' << 24) | ('s' << 16) | ('t' << 8) | 'P';

    // effProcessReplacing 的 opcode（VST2 SDK 里就叫这个编号，跨版本稳定）。
    const int EffProcessReplacing = 29;

    // AEffect 里我们关心的字段偏移。按 VST2.4 头文件的顺序排：
    // magic, dispatcher, process, setParameter, getParameter, [events/misc...]
    // 这里只按偏移取指针，不做整个结构体的 marshal（那玩意儿随版本会变）。
    readonly Dictionary<int, Slot> mSlots = [];

    // 共享音频环（DESIGN.md §5.3 之后的修订：音频走共享内存，指令走命名管道）。
    // 桥侧的两条腿：
    //   mIn  = 宿主 → 桥（宿主是生产者，桥读 + Release）
    //   mOut = 桥 → 宿主（桥是生产者，宿主读 + Release）
    SharedAudioRing? mIn;
    SharedAudioRing? mOut;

    public void Attach(SharedAudioRing input, SharedAudioRing output)
    {
        mIn = input;
        mOut = output;
    }

    public (bool Ok, string? Reason, string? Name, string? Vendor, int LatencySamples) Load(
        LoadPluginMessage request, out int inputChannels, out int outputChannels)
    {
        inputChannels = 0;
        outputChannels = 0;

        if (mSlots.ContainsKey(request.Slot))
        {
            Unload(request.Slot);
        }

        if (!File.Exists(request.Path))
        {
            return (false, "plugin file not found", null, null, 0);
        }

        IntPtr library;
        try
        {
            library = NativeLibrary.Load(request.Path);
        }
        catch (Exception ex)
        {
            // 最常见的原因：位数不匹配（宿主按 x86 编译，这个桥就是 32 位，
            // 所以这里是"文件根本不是 VST2"或"缺依赖 dll"）。
            return (false, "cannot load the plugin library: " + ex.Message, null, null, 0);
        }

        if (!NativeLibrary.TryGetExport(library, "VSTPluginMain", out IntPtr entry))
        {
            NativeLibrary.Free(library);
            return (false, "the file is not a VST2 plugin (no VSTPluginMain export)", null, null, 0);
        }

        var main = Marshal.GetDelegateForFunctionPointer<VstPluginMain>(entry);

        IntPtr effect = main(HostCallback);
        if (effect == IntPtr.Zero)
        {
            NativeLibrary.Free(library);
            return (false, "the plugin refused to create its AEffect", null, null, 0);
        }

        int magic = Marshal.ReadInt32(effect, 0);
        if (magic != VstMagic)
        {
            NativeLibrary.Free(library);
            return (false, "the returned pointer is not a VST2 AEffect", null, null, 0);
        }

        // AEffect 的字段偏移（32 位指针 / 32 位 int，见文件末尾 ABI 小节的完整表）。
        var dispatcher = Marshal.GetDelegateForFunctionPointer<AudioMasterDispatcher>(
            Marshal.ReadIntPtr(effect, 4));

        // setParameter / getParameter：宿主改插件参数、读插件状态都走它们。
        var setParameter = Marshal.GetDelegateForFunctionPointer<SetParameterFunc>(
            Marshal.ReadIntPtr(effect, 12));

        var getParameter = Marshal.GetDelegateForFunctionPointer<GetParameterFunc>(
            Marshal.ReadIntPtr(effect, 16));

        int numParams = Marshal.ReadInt32(effect, 24);
        int numInputs = Marshal.ReadInt32(effect, 28);
        int numOutputs = Marshal.ReadInt32(effect, 32);

        inputChannels = numInputs;
        outputChannels = numOutputs;

        var slot = new Slot
        {
            Library = library,
            Effect = effect,
            Dispatcher = dispatcher,
            SetParameter = setParameter,
            GetParameter = getParameter,
            NumParams = numParams,
            NumInputs = numInputs,
            NumOutputs = numOutputs,
            Channels = Math.Max(Math.Min(numInputs, numOutputs), 1),
            Frames = Math.Max(request.MaxBlock, 512),
        };

        // 打开插件（effOpen）。VST2 要求先 open 再干别的。
        slot.Dispatcher(effect, 0 /* effOpen */, 0, 0, IntPtr.Zero, 0);

        // 设置采样率（effSetSampleRate = 1）。
        slot.Dispatcher(effect, 1, 0, 0, IntPtr.Zero, (float)request.SampleRate);

        // 告诉它最大块长（effSetBlockSize = 3）。
        slot.Dispatcher(effect, 3, 0, slot.Frames, IntPtr.Zero, 0);

        // 准备平面缓冲（per slot，块长固定——协议里 MaxBlock 定死它）。
        slot.InPlanes = new float[slot.Channels][];
        slot.OutPlanes = new float[slot.Channels][];
        for (int ch = 0; ch < slot.Channels; ch++)
        {
            slot.InPlanes[ch] = new float[slot.Frames];
            slot.OutPlanes[ch] = new float[slot.Frames];
        }

        // effGetEffectName = 1、effGetVendorString = 3（不是 2！2 是 effGetProductString）。
        string name = ReadString(effect, dispatcher, 1 /* effGetEffectName */);
        string vendor = ReadString(effect, dispatcher, 3 /* effGetVendorString */);

        // initialDelay 在 AEffect 偏移 44（dispatcher 问不到：4 是 effGetVendorVersion）。
        int latency = Marshal.ReadInt32(effect, 44);

        mSlots[request.Slot] = slot;

        return (true, null, name, vendor, latency);
    }

    public bool Unload(int slotNumber)
    {
        if (!mSlots.Remove(slotNumber, out var slot))
            return false;

        // 关闭插件、卸载 dll。顺序反了会踩到插件自己的析构。
        try
        {
            slot.Dispatcher(slot.Effect, 1 /* effClose */, 0, 0, IntPtr.Zero, 0);
        }
        catch (Exception)
        {
        }

        try
        {
            NativeLibrary.Free(slot.Library);
        }
        catch (Exception)
        {
        }

        return true;
    }

    // 处理宿主要求的一块。输入从共享环取，处理完把结果发到另一个共享环。
    // 返回 false = 这一块没处理成功（宿主会记日志 / 决定是否旁通）。
    public bool Process(int slotNumber, bool bypass, out int frames, out int channels)
    {
        frames = 0;
        channels = 0;

        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        // 入场券：宿主要求处理时，输入环里**已经有**一块在等我们。
        if (mIn == null || !mIn.TryRead(0, out _, out var inPlanes, out int ch, out int fr))
            return false;

        frames = fr;
        channels = ch;

        // 把输入搬进槽位自己的平面缓冲（插件读的是这份）。
        // 声道数可能比槽位窄（宿主送 2 声道而插件只要 1）：少的那几条留空 = 静音，
        // 这正是"单声道插件挂在立体声总线上"的语义。
        for (int c = 0; c < slot.Channels; c++)
        {
            int source = Math.Min(c, inPlanes.Length - 1);
            Array.Copy(inPlanes[source], slot.InPlanes[c], fr);
        }

        try
        {
            if (bypass)
            {
                // 直通：输入原样发回去。宿主要的是"旁通时声音不变"。
                mOut?.TryPublish(slotNumber, inPlanes, ch, fr, 1000);
                return true;
            }

            for (int c = 0; c < slot.Channels; c++)
                Array.Clear(slot.OutPlanes[c], 0, fr);

            bool ok = CallProcess(slot, fr);

            // 插件失败也**不能**输出静音：直通原样，用户至少还听得见原来的东西。
            mOut?.TryPublish(slotNumber, ok ? slot.OutPlanes : slot.InPlanes, ch, fr, 1000);
            return ok;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            // 归还输入槽：宿主据此知道可以把下一块写进来了。
            mIn.Release();
        }
    }

    public byte[]? GetState(int slotNumber)
        => mSlots.TryGetValue(slotNumber, out var slot)
            ? GetChunk(slot)
            : null;

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

        return SetChunk(slot, data);
    }

    public List<ParameterInfo> GetParameters(int slotNumber)
    {
        var list = new List<ParameterInfo>();

        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return list;

        for (int i = 0; i < slot.NumParams; i++)
        {
            string name = ReadString(slot.Effect, slot.Dispatcher, 7 /* effGetParamName */, i);

            list.Add(new ParameterInfo
            {
                Id = i,
                Name = name,
                Min = 0,
                Max = 1,
                Value = slot.GetParameter(slot.Effect, i),
            });
        }

        return list;
    }

    public void SetParameter(int slotNumber, long id, double value)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return;

        // AEffect 的 setParameter 收 int 索引（VST2 参数就是按序号寻址，没有 64 位 id）。
        slot.SetParameter(slot.Effect, (int)id, (float)value);
    }

    // effProcessReplacing 的调用：平面缓冲必须**钉住**才能把指针交给原生代码。
    // 每块钉 4 个 GCHandle 是这块代码的主要开销；换来的是"零拷贝、零 marshal 缓冲"。
    // （替代方案是 IntPtr[] 数组 + Marshal.UnsafeAddrOfPinnedArrayElement，见下——本例用后者。）
    static bool CallProcess(Slot slot, int frames)
    {
        var input = new IntPtr[slot.Channels];
        var output = new IntPtr[slot.Channels];

        for (int ch = 0; ch < slot.Channels; ch++)
        {
            input[ch] = Marshal.UnsafeAddrOfPinnedArrayElement(slot.InPlanes[ch], 0);
            output[ch] = Marshal.UnsafeAddrOfPinnedArrayElement(slot.OutPlanes[ch], 0);
        }

        return slot.ProcessReplacing(slot.Effect, input, output, frames) == 0;
    }

    // VST2 里字符串通过一个回调参数回传：宿主提供一个指针， dispatcher 往里写。
    // 这里用一个固定的托管缓冲 + GCHandle 钉住，读一段立刻释放。
    static string ReadString(IntPtr effect, AudioMasterDispatcher dispatcher, int opcode, int index = 0)
    {
        var buffer = new byte[256];

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            dispatcher(effect, opcode, index, 0, handle.AddrOfPinnedObject(), 0);
        }
        finally
        {
            handle.Free();
        }

        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0)
            end = buffer.Length;

        return System.Text.Encoding.ASCII.GetString(buffer, 0, end);
    }

    // effGetChunk = 23。插件把 state 写进一个**它自己分配**的指针，宿主读走、不负责释放
    // （VST2 的约定：插件返回的 chunk 归插件管，宿主只读一次）。
    static byte[]? GetChunk(Slot slot)
    {
        var handle = GCHandle.Alloc(IntPtr.Zero, GCHandleType.Pinned);

        try
        {
            // ptr 参数传出 chunk 指针；value 传 0 表示"要 program 0 的 bank"。
            int size = slot.Dispatcher(slot.Effect, 23, 0, 0, handle.AddrOfPinnedObject(), 0);
            IntPtr data = Marshal.ReadIntPtr(handle.AddrOfPinnedObject());

            if (size <= 0 || data == IntPtr.Zero)
                return null;

        var chunk = new byte[size];
        Marshal.Copy(data, chunk, 0, size);
        return chunk;
        }
        finally
        {
            handle.Free();
        }
    }

    // effSetChunk = 24。反过来：宿主提供缓冲区，插件自己拷走。
    static bool SetChunk(Slot slot, byte[] data)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            return slot.Dispatcher(slot.Effect, 24, 0, data.Length, handle.AddrOfPinnedObject(), 0) != 0;
        }
        finally
        {
            handle.Free();
        }
    }

    // 插件回调进宿主的那个函数（hostCallback）。VST2 插件会问一些事：
    // "现在什么时间"、"宿主支持哪些功能"、"能不能开窗"。
    // 桥进程只做音频，所以绝大多数都答"不支持 / 不知道" —— 这不会让插件不响。
    // 反过来，答"支持"却给不出对应实现才会让插件走崩。
    static int HostCallback(IntPtr effect, int opcode, int index, long value, IntPtr ptr, float opt)
        => 0;

    sealed class Slot
    {
        public IntPtr Library;
        public IntPtr Effect;
        public AudioMasterDispatcher Dispatcher = null!;
        public EffectProcessReplacing ProcessReplacing = null!;
        public SetParameterFunc SetParameter = null!;
        public GetParameterFunc GetParameter = null!;
        public int NumParams;
        public int NumInputs;
        public int NumOutputs;
        public int Channels;
        public int Frames;
        public float[][] InPlanes = [];
        public float[][] OutPlanes = [];
    }

    // ── VST2.4 的 C ABI ──
    //
    // 全部按 VST2 SDK（audioeffect.h / aeffect.h）的**原始 C 签名**写，
    // 不套任何第三方包装。代价是这一百多行 P/Invoke 得自己维护，
    // 好处是 VST2 的 ABI 自 2006 年定型以来没变过，写对一次就一直对。
    //
    // 【AEffect 布局】关键字段偏移（VST2 从来只是 32 位插件 API，指针 4 字节）：
    //   0   int   magic ('VstP')
    //   4   ptr   dispatcher
    //   8   ptr   process            （弃用，不用）
    //  12   ptr   setParameter
    //  16   ptr   getParameter
    //  20   int   numPrograms
    //  24   int   numParams
    //  28   int   numInputs
    //  32   int   numOutputs
    //  …   flags / resvd1 / resvd2 / initialDelay / ...
    //  56   int   uniqueID
    //  60   int   version
    //
    // 【为什么不用 Marshal.PtrToStructure】AEffect 有四十多个字段、尾部还跟着
    // 插件私有的数据；我们只需要其中几个。按偏移读、按需取函数指针，
    // 比维护一份完整结构体可靠，也不会因为 SDK 差一个字段就整体错位。

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr VstPluginMain(HostCallbackDelegate hostCallback);

    // audioMasterCallback：插件 → 宿主。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int HostCallbackDelegate(IntPtr effect, int opcode, int index, long value, IntPtr ptr, float opt);

    // AEffectDispatcher：宿主 → 插件（effOpen / effClose / effGetChunk / effSetChunk …）。
    // value 在 VST2 里是 VstIntPtr。Win32 上它就是 32 位有符号整数，
    // 写 long 虽然"能跑"（对持方只取低 32 位）但会白占栈，这里按真实宽度写 int。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int AudioMasterDispatcher(IntPtr effect, int opcode, int index, int value, IntPtr ptr, float opt);

    // effProcessReplacing：平面缓冲（float**）。这是 VST2 里唯一值得用的处理入口
    // （旧的 effProcess 收 VstEvents、是 MIDI 时代的产物，已弃用）。
    // 返回 0 表示"我没写输出"，宿主要自己把输入拷过去。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int EffectProcessReplacing(IntPtr effect, IntPtr[] inputs, IntPtr[] outputs, int sampleFrames);

    // AEffect 里的参数读写函数指针（偏移 12 / 16）。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void SetParameterFunc(IntPtr effect, int index, float parameter);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate float GetParameterFunc(IntPtr effect, int index);
}
