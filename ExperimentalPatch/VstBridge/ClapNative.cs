using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

// CLAP 的 C ABI 声明（P-C1：只到"问描述"这一步）。
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
internal static unsafe class ClapNative
{
    // 本宿主认识的 CLAP 主版本。Major 不等 = 结构布局都可能不同，不能猜。
    public const uint SupportedMajor = 1;

    // 插件工厂的固定扩展 id（clap/plugin.h）。
    public const string PluginFactoryId = "clap.plugin-factory";

    // 一个 .clap 里最多认多少个插件。真实插件是 1~4 个；这条只是防"读坏了当几百万条"。
    const uint MaxPluginsPerFile = 1024;

    [StructLayout(LayoutKind.Sequential)]
    public struct Version
    {
        public uint Major;
        public uint Minor;
        public uint Revision;

        public override readonly string ToString() => $"{Major}.{Minor}.{Revision}";
    }

    // clap_plugin_entry_t：.clap 导出的那一个变量。
    [StructLayout(LayoutKind.Sequential)]
    public struct PluginEntry
    {
        public Version ClapVersion;

        // bool init(const char *plugin_path)
        public delegate* unmanaged[Cdecl]<byte*, byte> Init;

        // void deinit(void)
        public delegate* unmanaged[Cdecl]<void> Deinit;

        // const void *get_factory(const char *factory_id)
        public delegate* unmanaged[Cdecl]<byte*, IntPtr> GetFactory;
    }

    // clap_plugin_factory_t。只声明到"问描述"；create_plugin 留给 P-C2。
    [StructLayout(LayoutKind.Sequential)]
    public struct PluginFactory
    {
        public delegate* unmanaged[Cdecl]<PluginFactory*, uint> GetPluginCount;
        public delegate* unmanaged[Cdecl]<PluginFactory*, uint, PluginDescriptor*> GetPluginDescriptor;
        public delegate* unmanaged[Cdecl]<PluginFactory*, IntPtr, byte*, IntPtr> CreatePlugin;
    }

    // clap_plugin_descriptor_t。**所有字符串都是 const char*，生命周期到 deinit 为止**：
    // 所以调用方必须在 deinit 之前把它们复制成托管字符串（见 ClapScanner）。
    [StructLayout(LayoutKind.Sequential)]
    public struct PluginDescriptor
    {
        public Version ClapVersion;

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
}
