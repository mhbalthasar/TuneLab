using System;
using System.IO;
using System.Linq;
using TuneLab;
using Xunit;

namespace TuneLab.Tests;

// insert-plugins.json 的约定（TuneLab/ExperimentalPatchInsertSettings.cs）。
//
// 一、扫描缓存的"乐器不进目录"：混音台的 FX 只能挂效果器 —— 乐器没有 MIDI 输入可用，
//     插进去也是哑的。所以扫描结果里直接不留乐器：缓存、菜单、设置页计数都看不到合成器。
//     这两条还钉住了一个兼容决定：旧缓存里根本没有 IsInstrument 这个字段（那一版不知道
//     哪个是乐器），所以整份缓存按"没扫过"处理，让界面提示去扫描，而不是把合成器摆回菜单。
//
// 二、[VST3-BRIDGE P5.5] 崩溃隔离的默认值：没设置过 = 这台机器能隔离就隔离；用户的显式选择
//     永远优先；旧版本写下的 false 不算选择（那一版默认就是关）。状态行要靠"桥可用不可用"
//     与"上一次回退原因"这两条实时信息，所以这里连带钉住它们。
public class InsertPluginSettingsTests
{
    // DataFolder() 每次调用都读 TUNELAB_DATA_DIR，所以测试可以把数据目录指到临时目录。
    // 用锁串行化：环境变量是进程级的，两个用例同时改会互相抵消。
    static readonly object sLock = new();

    [Fact]
    public void InstrumentsAreNotPartOfTheCatalog()
    {
        // 4 = 当前格式（完整扫描会把 VST3 的厂商按官方口径写进来）。
        // 旧数字 = 旧格式，整份缓存忽略。
        WithCache("""
        {
          "CacheVersion": 4,
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

    // ── [VST3-BRIDGE P5.5] 隔离开关的默认值 / 迁移 / 状态行 ──

    [Fact]
    public void IsolationIsOnByDefaultWhenTheBridgeIsThere()
    {
        WithIsolation(null, bridgeAvailable: true, settings =>
        {
            Assert.True(settings.IsolateVst3Available);
            Assert.True(settings.IsolateVst3);
            Assert.Null(settings.IsolateVst3UnavailableReason);
            Assert.Null(settings.IsolateVst3FallbackReason);
        });
    }

    [Fact]
    public void IsolationIsOnByDefaultEvenWithoutTheBridge()
    {
        // 默认值**不跟"本机有没有桥"走**：所有平台一律开，桥不在就交给后端的回退路径
        // （Vst3Backend 记 IsolationFallbackReason、宿主写日志、状态行写"现在在进程内"）。
        // 默认值要是跟着可用性变，同一台机器在不同部署上就有两种默认 —— 那不叫跨平台一致。
        WithIsolation(null, bridgeAvailable: false, settings =>
        {
            Assert.False(settings.IsolateVst3Available);
            Assert.True(settings.IsolateVst3);
            Assert.False(string.IsNullOrEmpty(settings.IsolateVst3UnavailableReason));
        });
    }

    [Fact]
    public void AnExplicitChoiceBeatsTheDefault()
    {
        WithIsolation(null, bridgeAvailable: true, settings =>
        {
            settings.SetIsolateVst3(false);

            // 新实例 = 重新读盘：用户的"关"必须留着。
            Assert.False(new InsertPluginSettings().IsolateVst3);
        });

        // 反过来也一样：桥不在时用户要开，就开（加载时会回退，但那是他要的）。
        WithIsolation(null, bridgeAvailable: false, settings =>
        {
            settings.SetIsolateVst3(true);
            Assert.True(new InsertPluginSettings().IsolateVst3);
        });
    }

    [Fact]
    public void TheOldDefaultIsNotMistakenForAChoice()
    {
        // P5.5 之前的默认就是关，任何一次扫描 / 保存都会写出 IsolateVst3: false ——
        // 那个 false 不是用户的选择，迁移后要按"没设置过"处理，让新默认生效。
        WithIsolation("""
        {
          "CacheVersion": 4,
          "IsolateVst3": false,
          "Cache": []
        }
        """, bridgeAvailable: true, settings =>
        {
            Assert.True(settings.IsolateVst3);

            // 迁移完写成带版本号的新文件；没设置的开关干脆不写键 ——
            // 老版本程序读到 "IsolateVst3": null 会当反序列化失败，把整份扫描路径一起丢掉。
            InsertPluginSettings.Save(InsertPluginSettings.Load());

            string text = File.ReadAllText(InsertPluginSettings.SettingsPath);
            Assert.Contains("\"SettingsVersion\": 2", text);
            Assert.DoesNotContain("IsolateVst3", text);
        });
    }

    [Fact]
    public void TheLastFallbackReasonIsVisibleUntilIsolationWorksAgain()
    {
        lock (sLock)
        {
            try
            {
                InsertPluginSettings.NoteIsolationFallback("the bridge does not carry vst3");
                Assert.Equal("the bridge does not carry vst3", new InsertPluginSettings().IsolateVst3FallbackReason);

                // 成功隔离一次（或用户重新选了开关）后要清掉：状态行不该永远挂着一次旧失败。
                InsertPluginSettings.NoteIsolationFallback(null);
                Assert.Null(new InsertPluginSettings().IsolateVst3FallbackReason);
            }
            finally
            {
                InsertPluginSettings.NoteIsolationFallback(null);
            }
        }
    }

    // VST2 目录里"其实不是 VST2"的 dll（真机上就有人把 LV2 的二进制丢进去了）：
    // 扫描要把它标成不可用 + 原因，并且**落进缓存**，重启后菜单里仍然是灰的。
    // 以前它带着"可用"的招牌列出来，用户插进去才看到 "the file is not a VST2 plugin"。
    [Fact]
    public void ScanningMarksAFileThatIsNotVst2AsUnavailable()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TuneLab.Tests.vst2-scan." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            PeFixtures.Write(directory, "realvst2.dll", PeFixtures.Build(exportName: "VSTPluginMain"));
            PeFixtures.Write(directory, "lv2inside.dll", PeFixtures.Build(exportName: "lv2_descriptor"));

            WithCache(null, settings =>
            {
                settings.SetPaths("VST2", [directory]);
                settings.SetPaths("VST3", []);

                var scanned = settings.Scan();

                var lv2 = Assert.Single(scanned, p => p.Name == "lv2inside");
                Assert.False(lv2.IsAvailable);
                Assert.Contains("not a VST2 plugin", lv2.UnavailableReason ?? string.Empty);

                var real = Assert.Single(scanned, p => p.Name == "realvst2");
                Assert.True(real.IsAvailable);
                Assert.Null(real.UnavailableReason);

                // 菜单读的是缓存：重启之后标记不能丢。
                var cached = Assert.Single(new InsertPluginSettings().CachedPlugins, p => p.Name == "lv2inside");
                Assert.False(cached.IsAvailable);
            });
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (Exception) { }
        }
    }

    static void WithCache(string json, Action<InsertPluginSettings> assert)
    {
        WithIsolation(json, bridgeAvailable: false, assert);
    }

    // 隔离的默认值 / 迁移 / 状态行：把"本机有没有桥"的探针换掉，才可能脱离这台机器断言。
    // 探针是进程级静态，和 TUNELAB_DATA_DIR 一样用 sLock 串行化。
    static void WithIsolation(string? json, bool bridgeAvailable, Action<InsertPluginSettings> assert)
    {
        lock (sLock)
        {
            string folder = Path.Combine(Path.GetTempPath(), "TuneLab.Tests.insert-cache." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            string? previousDir = Environment.GetEnvironmentVariable("TUNELAB_DATA_DIR");
            var previousProbe = InsertPluginSettings.IsolationAvailableProbe;

            Environment.SetEnvironmentVariable("TUNELAB_DATA_DIR", folder);
            InsertPluginSettings.IsolationAvailableProbe = () => bridgeAvailable;

            try
            {
                if (json != null)
                    File.WriteAllText(Path.Combine(folder, InsertPluginSettings.FileName), json);

                assert(new InsertPluginSettings());
            }
            finally
            {
                InsertPluginSettings.IsolationAvailableProbe = previousProbe;
                Environment.SetEnvironmentVariable("TUNELAB_DATA_DIR", previousDir);
                try { Directory.Delete(folder, true); } catch (Exception) { }
            }
        }
    }
}

