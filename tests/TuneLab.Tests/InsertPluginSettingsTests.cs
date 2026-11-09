using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TuneLab;
using TuneLab.Audio;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.InsertHost;
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
        // 6 = 当前格式（JSFX 也进目录）。旧数字 = 旧格式，整份缓存忽略。
        WithCache("""
        {
          "CacheVersion": 6,
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

    // ── [VST3-BRIDGE] VST3 一律隔离：这一节只剩"状态行读什么" ──

    [Fact]
    public void Vst3IsolationAvailabilityFollowsTheProbe()
    {
        WithSettings(null, bridgeAvailable: true, settings =>
        {
            Assert.True(settings.Vst3IsolationAvailable);
            Assert.Null(settings.Vst3IsolationUnavailableReason);
        });

        WithSettings(null, bridgeAvailable: false, settings =>
        {
            Assert.False(settings.Vst3IsolationAvailable);
            Assert.False(string.IsNullOrEmpty(settings.Vst3IsolationUnavailableReason));
        });
    }

    [Fact]
    public void AnOldFileWithTheRemovedIsolationSwitchStillLoadsItsPaths()
    {
        // 旧版本写下的 IsolateVst3 键现在没有对应属性了：反序列化要当成"没这个键"，
        // 绝不能因此把整份清单（用户的扫描路径）一起丢掉。
        WithSettings("""
        {
          "CacheVersion": 4,
          "IsolateVst3": false,
          "Vst3Paths": [ "C:\\VST3" ],
          "Cache": []
        }
        """, bridgeAvailable: true, settings =>
        {
            Assert.Equal(["C:\\VST3"], settings.Paths["VST3"]);
        });
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

    // VST3 的两套历史 id 形态（路径型 vs bundle 名）必须归一，否则工程里存的短 id
    // 会命中 x86 副本 —— 而 x64 桥的 LoadLibrary 碰不了 32 位 bundle。
    [Fact]
    public void Vst3CopiesNormalizeToTheSameIdAndThe64BitOneWins()
    {
        WithCache("""
        {
          "CacheVersion": 6,
          "Vst3Paths": [],
          "Vst2Paths": [],
          "LastScanUtc": "2026-11-07 00:00:00Z",
          "Cache": [
            {
              "Format": "VST3",
              "PluginId": "vst3:C:/Program Files/Common Files/VST3/ChowPhaserStereo.vst3/Contents/x86_64-win/ChowPhaserStereo.vst3",
              "Name": "ChowPhaserStereo",
              "Locator": "C:/Program Files/Common Files/VST3/ChowPhaserStereo.vst3/Contents/x86_64-win/ChowPhaserStereo.vst3"
            },
            {
              "Format": "VST3",
              "PluginId": "vst3:ChowPhaserStereo",
              "Name": "ChowPhaserStereo",
              "Locator": "C:/Program Files (x86)/Common Files/VST3/ChowPhaserStereo.vst3/Contents/x86-win/ChowPhaserStereo.vst3"
            }
          ]
        }
        """, settings =>
        {
            var plugin = Assert.Single(settings.CachedPlugins);

            Assert.Equal("vst3:ChowPhaserStereo", plugin.PluginId);
            Assert.Contains("x86_64-win", plugin.Locator);
            Assert.True(plugin.IsAvailable);
            Assert.Null(plugin.UnavailableReason);
        });
    }

    [Fact]
    public void A32BitOnlyVst3CopyIsListedButUnavailable()
    {
        WithCache("""
        {
          "CacheVersion": 6,
          "Vst3Paths": [],
          "Vst2Paths": [],
          "LastScanUtc": "2026-11-07 00:00:00Z",
          "Cache": [
            {
              "Format": "VST3",
              "PluginId": "vst3:ChowPhaserStereo",
              "Name": "ChowPhaserStereo",
              "Locator": "C:/Program Files (x86)/Common Files/VST3/ChowPhaserStereo.vst3/Contents/x86-win/ChowPhaserStereo.vst3"
            }
          ]
        }
        """, settings =>
        {
            var plugin = Assert.Single(settings.CachedPlugins);

            Assert.False(plugin.IsAvailable);
            Assert.Contains("32-bit VST3", plugin.UnavailableReason ?? string.Empty);
        });
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Common Files\VST3\X.vst3\Contents\x86-win\X.vst3", true)]
    [InlineData("C:/Program Files (x86)/Common Files/VST3/X.vst3/Contents/x86-win/X.vst3", true)]
    [InlineData(@"C:\Program Files\Common Files\VST3\X.vst3\Contents\x86_64-win\X.vst3", false)]
    [InlineData("", false)]
    public void Vst3BitnessIsReadFromTheBundlePath(string locator, bool expected)
        => Assert.Equal(expected, Vst3Backend.Is32BitBundle(locator));

    // 旧缓存中 x64 条目的 PluginId 是完整路径，x86 条目是短 id；工程只存短 id。
    // 解析必须按 bundle 名找回 x64 条目，而不是精确匹配到 x86 后让 x64 桥去加载它。
    [Fact]
    public void ResolvingAProjectIdPrefersThe64BitCacheEntry()
    {
        var cache = new List<InsertPluginEntry>
        {
            new()
            {
                Format = "VST3",
                PluginId = @"vst3:C:\Program Files\Common Files\VST3\ChowPhaserStereo.vst3\Contents\x86_64-win\ChowPhaserStereo.vst3",
                Name = "ChowPhaserStereo",
                Locator = @"C:\Program Files\Common Files\VST3\ChowPhaserStereo.vst3\Contents\x86_64-win\ChowPhaserStereo.vst3",
                IsAvailable = true,
            },
            new()
            {
                Format = "VST3",
                PluginId = "vst3:ChowPhaserStereo",
                Name = "ChowPhaserStereo",
                Locator = @"C:\Program Files (x86)\Common Files\VST3\ChowPhaserStereo.vst3\Contents\x86-win\ChowPhaserStereo.vst3",
                IsAvailable = true,
            },
        };

        var plugin = new InsertInstanceFactory()
            .FindPlugin(new Vst3Backend(), "vst3:ChowPhaserStereo", cache);

        Assert.NotNull(plugin);
        Assert.Contains("x86_64-win", plugin!.Locator);
    }

    [Fact]
    public void AVst3PluginWithoutA64BitCopyIsRejectedWithAnExplicitReason()
    {
        var plugin = new InsertPluginInfo
        {
            Format = "VST3",
            PluginId = "vst3:ChowPhaserStereo",
            Name = "ChowPhaserStereo",
            Locator = @"C:\Program Files (x86)\Common Files\VST3\ChowPhaserStereo.vst3\Contents\x86-win\ChowPhaserStereo.vst3",
        };

        var insert = new Vst3Backend().Create(
            plugin, new InsertSettings { SampleRate = 44100, MaxBlockSize = 1024 }, out var reason);

        Assert.Null(insert);
        Assert.Contains("32-bit VST3", reason ?? string.Empty);
    }

    [Fact]
    public void ScanPicksUpJsfxTextFilesWithoutExecutingThem()
    {
        WithSettings(null, bridgeAvailable: false, settings =>
        {
            string folder = Environment.GetEnvironmentVariable("TUNELAB_DATA_DIR")!;
            string jsfxFolder = Path.Combine(folder, "Effects");
            Directory.CreateDirectory(jsfxFolder);

            File.WriteAllText(Path.Combine(jsfxFolder, "foo.jsfx"), """
                desc: Scan Test
                slider1:0<0,1,0.01>Gain

                @init
                gain = slider1;

                @sample
                spl0 = spl0 * gain;
                """);

            settings.SetPaths("VST3", []);
            settings.SetPaths("VST2", []);
            settings.SetPaths("CLAP", []);
            settings.SetPaths("JSFX", [jsfxFolder]);

            var scanned = settings.Scan();
            var jsfx = Assert.Single(scanned, plugin => plugin.Format == "JSFX");

            Assert.Equal("Scan Test", jsfx.Name);
            Assert.Equal("jsfx:foo.jsfx", jsfx.PluginId);
            Assert.True(jsfx.IsAvailable);
        });
    }

    // JSFX 探不过的不进清单（也进不了缓存 / 菜单）—— 见 JsfxBackend 的扫描探针
    // 与 InsertPluginSettings.AddUsableJsfx。其它格式的"不可用"仍然保留
    // （菜单灰掉并带原因，用户能据此发现自己放错了插件），所以这里只断言 JSFX 的取舍。
    [Fact]
    public void UnusableJsfxEntriesAreLeftOutOfTheCatalog()
    {
        var jsfx = new List<InsertPluginEntry>
        {
            new()
            {
                Format = "JSFX",
                PluginId = "jsfx:good.jsfx",
                Name = "Good",
                Locator = "good.jsfx",
            },
            new()
            {
                Format = "JSFX",
                PluginId = "jsfx:mod.jsfx",
                Name = "Mod",
                Locator = "mod.jsfx",
                IsAvailable = false,
                UnavailableReason = "JSFX inserts require 2 inputs and 2 outputs (this one declares 0/0)",
            },
        };

        var entries = new List<InsertPluginEntry>();

        InsertPluginSettings.AddUsableJsfx(entries, jsfx);

        var kept = Assert.Single(entries);
        Assert.Equal("jsfx:good.jsfx", kept.PluginId);
    }

    static void WithCache(string json, Action<InsertPluginSettings> assert)
    {
        WithSettings(json, bridgeAvailable: false, assert);
    }

    // 设置层的读写：把"本机能不能跑隔离宿主"的探针换掉，才可能脱离这台机器断言。
    // 探针是进程级静态，和 TUNELAB_DATA_DIR 一样用 sLock 串行化。
    static void WithSettings(string? json, bool bridgeAvailable, Action<InsertPluginSettings> assert)
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

