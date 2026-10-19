using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OwnVST3Host;
using TuneLab.ExperimentalPatch.MixerEngine;
using VstInsert = TuneLab.ExperimentalPatch.InsertHost.Vst3Insert;

namespace TuneLab.ExperimentalPatch.InsertHost;

// VST3 后端：把 OwnAudioVST3（降级 vendor 在 Vendored/）接到引擎的 IMixInsert 上。
//
// 【线程分工】本类只在 UI 线程用（ListPlugins / Create）。
// 真正在音频线程上跑的是 Vst3Insert（见那个文件顶部的说明）。
// 两者的交界处只有一次"装配完成"：插件 Initialize 好、状态载入好，然后才交给音频线程。
//
// 【.tlhost 后缀】原生库叫 ownvst3.tlhost.dll，而 vendor 的托管层写死了几个候选路径
// （它自己的 GetNativeLibraryPath 不会找到改过名的文件）。所以这里不用托管层的自动定位，
// 而是自己算出路径、显式传给 OwnVst3Wrapper(string) 构造。
public sealed class Vst3Backend : IInsertBackend
{
    // 我们的 native dll 文件名（设计文档 §5.4 的"改名"方案，见 Vendored/README）。
    public const string NativeFileName = "ownvst3.tlhost.dll";

    string IInsertBackend.FormatName => "VST3";

    public bool IsAvailable => NativePath != null;

    public string? UnavailableReason { get; private set; }

    string? NativePath { get; set; }

    public Vst3Backend()
    {
        NativePath = FindNativeLibrary();

        if (NativePath == null)
        {
            UnavailableReason = "VST3 host library is missing";
            return;
        }

        // 托管层内部还有几处 DllImport（编辑器那条路）走默认解析，
        // 装上 resolver 才不会去加载没改名的那一份。
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Vst3Backend).Assembly, ResolveNative);
        }
        catch (Exception)
        {
            // 重复绑定是预期的（多个后端/测试进程各装一次），忽略。
        }
    }

    // 找 native dll 的规则（按优先级）：
    //   1. 本程序集同目录（宿主 exe 旁边 —— 我们随应用分发的那一份）
    //   2. 环境变量 TL_VST3_NATIVE 指明的路径（排查 / 特殊部署用）
    // 不做"去系统目录搜"：那正是我们要避免的撞车来源。
    static string? FindNativeLibrary()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, NativeFileName);
        if (File.Exists(beside))
            return beside;

        string? fromEnv = Environment.GetEnvironmentVariable("TL_VST3_NATIVE");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        return null;
    }

    static IntPtr ResolveNative(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "ownvst3.dll", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(libraryName, "ownvst3", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        string candidate = Path.Combine(AppContext.BaseDirectory, NativeFileName);
        return File.Exists(candidate) ? NativeLibrary.Load(candidate) : IntPtr.Zero;
    }

    public IReadOnlyList<InsertPluginInfo> ListPlugins()
    {
        if (NativePath == null)
            return [];

        try
        {
            List<PluginDescriptor> found;
            try
            {
                found = PluginScanner.ScanAsync().GetAwaiter().GetResult().ToList();
            }
            catch (Exception)
            {
                // 这个库里扫描器的版本可能不对（我们的 native 是 1.7.0，带扫描器；
                // 换版本时兜住）。扫不出来只当"没有插件"，不让整个菜单挂掉。
                return [];
            }

            var list = new List<InsertPluginInfo>(found.Count);
            foreach (var plugin in found)
            {
                list.Add(new InsertPluginInfo
                {
                    Format = "VST3",
                    PluginId = PluginIdOf(plugin),
                    Name = plugin.Name,
                    Vendor = plugin.Vendor,
                    Category = plugin.Category,
                    Locator = plugin.Identifier,
                });
            }

            return list;
        }
        catch (Exception)
        {
            return [];
        }
    }

    // 插件 id：classId 优先（VST3 的稳定标识），拿不到就用 bundle 名兜底。
    // 落盘的真实内容，DESIGN.md §9.5 明确"pluginId 不存路径"。
    static string PluginIdOf(PluginDescriptor plugin)
        => string.IsNullOrWhiteSpace(plugin.Identifier)
            ? plugin.Name
            : "vst3:" + plugin.Identifier;

    public IMixInsert? Create(InsertPluginInfo plugin, InsertSettings settings, out string? reason)
    {
        reason = null;

        string? dll = NativePath;
        if (dll == null)
        {
            reason = "VST3 host library is missing";
            return null;
        }

        if (string.IsNullOrEmpty(plugin.Locator))
        {
            reason = "plugin location is unknown (rescan first)";
            return null;
        }

        OwnVst3Wrapper wrapper;
        try
        {
            wrapper = new OwnVst3Wrapper(dll);
        }
        catch (Exception ex)
        {
            reason = "failed to load the VST3 host library: " + ex.Message;
            return null;
        }

        try
        {
            if (!wrapper.LoadPlugin(plugin.Locator))
            {
                reason = "the plugin could not be loaded (bitness or a missing dependency)";
                wrapper.Dispose();
                return null;
            }

            double sampleRate = settings.SampleRate > 0 ? settings.SampleRate : 48000;
            int block = settings.MaxBlockSize > 0 ? settings.MaxBlockSize : 512;

            if (!wrapper.Initialize(sampleRate, block))
            {
                reason = "the plugin refused to initialise at " + sampleRate + " Hz";
                wrapper.Dispose();
                return null;
            }

            // 只看这个：是**效果器**才挂。乐器（IsInstrument）在这里毫无意义——
            // 它要收 MIDI、要自己产声音，而我们的插入点是"处理已经存在的音频"。
            if (wrapper.IsInstrument)
            {
                reason = "this plugin is an instrument; inserts need an effect";
                wrapper.Dispose();
                return null;
            }

            return new VstInsert(plugin, wrapper, sampleRate, block, settings.Bypass);
        }
        catch (Exception ex)
        {
            reason = "unexpected error while loading the plugin: " + ex.Message;
            wrapper.Dispose();
            return null;
        }
    }

    // 重新定位：用户在别的机器上打开工程，插件扫描结果变了。
    // PluginId 是软的（路径会变），所以这里就是"按 PluginId 再扫一次"。
    public InsertPluginInfo? Resolve(string pluginId)
        => ListPlugins().FirstOrDefault(p => p.PluginId == pluginId);
}
