using System;
using System.Collections.Generic;
using System.IO;
using TuneLab.ExperimentalPatch.InsertHost;
using Xunit;

namespace TuneLab.Tests;

// VST2 后端里**不需要真插件**的那一半：位数判定 + 清单过滤。
//
// 为什么先把这块单独测：它决定了"该起哪个桥"。判错的后果不是"报错"，
// 而是把一个 32 位 dll 递给 64 位桥（或反过来），得到 ERROR_BAD_EXE_FORMAT
// —— 一条和真正原因（位数）看不出关系的错误。
//
// 注意：不能拿托管程序集当样本。AnyCPU 的 PE 头里 Machine 仍然是 I386(0x14C)，
// 它和"这个 dll 是不是 32 位原生插件"不是一回事。所以这里自己造最小的 PE 头。
public class Vst2BackendTests
{
    [Theory]
    [InlineData(0x8664, 64)]   // AMD64
    [InlineData(0x014C, 32)]   // I386
    [InlineData(0xAA64, 64)]   // ARM64（同样是 64 位，x64 桥装不了）
    public void TheMachineFieldDecidesTheBitness(int machine, int expected)
    {
        string path = WriteSyntheticPe(machine);

        try
        {
            Assert.Equal(expected, PluginBitness.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileWithoutAPeSignatureIsRejected()
    {
        string path = Path.Combine(Path.GetTempPath(), "tunelab-not-pe-" + Guid.NewGuid().ToString("N") + ".dll");

        try
        {
            var bytes = new byte[0x100];
            BitConverter.GetBytes(0x40).CopyTo(bytes, 0x3C);
            // 0x40 处不写 'PE\0\0'，写一段垃圾。
            bytes[0x40] = 0x11;
            File.WriteAllBytes(path, bytes);

            Assert.Equal(0, PluginBitness.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ATextFileIsNotAWindowsDll()
    {
        string path = Path.Combine(Path.GetTempPath(), "tunelab-not-a-dll-" + Guid.NewGuid().ToString("N") + ".dll");

        try
        {
            File.WriteAllText(path, "this is not a PE file");
            Assert.Equal(0, PluginBitness.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFileIsNotAWindowsDll()
    {
        string path = Path.Combine(Path.GetTempPath(), "tunelab-missing-" + Guid.NewGuid().ToString("N") + ".dll");

        Assert.Equal(0, PluginBitness.Read(path));
    }

    [Fact]
    public void ScanningListsWindowsDllsWithTheirBitness()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tunelab-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            // 一个假 dll（文本）+ 一个 x64 插件 dll + 一个 x86 插件 dll。
            File.WriteAllText(Path.Combine(directory, "fake.dll"), "not a dll");
            File.WriteAllBytes(Path.Combine(directory, "sixtyfour.dll"), BuildPe(0x8664));
            File.WriteAllBytes(Path.Combine(directory, "thirtytwo.dll"), BuildPe(0x014C));

            var backend = new Vst2Backend();
            var plugins = backend.ListPlugins([directory]);

            // 顺序按文件系统枚举，别依赖它：按名字查。
            var byName = new Dictionary<string, InsertPluginInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var plugin in plugins)
                byName[plugin.Name] = plugin;

            Assert.Equal(2, plugins.Count);
            Assert.Equal("64-bit", byName["sixtyfour"].Category);
            Assert.Equal("32-bit", byName["thirtytwo"].Category);
            Assert.Equal("vst2:sixtyfour", byName["sixtyfour"].PluginId);
            Assert.EndsWith("sixtyfour.dll", byName["sixtyfour"].Locator, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScanningWithoutDirectoriesReturnsNothing()
    {
        var backend = new Vst2Backend();

        Assert.Empty(backend.ListPlugins(null));
        Assert.Empty(backend.ListPlugins([]));
    }

    // ── 最小 PE ──

    static string WriteSyntheticPe(int machine)
    {
        string path = Path.Combine(Path.GetTempPath(), "tunelab-pe-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(path, BuildPe(machine));
        return path;
    }

    // DOS 头里的 e_lfanew(0x3C) 指向 PE 头：'P','E',0,0 之后 2 字节是 Machine。
    // 真正校验位数只需要这三个字段，别的都填 0。
    static byte[] BuildPe(int machine)
    {
        var bytes = new byte[0x100];

        BitConverter.GetBytes(0x40).CopyTo(bytes, 0x3C);

        bytes[0x40] = (byte)'P';
        bytes[0x41] = (byte)'E';
        bytes[0x42] = 0;
        bytes[0x43] = 0;

        BitConverter.GetBytes((ushort)machine).CopyTo(bytes, 0x44);

        return bytes;
    }
}
