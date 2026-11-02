using System;
using System.IO;
using System.Linq;
using TuneLab.ExperimentalPatch.InsertHost;
using Xunit;

namespace TuneLab.Tests;

// CLAP 后端的宿主侧行为（P-C1）。
//
// 【为什么不加载插件】这一组刻意**不碰桥**：它钉的是"宿主这一层自己能保证的事"——
// 清单为空就不扫、路径不存在就不扫、Locator 编码能往返、标准目录按平台给。
// "真 .clap 能不能扫出来"是集成测试（ClapIntegrationTests）的事，两者分开跑：
// 前者在 CI 上恒真，后者只在装了 CLAP 插件的机器上有意义。
public class ClapBackendTests
{
    [Fact]
    public void FormatNameIsTheDisplayNameUsedByTheMenu()
    {
        // 菜单第一级、缓存里的 Format 字段、槽位的 Format 都是这个字符串。
        Assert.Equal("CLAP", ClapBackend.FormatName);

        IInsertBackend backend = new ClapBackend();
        Assert.Equal("CLAP", backend.FormatName);
    }

    [Fact]
    public void EmptyDirectoryListScansNothing()
    {
        // 扫哪 = 用户配的清单：清单为空就是不扫，不擅自去猜系统目录（EP-MIXER-12）。
        var backend = new ClapBackend();

        Assert.Empty(backend.ListPlugins(null));
        Assert.Empty(backend.ListPlugins([]));
    }

    [Fact]
    public void MissingDirectoriesScanNothing()
    {
        var backend = new ClapBackend();
        string missing = Path.Combine(Path.GetTempPath(), "tunelab-no-such-clap-dir-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(backend.ListPlugins([missing]));
    }

    [Fact]
    public void LocatorCarriesTheFileAndThePluginId()
    {
        // 一个 .clap 可以声明多个插件：定位一个具体插件要"文件 + id"两段（见 ClapBackend）。
        string locator = ClapBackend.MakeLocator(@"C:\plugins\Surge XT Effects.clap", "clap:com.surge-synth-team.surge-xt-effects");

        Assert.True(ClapBackend.TrySplitLocator(locator, out string path, out string pluginId));
        Assert.Equal(@"C:\plugins\Surge XT Effects.clap", path);
        Assert.Equal("clap:com.surge-synth-team.surge-xt-effects", pluginId);
    }

    [Fact]
    public void LocatorWithoutSeparatorIsRejected()
    {
        // 老槽位 / 手改坏了的 Locator：不能猜，交给调用方如实报"重新扫描"。
        Assert.False(ClapBackend.TrySplitLocator(@"C:\plugins\x.clap", out _, out _));
        Assert.False(ClapBackend.TrySplitLocator("\nclap:x", out _, out _));
        Assert.False(ClapBackend.TrySplitLocator(@"C:\plugins\x.clap\n", out _, out _));
    }

    [Fact]
    public void StandardPathsExistAndArePlatformSpecific()
    {
        // 默认清单不能是空的：设置页第一次打开时用户得看见"该往哪放"。
        var paths = ClapPaths.Default;

        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.False(string.IsNullOrWhiteSpace(p)));

        if (OperatingSystem.IsWindows())
            Assert.Contains(paths, p => p.EndsWith("CLAP", StringComparison.OrdinalIgnoreCase));
        else if (OperatingSystem.IsMacOS())
            Assert.Contains(paths, p => p.Contains("Plug-Ins/CLAP", StringComparison.Ordinal));
        else
            Assert.Contains(paths, p => p.EndsWith("clap", StringComparison.OrdinalIgnoreCase));
    }

    // features 的乐器判定口径（与 VST3 一致：宁可漏判，不可误判）由桥侧的 ClapScanner 负责，
    // 这里不重复实现 —— 那份规则只有桥会用到，单测钉它只能钉一个副本。
    // 真插件的判定在 ClapIntegrationTests 里对着一堆真 features 验。

    [Fact]
    public void BrokenLocatorIsRejectedBeforeTouchingTheBridge()
    {
        // Locator 是"文件\npluginId"两段。老工程 / 手改坏的槽位不能猜：如实报"重新扫描"。
        // 这一条刻意不碰桥（坏 Locator 在执行任何插件代码之前就该被拒）。
        var backend = new ClapBackend();

        var created = backend.Create(
            new InsertPluginInfo
            {
                Format = ClapBackend.FormatName,
                PluginId = "clap:com.example.test",
                Name = "Test",
                Locator = @"C:\x\test.clap",
            },
            new InsertSettings(),
            out string? reason);

        Assert.Null(created);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void PlaybackIsWiredInThisBuild()
    {
        // P-C2 的边界：能扫也能插。没有这条的话，"播放没接线"这种半成品状态会悄悄回来 ——
        // 用户看到的是菜单里有条目、点了却插不进来。
        Assert.True(ClapBackend.PlaybackAvailable);
    }

    [Fact]
    public void BackendsListIncludesClapOnce()
    {
        // 菜单按 Backends 的第一级列格式：CLAP 必须恰好出现一次（重复 = 菜单两个 CLAP）。
        var factory = new TuneLab.Audio.InsertInstanceFactory();
        var formats = factory.Backends.Select(b => b.FormatName).ToList();

        Assert.Single(formats, f => f == ClapBackend.FormatName);
    }
}
