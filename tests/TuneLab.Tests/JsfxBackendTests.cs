using System;
using System.IO;
using TuneLab.ExperimentalPatch.InsertHost;
using Xunit;

namespace TuneLab.Tests;

public class JsfxBackendTests
{
    [Fact]
    public void ListPlugins_ReadsHeaderAndBuildsRelativeId()
    {
        string root = Path.Combine(Path.GetTempPath(), "tunelab-jsfx-scan-" + Guid.NewGuid().ToString("N"));

        try
        {
            string sub = Path.Combine(root, "sub");
            Directory.CreateDirectory(sub);

            File.WriteAllText(Path.Combine(sub, "foo.jsfx"), """
                desc: My JSFX
                slider1:0.5<0,1,0.01>Amount

                @init
                x = slider1;

                @gfx
                gfx_rect(0, 0, 1, 1);
                """);

            File.WriteAllText(Path.Combine(root, "ignored.txt"), "not a jsfx");

            var backend = new JsfxBackend();
            var plugins = backend.ListPlugins([root], null, false, null);

            var plugin = Assert.Single(plugins);

            Assert.Equal(JsfxBackend.FormatName, plugin.Format);
            Assert.Equal("My JSFX", plugin.Name);
            Assert.Equal("jsfx:sub/foo.jsfx", plugin.PluginId);
            Assert.Equal(Path.GetFullPath(Path.Combine(sub, "foo.jsfx")), plugin.Locator);
            Assert.Contains("gfx", plugin.Category, StringComparison.OrdinalIgnoreCase);
            Assert.True(plugin.IsAvailable);
            Assert.False(plugin.IsInstrument);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ListPlugins_ReadsAuthorTagAsVendor()
    {
        string root = Path.Combine(Path.GetTempPath(), "tunelab-jsfx-scan-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);

            File.WriteAllText(Path.Combine(root, "tagged.jsfx"), """
                desc: Tagged
                author: Some Author
                slider1:0<0,1,0.01>Gain

                @init
                gain = slider1;
                """);

            // 不规范但很常见的注释写法（ysfx 也照读）。
            File.WriteAllText(Path.Combine(root, "commented.jsfx"), """
                desc: Commented
                //author: Comment Author

                @init
                gain = 1;
                """);

            // 没有 author 就留空 —— 菜单按"未知厂商"分组，而不是把格式当成作者。
            File.WriteAllText(Path.Combine(root, "bare.jsfx"), """
                desc: Bare

                @init
                gain = 1;
                """);

            // 头部元数据只在第一个 @section 之前有效，写在 @init 里不算。
            File.WriteAllText(Path.Combine(root, "late.jsfx"), """
                desc: Late

                @init
                author: Too Late
                """);

            var plugins = new JsfxBackend().ListPlugins([root], null, false, null);

            Assert.Equal("Some Author", Assert.Single(plugins, p => p.PluginId == "jsfx:tagged.jsfx").Vendor);
            Assert.Equal("Comment Author", Assert.Single(plugins, p => p.PluginId == "jsfx:commented.jsfx").Vendor);
            Assert.Equal(string.Empty, Assert.Single(plugins, p => p.PluginId == "jsfx:bare.jsfx").Vendor);
            Assert.Equal(string.Empty, Assert.Single(plugins, p => p.PluginId == "jsfx:late.jsfx").Vendor);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ListPlugins_FallsBackToFileNameWhenDescIsMissing()
    {
        string root = Path.Combine(Path.GetTempPath(), "tunelab-jsfx-scan-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "bare.jsfx"), """
                slider1:0<0,1,0.01>Gain

                @init
                gain = slider1;
                """);

            var plugins = new JsfxBackend().ListPlugins([root], null, false, null);
            var plugin = Assert.Single(plugins);

            Assert.Equal("bare", plugin.Name);
            Assert.Equal("jsfx:bare.jsfx", plugin.PluginId);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
