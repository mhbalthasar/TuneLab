using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TuneLab.ExperimentalPatch.InsertHost;
using Xunit;

namespace TuneLab.Tests;

// 真 .clap 文件的端到端扫描（P-C1 的验收）。
//
// 【它验的是什么】"宿主枚举文件 → 桥加载每个 .clap → 问描述 → 回一份带厂商/类别的清单"
// 这条链真的通。CLAP 没有可静态读取的元数据，所以这是唯一能证明扫描可用的办法：
// 拿这台机器上真装着的插件跑一遍。
//
// 【为什么可能跳过】要求机器上真装了 CLAP 插件（或用 TL_CLAP_DIR 指定目录）。
// 与 Vst3IntegrationTests 同一套约定：没有插件 / 没有发布桥就 Skip。
public class ClapIntegrationTests
{
    const string ClapDirVariable = "TL_CLAP_DIR";

    // 开发机上的两份：Y: 是手工收藏（带乐器），Common Files 是安装器默认位置。
    static readonly string[] FallbackDirectories =
    [
        @"Y:\Insts\VSTEffects\CLAP",
        @"C:\Program Files\Common Files\CLAP",
        "/usr/lib/clap",
    ];

    static IEnumerable<string> ClapDirectories()
        => new[] { Environment.GetEnvironmentVariable(ClapDirVariable) }
            .Concat(FallbackDirectories)
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    // 桥由 tools/publish-bridges.ps1 发到应用输出，测试进程旁边通常没有。
    // VstBridgeClient 认 TUNELAB_BRIDGE_DIR，这里从测试输出往上找应用输出并指过去。
    static ClapIntegrationTests()
    {
        if (VstBridgeClient.BridgeExecutableExists)
            return;

        string baseDirectory = AppContext.BaseDirectory;
        string configuration = new DirectoryInfo(baseDirectory.TrimEnd('\\', '/')).Parent?.Name ?? "Debug";

        for (DirectoryInfo? current = new(baseDirectory); current != null; current = current.Parent)
        {
            string candidate = Path.Combine(current.FullName, "TuneLab", "bin", configuration, "net8.0", "bridge");

            if (Directory.Exists(candidate))
            {
                Environment.SetEnvironmentVariable("TUNELAB_BRIDGE_DIR", candidate);
                return;
            }
        }
    }

    static ClapBackend BackendOrSkip()
    {
        var backend = new ClapBackend();

        Skip.Unless(ClapDirectories().Any(), "no CLAP plugin folder on this machine");
        Skip.If(!backend.IsAvailable, $"the isolated host is missing: {backend.UnavailableReason}");

        return backend;
    }

    [Fact]
    public void Backend_SeesTheIsolatedHost()
    {
        var backend = new ClapBackend();

        Skip.Unless(ClapDirectories().Any(), "no CLAP plugin folder on this machine");
        Skip.If(!backend.IsAvailable, $"the isolated host is missing: {backend.UnavailableReason}");
    }

    [Fact]
    public void Scan_DescribesRealPluginsThroughTheBridge()
    {
        var backend = BackendOrSkip();

        // 进度：一个 .clap 一个"正在检查 xxx"（描述要加载才能拿到，所以进度必须按文件报）。
        var checking = new List<string>();

        var entries = backend.ListPlugins(ClapDirectories().ToList(), onDetail: name =>
        {
            lock (checking)
                checking.Add(name);
        });

        Assert.NotEmpty(entries);
        Assert.NotEmpty(checking);

        Assert.All(entries, e =>
        {
            // 宿主这一层统一用显示名，缓存里不能出现 "clap" 与 "CLAP" 两组。
            Assert.Equal(ClapBackend.FormatName, e.Format);

            // 一个文件里可以有多个插件，所以 Locator 必须是"文件 + pluginId"两段。
            Assert.True(ClapBackend.TrySplitLocator(e.Locator, out string path, out string pluginId));
            Assert.False(string.IsNullOrWhiteSpace(pluginId));
            Assert.False(string.IsNullOrWhiteSpace(path));

            Assert.StartsWith("clap:", e.PluginId);

            if (e.IsAvailable)
            {
                // 可用条目的 id 一定是描述里的反向域名（占位条目走的是 clap:file: 前缀）。
                Assert.DoesNotContain("clap:file:", e.PluginId);
            }
        });

        // 厂商是插件自己报的（不是从路径猜的）：这台机器上的真插件至少有一个带 vendor。
        Assert.Contains(entries, e => !string.IsNullOrWhiteSpace(e.Vendor));

        // P-C1 的边界：能扫、不能插。所有条目都必须是"不可用 + 有原因"，
        // 否则菜单会给用户一个点了注定失败的入口。
        if (!ClapBackend.PlaybackAvailable)
        {
            Assert.All(entries, e =>
            {
                Assert.False(e.IsAvailable);
                Assert.False(string.IsNullOrWhiteSpace(e.UnavailableReason));
            });
        }
    }

    [Fact]
    public void Scan_ClassifiesInstrumentsFromDeclaredFeatures()
    {
        var backend = BackendOrSkip();

        var entries = backend.ListPlugins(ClapDirectories().ToList());

        // Surge XT 与 Surge XT Effects 来自同一套安装的两个 .clap：
        // 前者是乐器、后者是效果器。判错任何一边都意味着菜单里多了/少了一条。
        var synth = entries.FirstOrDefault(e => e.PluginId == "clap:org.surge-synth-team.surge-xt");
        var effects = entries.FirstOrDefault(e => e.PluginId == "clap:org.surge-synth-team.surge-xt-fx");

        if (synth == null || effects == null)
            return;   // 这台机器没装 Surge XT：别的断言交给上一条测试

        Assert.True(synth.IsInstrument);
        Assert.False(effects.IsInstrument);
        Assert.Equal("Surge Synth Team", synth.Vendor);
    }

    [Fact]
    public void Scan_ReportsFilesThatAreNotClapPlugins()
    {
        // 扩展名对、内容不对的文件必须被标出来（灰掉 + 原因），而不是让用户点进去才发现。
        // 这也是"加载发生在桥里"的现场证明：宿主没有把它拉进自己进程。
        var backend = BackendOrSkip();

        string directory = Path.Combine(Path.GetTempPath(), "tunelab-clap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "broken.clap"), "not a plugin");

            var entries = backend.ListPlugins([directory]);

            var entry = Assert.Single(entries);

            Assert.Equal(ClapBackend.FormatName, entry.Format);
            Assert.False(entry.IsAvailable);
            Assert.False(string.IsNullOrWhiteSpace(entry.UnavailableReason));
            Assert.StartsWith("clap:file:broken", entry.PluginId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
