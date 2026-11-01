using System;
using System.IO;
using TuneLab.ExperimentalPatch.InsertHost;
using Xunit;

namespace TuneLab.Tests;

// 读 PE 导出表判断"这个 dll 到底是不是 VST2"。
//
// 真机上的形状：`04_Reverb\RoomReverb.dll` 只导出 lv2_descriptor / lv2ui_descriptor
// （是 LV2 的二进制被放进了 VST2 目录），却以"可用"的姿态出现在 VST2 菜单里，
// 点进去才报 "the file is not a VST2 plugin (no VSTPluginMain export)"。
// 这一组钉住静态判定本身；扫描层怎么用它见 Vst2BackendTests。
public class PeExportsTests
{
    [Fact]
    public void AnExportNamedVstPluginMainCounts()
    {
        string path = TempFile(PeFixtures.Build(exportName: "VSTPluginMain"));

        try
        {
            Assert.True(PeExports.HasAny(path, "VSTPluginMain", "main"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // 老 SDK 的入口叫 main。只认 VSTPluginMain 会把一批老插件误标成不可用。
    [Fact]
    public void TheLegacyMainExportCountsToo()
    {
        string path = TempFile(PeFixtures.Build(exportName: "main"));

        try
        {
            Assert.True(PeExports.HasAny(path, "VSTPluginMain", "main"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnotherFormatsEntryPointDoesNotCount()
    {
        string path = TempFile(PeFixtures.Build(exportName: "lv2_descriptor"));

        try
        {
            Assert.False(PeExports.HasAny(path, "VSTPluginMain", "main"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void APeWithoutAnExportTableIsNotAVst2()
    {
        string path = TempFile(PeFixtures.Build(exportName: null));

        try
        {
            Assert.False(PeExports.HasAny(path, "VSTPluginMain", "main"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // 读不出来 = 不知道。调用方据此"照旧当可用"，不能把能用的插件藏起来。
    [Fact]
    public void AnUnreadableFileIsUnknownRatherThanUnavailable()
    {
        string text = Path.Combine(Path.GetTempPath(), "tunelab-pe-text-" + Guid.NewGuid().ToString("N") + ".dll");
        string missing = Path.Combine(Path.GetTempPath(), "tunelab-pe-missing-" + Guid.NewGuid().ToString("N") + ".dll");

        File.WriteAllText(text, "this is not a PE file");

        try
        {
            Assert.Null(PeExports.HasAny(text, "VSTPluginMain"));
            Assert.Null(PeExports.HasAny(missing, "VSTPluginMain"));
        }
        finally
        {
            File.Delete(text);
        }
    }

    static string TempFile(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "tunelab-pe-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
