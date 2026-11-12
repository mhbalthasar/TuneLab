using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

// CLAP 的 C ABI 声明。
//
// 【为什么 CLAP 不需要 native 宿主库】VST3 那边要 vendored 的 ownvst3（每平台一份 .dll/.so/.dylib），
// 因为 C++ 的接口/虚表没法从托管侧直接调。CLAP 是**纯 C ABI**：入口是一个导出变量
// `clap_entry`，里面是三个函数指针；插件工厂与描述符都是 POD 结构。于是 P/Invoke 就够，
// 桥不需要任何额外二进制 —— 这也是它三平台同一份代码的原因。
//
// 【函数指针为什么写成 delegate* unmanaged[Cdecl]】与 PluginHost 里 VST2 的处理同一个理由：
// 委托 + Marshal.GetDelegateForFunctionPointer 会引入装箱与调用约定的隐式转换，
// 而这里是 ABI 边界，一个字节错位就是崩溃。函数指针是编译器直接生成的 calli，
// 参数类型即 ABI。
//
// 【bool 一律用 byte】CLAP 的 bool 是 C99 `_Bool`（1 字节）。C# 的 bool 在互操作里
// 默认按 4 字节 Win32 BOOL 处理 —— 返回值只写 AL 时高三个字节是垃圾，读出来可能是
// 假的 true。所以这里的返回值 / 参数全用 byte，判 != 0。
//
// 【结构必须逐字节对齐】这些 struct 是直接投给 C 的指针，布局错了就是内存错位。
// 带固定长字符数组的（clap_param_info / clap_audio_port_info）用 fixed byte[...]，
// 长度取 C 头文件里的 CLAP_NAME_SIZE=256 / CLAP_PATH_SIZE=1024。
internal static unsafe class ClapNative
{
    // 本宿主认识的 CLAP 主版本。Major 不等 = 结构布局都可能不同，不能猜。
    public const uint SupportedMajor = 1;

    // 我们在 clap_host.clap_version 里自报的版本（只影响插件对能力的乐观假设，
    // 具体能力仍然靠扩展 id 询问）。
    public const uint HostMinor = 2;
    public const uint HostRevision = 2;

    // 插件工厂的固定扩展 id（clap/plugin.h）。
    public const string PluginFactoryId = "clap.plugin-factory";

    // 扩展 id（clap/ext/*.h）。
    public const string AudioPortsExtension = "clap.audio-ports";
    public const string ParamsExtension = "clap.params";
    public const string StateExtension = "clap.state";
    public const string LatencyExtension = "clap.latency";
    public const string ThreadCheckExtension = "clap.thread-check";
    public const string GuiExtension = "clap.plugin-gui";
    public const string HostGuiExtension = "clap.host-gui";

    // "win32" 的 UTF-8 字节串，放在非托管内存里：clap_window_t.api 会交给插件，
    // 生命周期必须覆盖整个 GUI 会话（栈上的 fixed 指针活不过那一次调用）。
    // is_api_supported / set_parent 共用这一份。
    public static readonly IntPtr Win32Api = AllocUtf8("win32");

    static IntPtr AllocUtf8(string text)
    {
        byte[] bytes = Utf8(text);
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    // 事件类型 / 空间（clap/events.h）。
    public const ushort EventSpacePlugin = 0;
    public const ushort EventTypeParamValue = 5;

    // 音频端口 flags（clap/audio-ports.h）。
    public const uint AudioPortIsMain = 1u << 0;
    public const uint AudioPortIsCv = 1u << 1;

    // 参数 flags（clap/params.h）。
    public const uint ParamIsStepped = 1u << 0;
    public const uint ParamIsReadonly = 1u << 3;
    public const uint ParamIsBypass = 1u << 4;

    // process 的返回值（clap/process.h）。
    public const int ProcessContinue = 0;
    public const int ProcessContinueIfNotQuiet = 1;
    public const int ProcessTail = 2;
    public const int ProcessSleep = 3;
    public const int ProcessError = -1;

    // 一个 .clap 里最多认多少个插件。真实插件是 1~4 个；这条只是防"读坏了当几百万条"。
    const uint MaxPluginsPerFile = 1024;

    // 一块最多塞多少条参数事件。超过就丢（宁可少一次自动化，不可越界）。
    public const int MaxEventsPerBlock = 64;

    // 【布局自检】下面的数字是 CLAP 1.x 头文件在 64 位上的自然布局（含对齐填充）。
    // 布局错了不会有人报错 —— 而是把错位的指针交给插件，表现为随机崩溃 / 内存损坏。
    // 所以第一次用到 CLAP 时先对一遍：对不上就抛，桥带着这条消息死掉，宿主如实报
    // "隔离宿主没有响应"，比静默损坏好查得多。
    static ClapNative()
    {
        Check(sizeof(ClapVersion) == 12, "clap_version_t");
        Check(sizeof(PluginEntry) == 40, "clap_plugin_entry_t");
        Check(sizeof(PluginFactory) == 24, "clap_plugin_factory_t");
        Check(sizeof(PluginDescriptor) == 88, "clap_plugin_descriptor_t");
        Check(sizeof(Host) == 88, "clap_host_t");
        Check(sizeof(Plugin) == 96, "clap_plugin_t");
        Check(sizeof(AudioBuffer) == 32, "clap_audio_buffer_t");
        Check(sizeof(Process) == 64, "clap_process_t");
        Check(sizeof(EventHeader) == 16, "clap_event_header_t");
        Check(sizeof(EventParamValue) == 56, "clap_event_param_value_t");
        Check(sizeof(InputEvents) == 24, "clap_input_events_t");
        Check(sizeof(OutputEvents) == 16, "clap_output_events_t");
        Check(sizeof(OStream) == 16, "clap_ostream_t");
        Check(sizeof(IStream) == 16, "clap_istream_t");
        Check(sizeof(AudioPortInfo) == 288, "clap_audio_port_info_t");
        Check(sizeof(PluginAudioPorts) == 16, "clap_plugin_audio_ports_t");
        Check(sizeof(ParamInfo) == 1320, "clap_param_info_t");
        Check(sizeof(PluginParams) == 48, "clap_plugin_params_t");
        Check(sizeof(PluginState) == 16, "clap_plugin_state_t");
        Check(sizeof(PluginLatency) == 8, "clap_plugin_latency_t");
        Check(sizeof(Window) == 16, "clap_window_t");
        Check(sizeof(PluginGui) == 96, "clap_plugin_gui_t");
        Check(sizeof(HostGui) == 40, "clap_host_gui_t");
        Check(sizeof(HostThreadCheck) == 16, "clap_host_thread_check_t");
    }

    static void Check(bool ok, string what)
    {
        if (!ok)
            throw new InvalidOperationException("CLAP ABI layout mismatch: " + what);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapVersion
    {
        public uint Major;
        public uint Minor;
        public uint Revision;

        public override readonly string ToString() => $"{Major}.{Minor}.{Revision}";

        public static ClapVersion Host => new()
        {
            Major = SupportedMajor,
            Minor = HostMinor,
            Revision = HostRevision,
        };
    }

    // clap_plugin_entry_t：.clap 导出的那一个变量。
    [StructLayout(LayoutKind.Sequential)]
    public struct PluginEntry
    {
        public ClapVersion ClapVersion;

        // bool init(const char *plugin_path)
        public delegate* unmanaged[Cdecl]<byte*, byte> Init;

        // void deinit(void)
        public delegate* unmanaged[Cdecl]<void> Deinit;

        // const void *get_factory(const char *factory_id)
        public delegate* unmanaged[Cdecl]<byte*, IntPtr> GetFactory;
    }

    // clap_plugin_factory_t。
    [StructLayout(LayoutKind.Sequential)]
    public struct PluginFactory
    {
        public delegate* unmanaged[Cdecl]<PluginFactory*, uint> GetPluginCount;
        public delegate* unmanaged[Cdecl]<PluginFactory*, uint, PluginDescriptor*> GetPluginDescriptor;

        // const clap_plugin_t *create_plugin(factory, host, plugin_id)
        public delegate* unmanaged[Cdecl]<PluginFactory*, IntPtr, byte*, IntPtr> CreatePlugin;
    }

    // clap_plugin_descriptor_t。**所有字符串都是 const char*，生命周期到 deinit 为止**：
    // 所以调用方必须在 deinit 之前把它们复制成托管字符串（见 ClapScanner）。
    [StructLayout(LayoutKind.Sequential)]
    public struct PluginDescriptor
    {
        public ClapVersion ClapVersion;

        public byte* Id;
        public byte* Name;
        public byte* Vendor;
        public byte* Url;
        public byte* ManualUrl;
        public byte* SupportUrl;
        public byte* Version;
        public byte* Description;

        // const char *const *features —— 以 null 结尾的字符串数组，可以为 null。
        public byte** Features;
    }

    // clap_host_t。每个插件实例一份（host_data 指向桥里的槽位），必须放在**非托管内存**里：
    // 插件会长期持有这个指针并随时回调。
    [StructLayout(LayoutKind.Sequential)]
    public struct Host
    {
        public ClapVersion ClapVersion;
        public IntPtr HostData;

        public byte* Name;
        public byte* Vendor;
        public byte* Url;
        public byte* HostVersion;

        // const void *get_extension(host, extension_id)
        public delegate* unmanaged[Cdecl]<Host*, byte*, IntPtr> GetExtension;

        public delegate* unmanaged[Cdecl]<Host*, void> RequestRestart;
        public delegate* unmanaged[Cdecl]<Host*, void> RequestProcess;
        public delegate* unmanaged[Cdecl]<Host*, void> RequestCallback;
    }

    // clap_plugin_t。
    [StructLayout(LayoutKind.Sequential)]
    public struct Plugin
    {
        public PluginDescriptor* Desc;
        public IntPtr PluginData;

        public delegate* unmanaged[Cdecl]<Plugin*, byte> Init;
        public delegate* unmanaged[Cdecl]<Plugin*, void> Destroy;
        public delegate* unmanaged[Cdecl]<Plugin*, double, uint, uint, byte> Activate;
        public delegate* unmanaged[Cdecl]<Plugin*, void> Deactivate;
        public delegate* unmanaged[Cdecl]<Plugin*, byte> StartProcessing;
        public delegate* unmanaged[Cdecl]<Plugin*, void> StopProcessing;
        public delegate* unmanaged[Cdecl]<Plugin*, void> Reset;
        public delegate* unmanaged[Cdecl]<Plugin*, Process*, int> Process;
        public delegate* unmanaged[Cdecl]<Plugin*, byte*, IntPtr> GetExtension;
        public delegate* unmanaged[Cdecl]<Plugin*, void> OnMainThread;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBuffer
    {
        public float** Data32;
        public double** Data64;
        public uint ChannelCount;

        // 插件上报的自身延迟（帧）。宿主用 clap.latency 那份，这份只作参考。
        public uint Latency;
        public ulong ConstantMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Process
    {
        public long SteadyTime;
        public uint FramesCount;
        public IntPtr Transport;          // 可选，null
        public AudioBuffer* AudioInputs;
        public AudioBuffer* AudioOutputs;
        public uint AudioInputsCount;
        public uint AudioOutputsCount;
        public InputEvents* InEvents;
        public OutputEvents* OutEvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventHeader
    {
        public uint Size;
        public uint Time;
        public ushort SpaceId;
        public ushort Type;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventParamValue
    {
        public EventHeader Header;
        public uint ParamId;
        public IntPtr Cookie;
        public int NoteId;
        public short PortIndex;
        public short Channel;
        public short Key;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct InputEvents
    {
        public IntPtr Ctx;
        public delegate* unmanaged[Cdecl]<InputEvents*, uint> Size;
        public delegate* unmanaged[Cdecl]<InputEvents*, uint, EventHeader*> Get;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OutputEvents
    {
        public IntPtr Ctx;
        public delegate* unmanaged[Cdecl]<OutputEvents*, EventHeader*, byte> TryPush;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OStream
    {
        public IntPtr Ctx;

        // int64_t write(stream, const void *buffer, uint64_t size) → 写入字节数，-1 = 失败
        public delegate* unmanaged[Cdecl]<OStream*, void*, ulong, long> Write;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IStream
    {
        public IntPtr Ctx;

        // int64_t read(stream, void *buffer, uint64_t size) → 读到的字节数，0 = EOF，-1 = 失败
        public delegate* unmanaged[Cdecl]<IStream*, void*, ulong, long> Read;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioPortInfo
    {
        public uint Id;
        public fixed byte Name[256];
        public uint Flags;
        public uint ChannelCount;
        public byte* PortType;
        public uint InPlacePair;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PluginAudioPorts
    {
        public delegate* unmanaged[Cdecl]<Plugin*, byte, uint> Count;
        public delegate* unmanaged[Cdecl]<Plugin*, uint, byte, AudioPortInfo*, byte> Get;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ParamInfo
    {
        public uint Id;
        public uint Flags;
        public IntPtr Cookie;
        public fixed byte Name[256];
        public fixed byte Module[1024];
        public double MinValue;
        public double MaxValue;
        public double DefaultValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PluginParams
    {
        public delegate* unmanaged[Cdecl]<Plugin*, uint> Count;
        public delegate* unmanaged[Cdecl]<Plugin*, uint, ParamInfo*, byte> GetInfo;
        public delegate* unmanaged[Cdecl]<Plugin*, uint, double*, byte> GetValue;
        public delegate* unmanaged[Cdecl]<Plugin*, uint, double, byte*, uint, byte> ValueToText;
        public delegate* unmanaged[Cdecl]<Plugin*, uint, byte*, double*, byte> TextToValue;
        public delegate* unmanaged[Cdecl]<Plugin*, InputEvents*, OutputEvents*, void> Flush;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PluginState
    {
        public delegate* unmanaged[Cdecl]<Plugin*, OStream*, byte> Save;
        public delegate* unmanaged[Cdecl]<Plugin*, IStream*, byte> Load;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PluginLatency
    {
        public delegate* unmanaged[Cdecl]<Plugin*, uint> Get;
    }

    // clap_window_t（clap/gui.h）：api 字符串 + 一个联合体（x64 上是 8 字节）。
    // win32 那一路的联合体成员是 HWND（intptr_t），不是字符串 —— 所以这里用 IntPtr。
    [StructLayout(LayoutKind.Sequential)]
    public struct Window
    {
        public byte* Api;
        public IntPtr Specific;
    }

    // clap_plugin_gui_t（clap/gui.h）。宿主只走 Windows 的 win32 API：
    // create → set_parent → show；关窗时 hide → destroy。
    [StructLayout(LayoutKind.Sequential)]
    public struct PluginGui
    {
        public delegate* unmanaged[Cdecl]<Plugin*, byte*, byte, byte> IsApiSupported;
        public delegate* unmanaged[Cdecl]<Plugin*, byte**, byte*, byte> GetPreferredApi;
        public delegate* unmanaged[Cdecl]<Plugin*, byte*, byte, byte> Create;
        public delegate* unmanaged[Cdecl]<Plugin*, void> Destroy;
        public delegate* unmanaged[Cdecl]<Plugin*, double, byte> SetScale;
        public delegate* unmanaged[Cdecl]<Plugin*, uint*, uint*, byte> GetSize;
        public delegate* unmanaged[Cdecl]<Plugin*, uint, uint, byte> SetSize;
        public delegate* unmanaged[Cdecl]<Plugin*, Window*, byte> SetParent;
        public delegate* unmanaged[Cdecl]<Plugin*, Window*, byte> SetTransient;
        public delegate* unmanaged[Cdecl]<Plugin*, byte*, void> SuggestTitle;
        public delegate* unmanaged[Cdecl]<Plugin*, byte> Show;
        public delegate* unmanaged[Cdecl]<Plugin*, byte> Hide;
    }

    // clap_host_gui_t（clap/gui.h）。插件通过这些回调要求宿主改窗口尺寸 / 显隐。
    [StructLayout(LayoutKind.Sequential)]
    public struct HostGui
    {
        public delegate* unmanaged[Cdecl]<Host*, void> ResizeHintsChanged;
        public delegate* unmanaged[Cdecl]<Host*, uint, uint, byte> RequestResize;
        public delegate* unmanaged[Cdecl]<Host*, byte> RequestShow;
        public delegate* unmanaged[Cdecl]<Host*, byte> RequestHide;
        public delegate* unmanaged[Cdecl]<Host*, byte, void> Closed;
    }

    // clap_host_thread_check。桥是单线程的（收消息 → 办事），所以两个问题的答案都是"是"。
    // 【为什么必须提供】不少插件在没有这个扩展时会**假设**自己不在主线程上，
    // 于是把该做的事往后推、永远不做（DPF 系尤其明显：参数改了界面不刷新）。
    [StructLayout(LayoutKind.Sequential)]
    public struct HostThreadCheck
    {
        public delegate* unmanaged[Cdecl]<Host*, byte> IsMainThread;
        public delegate* unmanaged[Cdecl]<Host*, byte> IsAudioThread;
    }

    // UTF-8 + 结尾 0 的字节串。调用方要 fixed 住再传指针。
    public static byte[] Utf8(string text)
    {
        int count = Encoding.UTF8.GetByteCount(text);
        var bytes = new byte[count + 1];
        Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    public static string? StringAt(byte* text)
        => text == null ? null : Marshal.PtrToStringUTF8((IntPtr)text);

    // 读 features 数组。上限是为了防越界读：数组坏了的时候指针会一路读下去。
    public static List<string> FeaturesAt(byte** features)
    {
        var list = new List<string>();

        if (features == null)
            return list;

        for (int i = 0; i < 64; i++)
        {
            byte* text = features[i];

            if (text == null)
                break;

            string? value = StringAt(text);

            if (!string.IsNullOrEmpty(value))
                list.Add(value);
        }

        return list;
    }

    public static uint PluginCount(PluginFactory* factory, out bool plausible)
    {
        plausible = true;

        uint count = factory->GetPluginCount(factory);

        // 读坏了的时候这里会是几百万：不设上限的话后面就是越界读。
        if (count > MaxPluginsPerFile)
        {
            plausible = false;
            return 0;
        }

        return count;
    }

    // 固定长 char 数组 → 托管字符串（到第一个 0 为止）。
    public static string FixedString(byte* text, int capacity)
    {
        if (text == null)
            return string.Empty;

        int length = 0;

        while (length < capacity && text[length] != 0)
            length++;

        return length == 0 ? string.Empty : Encoding.UTF8.GetString(text, length);
    }
}
