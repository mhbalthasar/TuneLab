using System;
using System.IO;
using System.Text;
using TuneLab.ExperimentalPatch.InsertHost;
using Xunit;

namespace TuneLab.Tests;

// VST3 "是不是乐器"的**静态**判定（不加载插件）。
//
// 【为什么要单独钉住】这条判定决定「添加效果」菜单里出现哪些插件，而它**绝不能**用
// "加载插件问一句"来实现：实测扫描时逐个 LoadPlugin → Initialize 会把整个宿主进程带走
// （某个 DPF 插件的 assert 直接 abort），也有插件 20 秒不返回。所以它只看二进制里的类别标记，
// 而标记的读取有几个容易错的细节：跨块、UTF-16、以及"宁可漏判不可误判"的组合规则。
public class Vst3ClassifierTests
{
    [Fact]
    public void TheCategoryMarkerTellsEffectsFromInstruments()
    {
        WithTempBinary("junk\0Instrument|Synth\0junk", path =>
            Assert.True(Vst3Backend.LooksLikeInstrument(path)));

        WithTempBinary("junk\0Fx|Delay\0junk", path =>
            Assert.False(Vst3Backend.LooksLikeInstrument(path)));
    }

    [Fact]
    public void AMarkerSplitAcrossTheReadChunkIsStillFound()
    {
        // 标记正好骑在 1 MB 的读块边界上。没有"留尾巴"的实现会在这里漏掉。
        byte[] prefix = new byte[(1 << 20) - 4];

        WithTempBinary(prefix, "Instrument|Synth", path =>
            Assert.True(Vst3Backend.LooksLikeInstrument(path)));
    }

    [Fact]
    public void WideStringsAreRecognisedToo()
    {
        // 有些插件的子类别是 UTF-16 字面量（L"Instrument|Synth"）。
        WithTempBinary(Encoding.Unicode.GetBytes("x\0Instrument|Synth\0x"), path =>
            Assert.True(Vst3Backend.LooksLikeInstrument(path)));
    }

    [Fact]
    public void APluginWithBothMarkersIsTreatedAsAnEffect()
    {
        // 一个 bundle 里既有乐器类又有效果类（宿主型插件很常见）。
        // 判成乐器的代价是"用户以为插件没扫到"，比漏判一个合成器糟 —— 所以这种一律照效果器列出来。
        WithTempBinary("Instrument|Synth\0Fx|Delay", path =>
            Assert.False(Vst3Backend.LooksLikeInstrument(path)));
    }

    [Fact]
    public void AnUnreadableFileIsNotAnInstrument()
    {
        // 读不动（占用 / 权限）→ "不知道" → 列出来，而不是悄悄消失。
        string missing = Path.Combine(Path.GetTempPath(), "tunelab-missing-" + Guid.NewGuid().ToString("N") + ".vst3");

        Assert.False(Vst3Backend.LooksLikeInstrument(missing));
    }

    static void WithTempBinary(string content, Action<string> assert)
        => WithTempBinary(Encoding.UTF8.GetBytes(content), assert);

    static void WithTempBinary(byte[] head, string tail, Action<string> assert)
    {
        byte[] bytes = new byte[head.Length + tail.Length];
        head.CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes(tail).CopyTo(bytes, head.Length);

        WithTempBinary(bytes, assert);
    }

    static void WithTempBinary(byte[] bytes, Action<string> assert)
    {
        string directory = Path.Combine(Path.GetTempPath(), "tunelab-vst3-marker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, "Plugin.vst3");

        try
        {
            File.WriteAllBytes(path, bytes);
            assert(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
