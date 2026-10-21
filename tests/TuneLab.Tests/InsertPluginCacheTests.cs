using System;
using System.IO;
using System.Linq;
using TuneLab;
using Xunit;

namespace TuneLab.Tests;

// 插件扫描缓存的"乐器不进目录"约定（TuneLab/ExperimentalPatchInsertSettings.cs）。
//
// 混音台的 FX 只能挂效果器：乐器没有 MIDI 输入可用，插进去也是哑的。所以扫描结果里
// 直接不留乐器 —— 缓存、菜单、设置页计数都看不到合成器。
//
// 这两条还钉住了一个兼容决定：旧缓存里根本没有 IsInstrument 这个字段（那一版不知道
// 哪个是乐器），所以整份缓存按"没扫过"处理，让界面提示去扫描，而不是把合成器摆回菜单。
public class InsertPluginCacheTests
{
    // DataFolder() 每次调用都读 TUNELAB_DATA_DIR，所以测试可以把数据目录指到临时目录。
    // 用锁串行化：环境变量是进程级的，两个用例同时改会互相抵消。
    static readonly object sLock = new();

    [Fact]
    public void InstrumentsAreNotPartOfTheCatalog()
    {
        // 3 = 当前格式（VST3 也从 moduleinfo.json / 二进制读厂商了）。
        // 旧数字 = 旧格式，整份缓存忽略。
        WithCache("""
        {
          "CacheVersion": 3,
          "Vst3Paths": [],
          "Vst2Paths": [],
          "LastScanUtc": "2026-10-21 00:00:00Z",
          "Cache": [
            { "Format": "VST3", "PluginId": "vst3:pro-q", "Name": "Pro-Q 4", "IsInstrument": false, "Locator": "fx.vst3" },
            { "Format": "VST3", "PluginId": "vst3:surge", "Name": "Surge XT", "IsInstrument": true, "Locator": "synth.vst3" }
          ]
        }
        """, settings =>
        {
            var plugins = settings.CachedPlugins;

            Assert.Single(plugins);
            Assert.Equal("Pro-Q 4", plugins[0].Name);
            Assert.DoesNotContain(plugins, plugin => plugin.IsInstrument);
        });
    }

    [Fact]
    public void OldCacheThatCannotTellInstrumentsApartIsIgnored()
    {
        // CacheVersion 缺失 = 那一版还没记录 IsInstrument：分不出乐器，整份缓存不可信。
        WithCache("""
        {
          "Vst3Paths": [],
          "Vst2Paths": [],
          "LastScanUtc": "2026-10-20 04:20:52Z",
          "Cache": [
            { "Format": "VST3", "PluginId": "vst3:surge", "Name": "Surge XT", "Locator": "synth.vst3" }
          ]
        }
        """, settings =>
        {
            Assert.Empty(settings.CachedPlugins);

            // "上次扫描时间"也要一起作废，否则界面上会说"扫过了"却列不出东西。
            Assert.Null(settings.LastScanText);
        });
    }

    static void WithCache(string json, Action<InsertPluginSettings> assert)
    {
        lock (sLock)
        {
            string folder = Path.Combine(Path.GetTempPath(), "TuneLab.Tests.insert-cache." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            string? previous = Environment.GetEnvironmentVariable("TUNELAB_DATA_DIR");
            Environment.SetEnvironmentVariable("TUNELAB_DATA_DIR", folder);

            try
            {
                File.WriteAllText(Path.Combine(folder, InsertPluginSettings.FileName), json);
                assert(new InsertPluginSettings());
            }
            finally
            {
                Environment.SetEnvironmentVariable("TUNELAB_DATA_DIR", previous);
                try { Directory.Delete(folder, true); } catch (Exception) { }
            }
        }
    }
}
