using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

// 一个已加载的 .clap 库，带引用计数。
//
// 【为什么需要引用计数】CLAP 规定 `clap_entry.init()` 每个库**每进程只调一次**，
// `deinit()` 与卸载要等所有实例都没了。而这座桥同时干两件事：
//   · 扫描（加载 → 问描述 → 卸载）
//   · 播放（实例一直挂着）
// 没有计数的话，"插着一个 CLAP 插件时点立即扫描"会 deinit + Free 掉活实例脚下的库。
//
// 【线程】桥是单线程的（收一条消息、办一件事），所以这里不需要锁；
// 与 PluginHost / Vst3PluginHost 同一前提。谁哪天给桥加线程，这里要一起改。
internal static unsafe class ClapLibrary
{
    sealed class Loaded
    {
        public IntPtr Handle;
        public ClapNative.PluginEntry Entry;
        public int RefCount;
    }

    // 键 = 解析后的二进制路径（Windows 不区分大小写；Linux 区分，但同一个文件写两遍
    // 只会走到同一个键上，宁可多认一次不认两次）。
    static readonly Dictionary<string, Loaded> sLoaded = new(StringComparer.OrdinalIgnoreCase);

    // 一个租约：Acquire 加一，Dispose 减一。实例与扫描各持一份。
    internal sealed class Lease : IDisposable
    {
        readonly string mKey;
        bool mDisposed;

        internal Lease(string key, IntPtr handle, ClapNative.PluginEntry entry, ClapNative.PluginFactory* factory)
        {
            mKey = key;
            Handle = handle;
            Entry = entry;
            Factory = factory;
        }

        public IntPtr Handle { get; }

        public ClapNative.PluginEntry Entry { get; }

        // 插件工厂。**只在租约有效期内可用**（deinit 之后指针就悬了）。
        public ClapNative.PluginFactory* Factory { get; }

        public void Dispose()
        {
            if (mDisposed)
                return;

            mDisposed = true;
            Release(mKey);
        }
    }

    /// <summary>
    /// 加载并 init 一个 .clap（已经加载过就只加引用计数）。失败返回 null 并写 reason。
    /// </summary>
    public static Lease? Acquire(string path, out string? reason)
    {
        reason = null;

        string? binary = ResolveBinary(path);

        if (binary == null)
        {
            reason = "the .clap has no loadable binary";
            return null;
        }

        if (sLoaded.TryGetValue(binary, out var known))
        {
            known.RefCount++;
            return Build(binary, known);
        }

        IntPtr library;

        try
        {
            library = NativeLibrary.Load(binary);
        }
        catch (Exception ex)
        {
            // 最常见：缺依赖 / 位数不对 / 根本不是动态库。
            reason = "cannot load the plugin library: " + ex.Message;
            return null;
        }

        if (!NativeLibrary.TryGetExport(library, "clap_entry", out IntPtr entryPointer))
        {
            NativeLibrary.Free(library);
            reason = "not a CLAP plugin (no clap_entry export)";
            return null;
        }

        var entry = *(ClapNative.PluginEntry*)entryPointer;

        if (entry.ClapVersion.Major != ClapNative.SupportedMajor)
        {
            NativeLibrary.Free(library);
            reason = $"built for CLAP {entry.ClapVersion}, this host speaks {ClapNative.SupportedMajor}.x";
            return null;
        }

        if (entry.Init == null || entry.GetFactory == null)
        {
            NativeLibrary.Free(library);
            reason = "the plugin declares an incomplete entry point";
            return null;
        }

        byte[] pathBytes = ClapNative.Utf8(binary);
        byte initialized;

        fixed (byte* pathPointer = pathBytes)
            initialized = entry.Init(pathPointer);

        if (initialized == 0)
        {
            NativeLibrary.Free(library);
            reason = "the plugin refused to initialize";
            return null;
        }

        var loaded = new Loaded
        {
            Handle = library,
            Entry = entry,
            RefCount = 1,
        };

        var lease = Build(binary, loaded, out string? factoryReason);

        if (lease == null)
        {
            // init 过了但没有工厂：收拾干净再报错（不留下"半加载"的条目）。
            if (entry.Deinit != null)
                entry.Deinit();

            NativeLibrary.Free(library);
            reason = factoryReason ?? "the plugin has no clap.plugin-factory";
            return null;
        }

        sLoaded[binary] = loaded;
        return lease;
    }

    static Lease? Build(string key, Loaded loaded) => Build(key, loaded, out _);

    static Lease? Build(string key, Loaded loaded, out string? reason)
    {
        reason = null;

        IntPtr factoryPointer;
        byte[] factoryId = ClapNative.Utf8(ClapNative.PluginFactoryId);

        fixed (byte* idPointer = factoryId)
            factoryPointer = loaded.Entry.GetFactory(idPointer);

        if (factoryPointer == IntPtr.Zero)
        {
            reason = "the plugin has no clap.plugin-factory";
            return null;
        }

        var factory = (ClapNative.PluginFactory*)factoryPointer;

        if (factory->GetPluginCount == null || factory->GetPluginDescriptor == null
            || factory->CreatePlugin == null)
        {
            reason = "the plugin factory is incomplete";
            return null;
        }

        return new Lease(key, loaded.Handle, loaded.Entry, factory);
    }

    static void Release(string key)
    {
        if (!sLoaded.TryGetValue(key, out var loaded))
            return;

        if (--loaded.RefCount > 0)
            return;

        sLoaded.Remove(key);

        try
        {
            if (loaded.Entry.Deinit != null)
                loaded.Entry.Deinit();
        }
        catch (Exception)
        {
            // 卸载阶段插件抛错：库马上要丢，吞掉（与 Vst3PluginHost.Unload 同一条纪律）。
        }

        NativeLibrary.Free(loaded.Handle);
    }

    /// <summary>
    /// .clap 在 Windows / Linux 上是一个文件；在 macOS 上是**目录包**
    /// （Foo.clap/Contents/MacOS/Foo）。加载器要的是里面那个二进制。
    /// </summary>
    public static string? ResolveBinary(string path)
    {
        if (File.Exists(path))
            return path;

        if (!Directory.Exists(path))
            return null;

        string macos = Path.Combine(path, "Contents", "MacOS");

        if (!Directory.Exists(macos))
            return null;

        foreach (string candidate in Directory.EnumerateFiles(macos))
            return candidate;

        return null;
    }
}
