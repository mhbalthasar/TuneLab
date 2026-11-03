using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TuneLab.ExperimentalPatch.InsertHost;
using Xunit;

namespace TuneLab.Tests;

// 真 .clap 的端到端**播放**链路（P-C2 的验收）：宿主 → 桥 → 插件 C ABI → 共享环 → 宿主。
//
// 这里不验"某个插件的声音对不对"（那需要逐插件的黄金样本），只验**链路**：
// 实例建起来了、process 有有限输出、参数列得出来、状态能存能读、卸得干净。
// 与 ClapIntegrationTests（只扫）分开：那组只要装了插件，这组还要桥真能加载。
[Collection("IsolatedBridge")]
public class ClapInsertIntegrationTests
{
    const string ClapDirVariable = "TL_CLAP_DIR";

    static readonly string[] FallbackDirectories =
    [
        @"Y:\Insts\VSTEffects\CLAP",
        @"C:\Program Files\Common Files\CLAP",
        "/usr/lib/clap",
    ];

    static IEnumerable<string> ClapDirectories()
        => new[] { Environment.GetEnvironmentVariable(ClapDirVariable) }
            .Concat(FallbackDirectories)
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    static ClapInsertIntegrationTests()
    {
        if (VstBridgeClient.BridgeExecutableExists)
            return;

        string baseDirectory = AppContext.BaseDirectory;
        string configuration = new DirectoryInfo(baseDirectory.TrimEnd('\\', '/')).Parent?.Name ?? "Debug";

        for (DirectoryInfo? current = new(baseDirectory); current != null; current = current.Parent)
        {
            string candidate = Path.Combine(current.FullName, "TuneLab", "bin", configuration, "net8.0", "bridge");

            if (Directory.Exists(candidate))
            {
                Environment.SetEnvironmentVariable("TUNELAB_BRIDGE_DIR", candidate);
                return;
            }
        }
    }

    static ClapBackend BackendOrSkip()
    {
        var backend = new ClapBackend();

        Skip.Unless(ClapDirectories().Any(), "no CLAP plugin folder on this machine");
        Skip.If(!backend.IsAvailable, $"the isolated host is missing: {backend.UnavailableReason}");

        return backend;
    }

    // 找一个"效果器"条目。优先 Surge XT Effects（DPF，行为规矩、参数多），
    // 没装就用清单里第一个非乐器条目。
    static InsertPluginInfo? FindEffect(ClapBackend backend)
    {
        var entries = backend.ListPlugins(ClapDirectories().ToList())
            .Where(e => e.IsAvailable && !e.IsInstrument)
            .ToList();

        return entries.FirstOrDefault(e => e.PluginId == "clap:org.surge-synth-team.surge-xt-fx")
            ?? entries.FirstOrDefault();
    }

    static InsertSettings Settings() => new()
    {
        SampleRate = 48000,
        MaxBlockSize = 512,
    };

    [Fact]
    public void Insert_LoadsARealPluginThroughTheBridge()
    {
        var backend = BackendOrSkip();
        var plugin = FindEffect(backend);

        Skip.If(plugin == null, "no loadable CLAP effect on this machine");

        using var insert = ClapBridgeInsert.Create(plugin!, Settings(), out string? reason, out _);

        Assert.Null(reason);
        Assert.NotNull(insert);
    }

    [Fact]
    public void Insert_ProcessesAudioWithFiniteOutput()
    {
        var backend = BackendOrSkip();
        var plugin = FindEffect(backend);

        Skip.If(plugin == null, "no loadable CLAP effect on this machine");

        var insert = ClapBridgeInsert.Create(plugin!, Settings(), out string? reason, out _);

        Assert.NotNull(insert);
        Assert.Null(reason);

        using var disposable = insert as IDisposable;

        // 一秒不到的 440 Hz 正弦，分两块过（第二块故意短于第一块：块的尾巴上会出现
        // 小于 32 帧的块，activate 时 min_frames_count 必须容得下）。
        var buffer = new float[512 * 2];

        for (int block = 0; block < 3; block++)
        {
            int frames = block == 2 ? 64 : 512;

            for (int i = 0; i < frames; i++)
            {
                float sample = 0.1f * MathF.Sin(2 * MathF.PI * 440f * (block * 512 + i) / 48000f);
                buffer[i * 2] = sample;
                buffer[i * 2 + 1] = sample;
            }

            insert.Process(buffer.AsSpan(), 0, frames);

            for (int i = 0; i < frames * 2; i++)
                Assert.True(float.IsFinite(buffer[i]), $"sample {i} of block {block} is not finite");
        }
    }

    [Fact]
    public void Insert_ListsParametersAndReadsValues()
    {
        var backend = BackendOrSkip();
        var plugin = FindEffect(backend);

        Skip.If(plugin == null, "no loadable CLAP effect on this machine");

        var insert = ClapBridgeInsert.Create(plugin!, Settings(), out _, out _);

        Assert.NotNull(insert);

        using var disposable = insert as IDisposable;

        var parameters = insert!.Parameters;

        Assert.NotEmpty(parameters);

        Assert.All(parameters, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.True(p.MaxValue >= p.MinValue);
            Assert.InRange(p.CurrentValue, p.MinValue, p.MaxValue);
        });

        // 改一个参数再读回来：桥那边是"入队等下一次 process"，所以读回的是**旧值** ——
        // 这里只钉"改不炸、id 找得到"，冲队列由 FlushParameterChanges 负责。
        var first = parameters[0];
        insert.SetParameter(first.Id, (first.MinValue + first.MaxValue) / 2);
        Assert.InRange(insert.GetParameter(first.Id), first.MinValue, first.MaxValue);
    }

    [Fact]
    public void Insert_SavesAndLoadsState()
    {
        var backend = BackendOrSkip();
        var plugin = FindEffect(backend);

        Skip.If(plugin == null, "no loadable CLAP effect on this machine");

        var insert = ClapBridgeInsert.Create(plugin!, Settings(), out _, out _);

        Assert.NotNull(insert);

        using var disposable = insert as IDisposable;

        byte[]? state = insert!.SaveState();

        // 有些极简插件不实现 clap.state：null 是合法的"没有状态"。
        if (state == null)
            return;

        Assert.NotEmpty(state);
        Assert.True(insert.LoadState(state));
    }

    [Fact]
    public void Instrument_IsRejectedAsAnInsert()
    {
        var backend = BackendOrSkip();

        var instrument = backend.ListPlugins(ClapDirectories().ToList())
            .FirstOrDefault(e => e.IsAvailable && e.IsInstrument);

        Skip.If(instrument == null, "no CLAP instrument on this machine");

        var insert = backend.Create(instrument!, Settings(), out string? reason);

        Assert.Null(insert);
        Assert.Contains("instrument", reason);
    }

    // P-C3 的验收：真插件在**桥进程里**开窗，宿主拿到跨进程句柄，关得掉。
    //
    // 只验链路（开 / 句柄 / 关），不验像素。没有 win32 界面的插件（或非 Windows）
    // 直接返回 —— 那不是失败：界面会自动退到参数面板。
    [Fact]
    public void Editor_OpensInTheBridgeAndCloses()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var backend = BackendOrSkip();
        var plugin = FindEffect(backend);

        Skip.If(plugin == null, "no loadable CLAP effect on this machine");

        var insert = ClapBridgeInsert.Create(plugin!, Settings(), out string? reason, out _);

        Assert.NotNull(insert);
        Assert.Null(reason);

        using var disposable = insert as IDisposable;

        if (!insert!.HasEditor)
            return;

        Assert.True(insert.OpenEditor(IntPtr.Zero));
        Assert.True(insert.IsEditorOpen);

        IntPtr window = insert.FindEditorWindowSince([]);

        Assert.NotEqual(IntPtr.Zero, window);

        insert.CloseEditor();
        Assert.False(insert.IsEditorOpen);
    }
}
