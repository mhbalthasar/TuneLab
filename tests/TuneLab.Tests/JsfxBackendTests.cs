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
