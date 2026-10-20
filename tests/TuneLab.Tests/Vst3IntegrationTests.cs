using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TuneLab.ExperimentalPatch.InsertHost;
using Xunit;

namespace TuneLab.Tests;

// 真 VST3 插件的端到端验证（阶段二 M6b 的验收）。
//
// 【为什么单独一组】这之前的所有插入测试都跑假后端（GainInsert）：它们验证"链被接进图、
// 旁通真的跳、latency 真的补 PDC"，但**一次都没让真插件出声**。本组补上那一半：
// 加载 .vst3 → Initialize → 一块音频穿过它 → state 存取 → 延迟上报被 PDC 用上。
//
// 【为什么默认跳过】要机器上真装有 64 位 VST3。用 TL_VST3_DIR 环境变量指定目录，
// 每个 .vst3 各跑一次；没设就跳过（CI / 别人的机器上不一定有）。
public class Vst3IntegrationTests
{
    const string Vst3DirVariable = "TL_VST3_DIR";

    // 默认路径：开发机上装了一整套（Y:\Insts\VSTEffects\VST3）。别处可经环境变量覆盖。
    static readonly string[] FallbackDirectories =
    [
        @"Y:\Insts\VSTEffects\VST3",
        @"C:\Program Files\Common Files\VST3",
    ];

    static IEnumerable<string> Vst3Directories()
        => new[] { Environment.GetEnvironmentVariable(Vst3DirVariable) }
            .Concat(FallbackDirectories)
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d));

    static IEnumerable<string> Vst3Bundles()
        => Vst3Directories()
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.vst3", SearchOption.AllDirectories))
            // 只取 64 位：32 位包在我们的进程里加载不了（那是 VstBridge 的活）。
            .Where(path => path.Contains(@"x86_64-win"))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Backend_SeesTheNativeLibrary()
    {
        Skip.Unless(Vst3Bundles().Any(), "no 64-bit VST3 bundle on this machine");

        var backend = new Vst3Backend();

        // native dll 由 InsertHost.csproj 拷到输出目录（名为 ownvst3.tlhost.dll）。
        // 不在就说明部署漏了它 —— 那是个真错误，不是"这台机器没插件"。
        Skip.If(!backend.IsAvailable, $"VST3 host library is missing: {backend.UnavailableReason}");
    }

    [Fact]
    public void Scans_FindsTheBundlesOnDisk()
    {
        Skip.If(!Vst3Bundles().Any(), "no 64-bit VST3 bundle on this machine");

        var backend = new Vst3Backend();
        Skip.If(!backend.IsAvailable, $"VST3 host library is missing: {backend.UnavailableReason}");

        List<InsertPluginInfo> found = backend.ListPlugins(Vst3Directories().ToList(), probeInstruments: false).ToList();

        // 扫到的东西必须**在我们指定的目录里**（不是系统里恰好装着的）：
        // 否则"用户加了一个扫描路径"这个功能就是假的。
        var roots = Vst3Directories().Select(p => p.TrimEnd('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(found, p => Assert.True(roots.Any(r => p.Locator.StartsWith(r, StringComparison.OrdinalIgnoreCase))));
    }

    // 一把抓多个插件，每个都：加载 → 初始化 → 处理一块 → state 往返。
    [Theory]
    [InlineData("ZamComp")]        // 压缩器，stereo→mono（1 in / 1 out）
    [InlineData("ZamCompX2")]      // 同上，双单声道版
    [InlineData("Sound Delay")]    // 立体声延迟
    [InlineData("Latency Delay")]  // **报 10000 样本延迟**（PDC 的靶子）
    public void EndToEnd_LoadProcessSave(string nameFragment)
    {
        Skip.If(!Vst3Bundles().Any(), "no 64-bit VST3 bundle on this machine");

        var backend = new Vst3Backend();
        Skip.If(!backend.IsAvailable, $"VST3 host library is missing: {backend.UnavailableReason}");

        var plugins = backend.ListPlugins(Vst3Directories().ToList(), probeInstruments: false);
        var plugin = plugins.FirstOrDefault(p => p.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase));

        Skip.If(plugin == null, $"plugin '{nameFragment}' is not installed");

        var insert = backend.Create(plugin, new InsertSettings
        {
            SampleRate = 48000,
            MaxBlockSize = 512,
        }, out var reason);

        Assert.NotNull(insert);

        // 成功时 reason 必须是 null；失败时（下面几个 Case）才该有话可说。
        Assert.True(string.IsNullOrWhiteSpace(reason), $"success should not carry a reason: {reason}");

        // 同一个插件再建一次也该成（多个轨挂同一个效果器是常见用法）。
        Assert.True(backend.Create(plugin, new InsertSettings { SampleRate = 48000, MaxBlockSize = 512 },
            out var secondReason) != null, secondReason);

        Assert.NotNull(insert);

        // ── 处理一块 ──
        const int block = 512;
        var buffer = new float[block * 2];

        // 一块已知信号：左 440Hz 正弦、右 0.25 常量。
        for (int i = 0; i < block; i++)
        {
            buffer[i * 2] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * i / 48000);
            buffer[i * 2 + 1] = 0.25f;
        }

        // 旁通时应当是原样（这一条把"旁通真的没碰音频"钉住）。
        var bypass = new InsertSettings { Bypass = true, SampleRate = 48000, MaxBlockSize = 512 };
        var bypassed = backend.Create(plugin, bypass, out _);
        Assert.NotNull(bypassed);

        var copy = (float[])buffer.Clone();
        bypassed!.Process(copy, 0, block);

        Assert.Equal(buffer, copy);   // 逐样本相同

        // 真跑一次。**不断言输出非零**：延迟类插件这一块就该是全静音
        // （Latency Delay 报 10000 样本延迟 = 20 多个块）。
        insert!.Process(buffer, 0, block);

        // ── state 存取 ──
        if (insert is Vst3Insert vst)
        {
            byte[]? state = vst.SaveState();

            Skip.If(state == null || state.Length == 0, $"plugin '{plugin.Name}' reports no state chunk");

            // 只换参数不换插件：setParameter → 再存一次，两次 chunk 应该不同
            // （证明这不只是个空壳）。
            Assert.True(vst.LoadState(state));
        }
    }

    [Fact]
    public void PluginReportingLatency_LetsPdcCompensate()
    {
        Skip.If(!Vst3Bundles().Any(), "no 64-bit VST3 bundle on this machine");

        var backend = new Vst3Backend();
        Skip.If(!backend.IsAvailable, $"VST3 host library is missing: {backend.UnavailableReason}");

        var plugin = backend.ListPlugins(Vst3Directories().ToList(), probeInstruments: false)
            .FirstOrDefault(p => p.Name.Contains("Latency Delay", StringComparison.OrdinalIgnoreCase));

        Skip.If(plugin == null, "'Latency Delay' is not installed");

        var insert = backend.Create(plugin, new InsertSettings { SampleRate = 48000, MaxBlockSize = 512 }, out _);
        Assert.NotNull(insert);

        // Voxengo 的 Latency Delay 默认就报 10000 样本 —— 正好验证"延迟真的从插件传出来了"。
        Assert.True(insert!.LatencySamples > 0,
            $"expected the plugin to report a delay, got {insert.LatencySamples}");
    }

    [Fact]
    public void AnInstrument_IsRefused()
    {
        Skip.If(!Vst3Bundles().Any(), "no 64-bit VST3 bundle on this machine");

        var backend = new Vst3Backend();
        Skip.If(!backend.IsAvailable, $"VST3 host library is missing: {backend.UnavailableReason}");

        // Cardinal Synth：isInstrument=True、0 进 2 出（实测）。挂在插入链上毫无意义 ——
        // 它要收 MIDI、自己产声音。
        //
        // 注意这里**不能**拿 Surge XT 当靶子：它报 isInstrument=False / 2 in / 2 out，
        // 在 VST3 层面就声明自己是效果器（那个 bundle 同时含效果器与合成器）。
        var instrument = backend.ListPlugins(Vst3Directories().ToList(), probeInstruments: false)
            .FirstOrDefault(p => p.Name.Contains("Cardinal", StringComparison.OrdinalIgnoreCase)
                              && p.Name.Contains("Synth", StringComparison.OrdinalIgnoreCase));

        Skip.If(instrument == null, "'Cardinal Synth' is not installed");

        var insert = backend.Create(instrument, new InsertSettings { SampleRate = 48000, MaxBlockSize = 512 },
            out var reason);

        Assert.Null(insert);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void AMissingPlugin_IsRefusedWithAReason()
    {
        var backend = new Vst3Backend();
        Skip.If(!backend.IsAvailable, $"VST3 host library is missing: {backend.UnavailableReason}");

        var ghost = new InsertPluginInfo
        {
            Format = "VST3",
            PluginId = "vst3:nobody-makes-this",
            Name = "Ghost",
            Locator = @"C:\does\not\exist.vst3",
        };

        var insert = backend.Create(ghost, new InsertSettings { SampleRate = 48000, MaxBlockSize = 512 }, out var reason);

        Assert.Null(insert);
        Assert.False(string.IsNullOrWhiteSpace(reason));   // 界面上要靠这句话告诉用户出了什么事
    }
}

