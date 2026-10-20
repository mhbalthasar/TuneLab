using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using TuneLab.ExperimentalPatch.VstBridge;

// 桥进程里的插件宿主：把插件装进来、喂音频、收音频。
//
// 【VST2 是什么】一个导出 `VSTPluginMain` 的 dll，那个函数返回一个
// AEffect*——一大块结构体，一半是字段、一半是函数指针（回调进宿主）。
// 音频走：宿主备好平面缓冲 → 调 processReplacing → 插件把结果写回缓冲。
//
// 【为什么是 P/Invoke 而不是 VST.NET2】VST.NET2 的 NuGet 包要 net10 + Windows Desktop，
// 跟宿主 net8 对不上；而且它自带的桥是另一套协议。这里只实现 VST2 的子集
// （open/close/processReplacing/getChunk/setChunk/参数读写），大约两百行 ——
// 可控、可读、可单测（协议那半不需要真插件）。
//
// 【线程】本类只在桥的主线程用（管道的读写在同一个线程上）。
// 没有"音频线程"这一说：宿主每块发一个请求，我们同步处理完再回。
internal sealed class PluginHost
{
    // VST2 的 magic：AEffect 前 4 字节，用来校验"拿到的指针真的是 AEffect"。
    const int VstMagic = ('V' << 24) | ('s' << 16) | ('t' << 8) | 'P';

    // ── VST2 opcode（aeffect.h 的 AEffectOpcodes，编号是 ABI 的一部分）──
    //
    // 【为什么把这些写全】第一版凭记忆写了"effSetSampleRate = 1、effSetBlockSize = 3"，
    // 实际 1 是 **effClose**、3 是 effGetProgram。于是"打开插件"之后立刻把它关了，
    // 后续 dispatcher 全打在已释放的对象上 —— 表现为堆损坏 (0xC0000374)，
    // 看起来像 ABI/调用约定的问题，其实是 opcode 写错。
    const int EffOpen = 0;
    const int EffClose = 1;
    const int EffGetParamName = 8;      // 第一版写的 7 是 effGetParamDisplay
    const int EffSetSampleRate = 10;    // opt = sample rate
    const int EffSetBlockSize = 11;     // value = block size
    const int EffMainsChanged = 12;     // value = 1 开 / 0 关
    const int EffGetChunk = 23;         // index 0 = bank；ptr 收 void**
    const int EffSetChunk = 24;         // index 0 = bank；value = size
    const int EffGetEffectName = 45;
    const int EffGetVendorString = 47;

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
            // 最常见的原因：位数不匹配（桥是 32 位进程，装 64 位 dll 会报
            // ERROR_BAD_EXE_FORMAT），或者缺少依赖 dll。
            return (false, "cannot load the plugin library: " + ex.Message, null, null, 0);
        }

        if (!NativeLibrary.TryGetExport(library, "VSTPluginMain", out IntPtr entry))
        {
            NativeLibrary.Free(library);
            return (false, "the file is not a VST2 plugin (no VSTPluginMain export)", null, null, 0);
        }

        // 插件在 VSTPluginMain 里就会问宿主版本 / 采样率 / 块长（见 HostCallback），
        // 所以这两个值必须在**创建之前**写好，不能等到 effSetSampleRate。
        sSampleRate = (int)Math.Round(request.SampleRate);
        sBlockSize = Math.Max(request.MaxBlock, 512);

        // 入口用函数指针（calli）而不是委托：`VstPluginMain` 是为数不多实测在
        // `Marshal.GetDelegateForFunctionPointer` 上不稳的一个（见文件末尾 ABI 注记）。
        // 它只调一次，写 calli 没有维护成本。
        IntPtr effect;
        unsafe
        {
            var main = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)entry;
            effect = main(sHostCallbackPointer);
        }

        if (effect == IntPtr.Zero)
        {
            NativeLibrary.Free(library);
            return (false, "the plugin refused to create its AEffect", null, null, 0);
        }

        if (Marshal.ReadInt32(effect, 0) != VstMagic)
        {
            NativeLibrary.Free(library);
            return (false, "the returned pointer is not a VST2 AEffect", null, null, 0);
        }

        // AEffect 的布局随**指针宽度**变化：x86 桥里指针 4 字节，x64 里 8 字节。
        // 第一版按 x86 写死，拿到 x64 进程一跑就读到半个指针，调用即堆损坏。
        var layout = AEffectLayout.ForPointerSize(IntPtr.Size);

        int numParams = Marshal.ReadInt32(effect, layout.NumParams);
        int numInputs = Marshal.ReadInt32(effect, layout.NumInputs);
        int numOutputs = Marshal.ReadInt32(effect, layout.NumOutputs);

        inputChannels = numInputs;
        outputChannels = numOutputs;

        var slot = new Slot
        {
            Library = library,
            Effect = effect,
            Dispatcher = (DispatcherProc)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(effect, layout.Dispatcher), typeof(DispatcherProc)),
            SetParameter = (SetParameterProc)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(effect, layout.SetParameter), typeof(SetParameterProc)),
            GetParameter = (GetParameterProc)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(effect, layout.GetParameter), typeof(GetParameterProc)),
            NumParams = numParams,
            NumInputs = numInputs,
            NumOutputs = numOutputs,
            Frames = Math.Max(request.MaxBlock, 512),
        };

        // processReplacing 在 AEffect 的尾部（x86 偏移 80）。结构体里 +8 的
        // `process` 是累加式的老入口，已废弃；插件对它的实现率远低于 processReplacing。
        IntPtr processEntry = Marshal.ReadIntPtr(effect, layout.ProcessReplacing);
        if (processEntry == IntPtr.Zero)
        {
            // 老插件可能只实现了 +8 的 process。它把结果**累加**进输出缓冲，
            // 而我们在每块前已经清零输出，所以语义等价于 processReplacing。
            processEntry = Marshal.ReadIntPtr(effect, layout.Process);
        }

        if (processEntry == IntPtr.Zero)
        {
            ReleaseSlot(slot, closeEffect: true);
            NativeLibrary.Free(library);
            return (false, "the plugin exposes no audio processing entry point", null, null, 0);
        }

        slot.ProcessReplacing = (ProcessProc)Marshal.GetDelegateForFunctionPointer(
            processEntry, typeof(ProcessProc));

        // 走一次 VST2 的标准初始化顺序：open → 采样率 → 块长 → mainsChanged(1)。
        // 少最后一步时不少插件（含 REAPER 系）内部缓冲没分配，process 里会读到空指针。
        Call(slot.Dispatcher, effect, EffOpen, 0, IntPtr.Zero, IntPtr.Zero, 0);
        Call(slot.Dispatcher, effect, EffSetSampleRate, 0, IntPtr.Zero, IntPtr.Zero, (float)request.SampleRate);
        Call(slot.Dispatcher, effect, EffSetBlockSize, 0, (IntPtr)slot.Frames, IntPtr.Zero, 0);
        Call(slot.Dispatcher, effect, EffMainsChanged, 0, (IntPtr)1, IntPtr.Zero, 0);

        string name = ReadString(slot.Dispatcher, effect, EffGetEffectName);
        string vendor = ReadString(slot.Dispatcher, effect, EffGetVendorString);

        // initialDelay 就是 VST2 的 latency（单位：采样）。
        int latency = Marshal.ReadInt32(effect, layout.InitialDelay);

        // 平面缓冲按**插件声明的声道数**分配，不是按我们要用的 2 条。
        //
        // 【为什么】VST2 的 processReplacing 收的是 float**：插件会按自己的
        // numInputs / numOutputs 去索引这个数组。Voxengo 的插件声明 8 进 8 出，
        // 只递 2 个指针它就会读到数组外面 —— 实测直接 AccessViolation。
        // 正确做法是永远递满它声明的条数，用不上的填静音。
        slot.InPlanes = new float[Math.Max(numInputs, 1)][];
        slot.OutPlanes = new float[Math.Max(numOutputs, 1)][];
        for (int ch = 0; ch < slot.InPlanes.Length; ch++)
            slot.InPlanes[ch] = new float[slot.Frames];
        for (int ch = 0; ch < slot.OutPlanes.Length; ch++)
            slot.OutPlanes[ch] = new float[slot.Frames];

        slot.PublishPlanes = new float[SharedAudioLayout.MaxChannels][];

        mSlots[request.Slot] = slot;

        return (true, null, name, vendor, latency);
    }

    public bool Unload(int slotNumber)
    {
        if (!mSlots.Remove(slotNumber, out var slot))
            return false;

        ReleaseSlot(slot, closeEffect: true);
        NativeLibrary.Free(slot.Library);
        return true;
    }

    // 让插件停处理并（可选）关闭自己。顺序反了会踩到插件自己的析构。
    static void ReleaseSlot(Slot slot, bool closeEffect)
    {
        try
        {
            Call(slot.Dispatcher, slot.Effect, EffMainsChanged, 0, IntPtr.Zero, IntPtr.Zero, 0);
            if (closeEffect)
                Call(slot.Dispatcher, slot.Effect, EffClose, 0, IntPtr.Zero, IntPtr.Zero, 0);
        }
        catch (Exception)
        {
            // 插件在析构里抛/崩都不是我们能处理的；卸载流程继续走。
        }
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

        try
        {
            if (bypass)
            {
                // 直通：输入原样发回去。宿主要的是"旁通时声音不变"。
                mOut?.TryPublish(slotNumber, inPlanes, ch, fr, 1000);
                return true;
            }

            // 把输入搬进槽位自己的平面缓冲（插件读的是这份）。
            // 宿主送来的是 1~2 条；插件声明更多时，多出来的那几条保持静音。
            for (int c = 0; c < slot.InPlanes.Length; c++)
            {
                if (c < ch)
                {
                    int source = Math.Min(c, inPlanes.Length - 1);
                    Array.Copy(inPlanes[source], slot.InPlanes[c], fr);
                }
                else
                {
                    Array.Clear(slot.InPlanes[c], 0, fr);
                }
            }

            for (int c = 0; c < slot.OutPlanes.Length; c++)
                Array.Clear(slot.OutPlanes[c], 0, fr);

            bool ok = CallProcess(slot, fr);

            // 插件输出 → 宿主要的 1~2 条。单声道插件（1 出）在这里被复制到左右；
            // 8 出插件只取前两条。返回的平面数组是"视图"，不是拷贝。
            for (int c = 0; c < ch; c++)
                slot.PublishPlanes[c] = slot.OutPlanes[Math.Min(c, slot.OutPlanes.Length - 1)];

            // 插件失败也**不能**输出静音：直通原样，用户至少还听得见原来的东西。
            mOut?.TryPublish(slotNumber, ok ? slot.PublishPlanes : inPlanes, ch, fr, 1000);
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
            list.Add(new ParameterInfo
            {
                Id = i,
                Name = ReadString(slot.Dispatcher, slot.Effect, EffGetParamName, i),

                // VST2 的参数值域就是 0..1（归一化），没有别的单位。
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

    // processReplacing 的调用：平面缓冲必须**钉住**才能把指针交给原生代码。
    // 每块钉 2*(声道数) 个 GCHandle + 2 个指针数组，是这块代码的主要开销；
    // 换来的是"零拷贝、零 marshal 缓冲"。
    static bool CallProcess(Slot slot, int frames)
    {
        // 指针数组的长度必须 ≥ 插件声明的声道数（见 Load 里的注释）。
        int n = slot.InPlanes.Length;
        int outCount = slot.OutPlanes.Length;

        var input = new IntPtr[n];
        var output = new IntPtr[outCount];
        var pins = new GCHandle[n + outCount];

        GCHandle inputHandle = default;
        GCHandle outputHandle = default;

        try
        {
            for (int ch = 0; ch < n; ch++)
            {
                pins[ch] = GCHandle.Alloc(slot.InPlanes[ch], GCHandleType.Pinned);
                input[ch] = pins[ch].AddrOfPinnedObject();
            }

            for (int ch = 0; ch < outCount; ch++)
            {
                pins[n + ch] = GCHandle.Alloc(slot.OutPlanes[ch], GCHandleType.Pinned);
                output[ch] = pins[n + ch].AddrOfPinnedObject();
            }

            // float** 传的是"指针数组的地址"，所以数组本身也要钉住。
            inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);

            slot.ProcessReplacing(
                slot.Effect,
                inputHandle.AddrOfPinnedObject(),
                outputHandle.AddrOfPinnedObject(),
                frames);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (inputHandle.IsAllocated)
                inputHandle.Free();

            if (outputHandle.IsAllocated)
                outputHandle.Free();

            foreach (var pin in pins)
            {
                if (pin.IsAllocated)
                    pin.Free();
            }
        }
    }

    // VST2 里字符串通过一个回调参数回传：宿主提供一个指针，dispatcher 往里写。
    // 这里用一个固定的托管缓冲 + GCHandle 钉住，读一段立刻释放。
    static string ReadString(DispatcherProc dispatcher, IntPtr effect, int opcode, int index = 0)
    {
        var buffer = new byte[256];

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            Call(dispatcher, effect, opcode, index, IntPtr.Zero, handle.AddrOfPinnedObject(), 0);
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
            IntPtr size = Call(slot.Dispatcher, slot.Effect, EffGetChunk, 0,
                IntPtr.Zero, handle.AddrOfPinnedObject(), 0);

            IntPtr data = Marshal.ReadIntPtr(handle.AddrOfPinnedObject());
            int length = size.ToInt64() is var value && value > int.MaxValue ? 0 : (int)value;

            if (length <= 0 || data == IntPtr.Zero)
                return null;

            var chunk = new byte[length];
            Marshal.Copy(data, chunk, 0, length);
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
            return Call(slot.Dispatcher, slot.Effect, EffSetChunk, 0,
                (IntPtr)data.Length, handle.AddrOfPinnedObject(), 0) != IntPtr.Zero;
        }
        finally
        {
            handle.Free();
        }
    }

    // dispatcher 的薄封装，参数一律按 VST2 的宽类型写：
    //   value 是 VstIntPtr（指针宽），ptr 是 void*，opt 是 float。
    // 把 value 写成 int 会在 x64 上传一半的参，插件读到的就是垃圾。
    static IntPtr Call(DispatcherProc dispatcher, IntPtr effect, int opcode, int index,
        IntPtr value, IntPtr ptr, float opt)
        => dispatcher(effect, opcode, index, value, ptr, opt);

    // 插件回调进宿主的那个函数（audioMasterCallback）。VST2 插件会问一些事：
    // "宿主版本多少"、"采样率多少"、"现在什么时间"。
    //
    // 第一版一律返回 0 —— 结果一半插件在 VSTPluginMain 里就返回 null
    // （"the plugin refused to create its AEffect"）。它们把版本号 0 当作
    // "这个宿主不合法"，直接拒绝创建。所以下面这几项必须**如实回答**。
    //
    // 原则不变：只答我们真能给的东西。答"支持"却给不出实现的项（开窗、
    // 离线渲染、文件选择器）一律 0 —— 那才会让插件走崩。
    static IntPtr HostCallback(IntPtr effect, int opcode, int index, IntPtr value, IntPtr ptr, float opt)
    {
        switch (opcode)
        {
            // audioMasterVersion：VST2.4 = 2400。这个值决定插件走哪条兼容路径。
            case 1:
                return (IntPtr)2400;

            // audioMasterGetTime：返回 VstTimeInfo*。有些插件不判空直接解引用，
            // 所以这里给一块真实的结构体（播放中、120 BPM、4/4）。
            case 7:
                return TimeInfoPointer();

            // audioMasterGetSampleRate / GetBlockSize。
            case 17:
                return (IntPtr)sSampleRate;

            case 18:
                return (IntPtr)sBlockSize;

            // audioMasterGetCurrentProcessLevel：2 = 实时线程。
            // 答 0（未知）会让部分插件关掉内部优化。
            case 24:
                return (IntPtr)2;

            // audioMasterGetLanguage：1 = 英语。
            case 33:
                return (IntPtr)1;

            default:
                return IntPtr.Zero;
        }
    }

    // 当前桥的音频参数。插件通过 audioMaster 回调问的就是这两个，
    // 而 HostCallback 是静态的，只能存在这里（一个桥进程只服务一个采样率）。
    static int sSampleRate = 48000;
    static int sBlockSize = 512;

    // VstTimeInfo 是 88 字节、8 字节对齐的纯值结构。懒分配一次，之后复用。
    static IntPtr sTimeInfo;

    static IntPtr TimeInfoPointer()
    {
        if (sTimeInfo == IntPtr.Zero)
        {
            sTimeInfo = Marshal.AllocHGlobal(88);

            // samplePos / sampleRate / nanoSeconds / ppqPos / tempo / barStartPos /
            // cycleStartPos / cycleEndPos —— 前 8 个字段都是 double。
            for (int i = 0; i < 8; i++)
                Marshal.WriteInt64(sTimeInfo, i * 8, 0);

            Marshal.WriteInt32(sTimeInfo, 64, 4);    // timeSigNumerator
            Marshal.WriteInt32(sTimeInfo, 68, 4);    // timeSigDenominator
            Marshal.WriteInt32(sTimeInfo, 72, 0);    // smpteOffset
            Marshal.WriteInt32(sTimeInfo, 76, 0);    // smpteFrameRate

            // kVstTransportPlaying | kVstSamplePositionValid | kVstTempoValid | kVstTimeSigValid
            Marshal.WriteInt32(sTimeInfo, 80, 0);    // samplesToNextClock
            Marshal.WriteInt32(sTimeInfo, 84, 2 | 16 | 1024 | 2048);
        }

        // 采样率与速度每次都刷新（不在播放，samplePos/ppqPos 保持 0）。
        Marshal.WriteInt64(sTimeInfo, 8, BitConverter.DoubleToInt64Bits(sSampleRate));
        Marshal.WriteInt64(sTimeInfo, 32, BitConverter.DoubleToInt64Bits(120.0));
        return sTimeInfo;
    }

    // 上面那个回调的**固定**托管 delegate + 它的 native 指针。
    // 必须钉住：native 会把这个指针存起来，整个插件生命周期内随时调。
    // 而一个普通的委托对象是可能被 GC 收走的 —— 那之后插件一调就是野指针。
    static readonly HostCallbackProc sHostCallback = HostCallback;
    static readonly IntPtr sHostCallbackPointer = Marshal.GetFunctionPointerForDelegate(sHostCallback);

    // AEffect 的字段偏移。**随指针宽度变化**，必须按 IntPtr.Size 算：
    //   x86（桥进程）：4  magic / 4  dispatcher / 8 process / 12 setParameter / 16 getParameter
    //                  / 24 numParams / 28 numInputs / 32 numOutputs / 48 initialDelay / 80 processReplacing
    //   x64（验证用探针）：8 / 16 / 24 / 32 / 44 / 48 / 52 / 80 / 120
    // 第一版把 x86 的数字写死了，x64 下每个字段都偏 —— 读到的是别的指针的半个字。
    readonly struct AEffectLayout
    {
        public readonly int Dispatcher;
        public readonly int Process;
        public readonly int SetParameter;
        public readonly int GetParameter;
        public readonly int NumParams;
        public readonly int NumInputs;
        public readonly int NumOutputs;
        public readonly int InitialDelay;
        public readonly int ProcessReplacing;

        AEffectLayout(int dispatcher, int process, int setParameter, int getParameter,
            int numParams, int numInputs, int numOutputs, int initialDelay, int processReplacing)
        {
            Dispatcher = dispatcher;
            Process = process;
            SetParameter = setParameter;
            GetParameter = getParameter;
            NumParams = numParams;
            NumInputs = numInputs;
            NumOutputs = numOutputs;
            InitialDelay = initialDelay;
            ProcessReplacing = processReplacing;
        }

        public static AEffectLayout ForPointerSize(int pointerSize)
        {
            static int AlignUp(int value, int alignment)
                => (value + alignment - 1) / alignment * alignment;

            // magic 是 4 字节的 int，后面第一个指针要按指针宽度对齐。
            int dispatcher = AlignUp(4, pointerSize);
            int process = dispatcher + pointerSize;
            int setParameter = process + pointerSize;
            int getParameter = setParameter + pointerSize;

            int numPrograms = getParameter + pointerSize;
            int numParams = numPrograms + 4;
            int numInputs = numParams + 4;
            int numOutputs = numInputs + 4;
            int flags = numOutputs + 4;

            int resvd1 = AlignUp(flags + 4, pointerSize);
            int resvd2 = resvd1 + pointerSize;
            int initialDelay = resvd2 + pointerSize;

            // initialDelay / realQualities / offQualities / ioRatio 各 4 字节，
            // 然后 object / user 两个指针、uniqueID / version 两个 int，
            // 再对齐到指针宽度就是 processReplacing。
            int processReplacing = AlignUp(
                initialDelay + 4 * 4 + pointerSize * 2 + 4 + 4, pointerSize);

            return new AEffectLayout(dispatcher, process, setParameter, getParameter,
                numParams, numInputs, numOutputs, initialDelay, processReplacing);
        }
    }

    sealed class Slot
    {
        public IntPtr Library;
        public IntPtr Effect;
        public DispatcherProc Dispatcher = null!;
        public SetParameterProc SetParameter = null!;
        public GetParameterProc GetParameter = null!;
        public ProcessProc ProcessReplacing = null!;
        public int NumParams;
        public int NumInputs;
        public int NumOutputs;
        public int Frames;

        // 长度 = max(NumInputs, 1) / max(NumOutputs, 1)：必须容纳插件声明的全部声道。
        public float[][] InPlanes = [];
        public float[][] OutPlanes = [];

        // 发布给宿主时用的 1~2 条"视图"，避免每块分配数组。
        public float[][] PublishPlanes = [];
    }

    // ── VST2.4 的 C ABI ──
    //
    // 全部按 VST2 SDK（audioeffect.h / aeffect.h）的**原始 C 签名**写，
    // 不套任何第三方包装。代价是这一百多行 P/Invoke 得自己维护，
    // 好处是 VST2 的 ABI 自 2006 年定型以来没变过，写对一次就一直对。
    //
    // 【调用约定】VST2 在 Windows 上把 `VSTCALLBACK` 定义成 `__cdecl`
    // （不是 WINAPI/stdcall）。三个回调（dispatcher / processReplacing /
    // audioMasterCallback）和入口 VSTPluginMain 全是 cdecl。
    // 在 x64 上两种约定没有区别，所以这一条只有 x86 桥里才真正生效 —— 那正是
    // 我们跑这个桥的地方，必须写对。
    //
    // 【VstIntPtr】dispatcher 的 value 参数和返回值是 VstIntPtr = intptr_t。
    // 必须用 IntPtr：写成 int 在 x64 上只传半个参数，插件读到的就是垃圾。
    //
    // 【为什么 AEffect 里的函数指针用委托而不是 calli】两者都能跑，
    // 委托让调用点留在托管代码里、不用把周围方法全标 unsafe，
    // 而且 `Marshal.GetDelegateForFunctionPointer` 用**非泛型**重载时
    // 在 .NET 8 上没有泛型重载那个 `Internal CLR error (0x80131506)` 的毛病。
    // 入口 VSTPluginMain 是唯一用 calli 的（只用一次，且它的委托类型实测不稳）。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr VstPluginMain(IntPtr hostCallback);

    // AEffectDispatcherProc：宿主 → 插件（effOpen / effClose / getChunk / setChunk …）。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr DispatcherProc(IntPtr effect, int opcode, int index,
        IntPtr value, IntPtr ptr, float opt);

    // audioMasterCallback：插件 → 宿主。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr HostCallbackProc(IntPtr effect, int opcode, int index,
        IntPtr value, IntPtr ptr, float opt);

    // setParameter / getParameter：VST2 参数按序号寻址，值域 0..1。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void SetParameterProc(IntPtr effect, int index, float value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate float GetParameterProc(IntPtr effect, int index);

    // processReplacing(AEffect*, float** inputs, float** outputs, VstInt32 frames)。
    // 返回 void；缓冲用 IntPtr 传，自己钉住指针数组（见 CallProcess）。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void ProcessProc(IntPtr effect, IntPtr inputs, IntPtr outputs, int frames);
}
