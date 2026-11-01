using System;
using System.Collections.Generic;
using System.Linq;
using TuneLab.ExperimentalPatch.InsertHost;
using Xunit;

namespace TuneLab.Tests;

// 完整扫描（ScanDeep）的 VST3 合并规则。
//
// 【为什么】完整扫描的 VST3 以前整份取自 native 扫描器，而那个扫描器只认标准 VST3 目录。
// 用户配了自定义目录（EP-MIXER-12 的 Vst3Paths）时，一次完整扫描就会把那些插件从菜单里
// 整片抹掉 —— 真机表现："VST2 里还有，VST3 里它消失了"。这里钉住合并：
// 配置目录那份必须活下来，和 native 重复的（同一个 bundle）只留 native 那条。
public class InsertPluginScanMergeTests
{
    static InsertPluginInfo Vst3(string name, string locator, string vendor = "")
        => new()
        {
            Format = "VST3",
            PluginId = "vst3:" + name,
            Name = name,
            Vendor = vendor,
            Locator = locator,
        };

    [Fact]
    public void PluginsFromConfiguredDirectoriesSurviveADeepScan()
    {
        var fromScanner = new[]
        {
            Vst3("BYOD", @"C:\Program Files\Common Files\VST3\BYOD.vst3", "chowdsp"),
        };

        var fromDirectories = new[]
        {
            // 同一个 bundle：枚举给出的是包内二进制，与 native 的包目录路径不同形。
            Vst3("BYOD", @"C:\Program Files\Common Files\VST3\BYOD.vst3\Contents\x86_64-win\BYOD.vst3", "chowdsp"),

            // 只在自定义目录里有的插件 —— 这条必须留下，否则就是那个"插件消失"的 bug。
            Vst3("RoomReverb",
                @"Y:\Insts\VSTEffects\VST3\04_Reverb\RoomReverb.vst3\Contents\x86_64-win\RoomReverb.vst3",
                "ElephantDSP.com"),
        };

        var merged = InsertPluginSettings.MergeVst3Scan(fromScanner, fromDirectories);

        Assert.Equal(2, merged.Count);
        Assert.Single(merged, p => p.Name == "RoomReverb");

        // 重复的那个留 native 的：它问过 factory，厂商/类别更准。
        var byod = Assert.Single(merged, p => p.Name == "BYOD");
        Assert.Equal(@"C:\Program Files\Common Files\VST3\BYOD.vst3", byod.Locator);
    }

    [Fact]
    public void TheMergeIsCaseInsensitiveAndToleratesSlashes()
    {
        var fromScanner = new[]
        {
            Vst3("ChowCentaur", @"C:\Program Files\Common Files\VST3\ChowCentaur.vst3"),
        };

        var fromDirectories = new[]
        {
            Vst3("ChowCentaur", @"c:/program files/common files/vst3/CHOWCENTAUR.VST3/Contents/x86_64-win/ChowCentaur.vst3"),
        };

        Assert.Single(InsertPluginSettings.MergeVst3Scan(fromScanner, fromDirectories));
    }

    [Fact]
    public void SingleFileBundlesAreComparedByTheirPath()
    {
        var fromScanner = new[]
        {
            Vst3("SPAN", @"C:\Program Files\Common Files\VST3\SPAN.vst3"),
        };

        var fromDirectories = new[]
        {
            Vst3("SPAN", @"C:\Program Files\Common Files\VST3\SPAN.vst3"),
            Vst3("MSED", @"C:\Program Files\Common Files\VST3\MSED.vst3"),
        };

        var merged = InsertPluginSettings.MergeVst3Scan(fromScanner, fromDirectories);

        Assert.Equal(2, merged.Count);
        Assert.Single(merged, p => p.Name == "SPAN");
        Assert.Single(merged, p => p.Name == "MSED");
    }
}
