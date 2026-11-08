using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.MixerEngine;
using Xunit;

namespace TuneLab.Tests;

// 真 VST3 链的自激 / 反馈排查。
//
// 用户报告：Main Std 轨上第一个 VST 插入不旁通时出现"啸叫"；两个以上插件时
// 与插件无关，只与"是不是链上第一个"有关。这里用真桥 + 真插件复现：
// 前 1 秒给正弦，之后给静音，看链的输出在输入停止后是否还持续有能量。
[Collection("IsolatedBridge")]
public class Vst3ChainFeedbackTests
{
    sealed class TestState : IMixChannelState
    {
        public double Volume { get; set; } = 1;
        public double Pan { get; set; }
        public bool IsSilent { get; set; }
    }

    // 前 activeFrames 帧是 440 Hz 正弦，之后是静音。
    sealed class BurstSource(int sampleRate, int activeFrames) : IMixSource
    {
        public void AddData(int position, int count, float[] buffer, int offset, float leftGain, float rightGain)
        {
            for (int i = 0; i < count; i++)
            {
                int p = position + i;
                if (p >= activeFrames)
                    continue;

                float v = 0.25f * (float)Math.Sin(2 * Math.PI * 440 * p / sampleRate);
                buffer[offset + i * 2] += leftGain * v;
                buffer[offset + i * 2 + 1] += rightGain * v;
            }
        }
    }

    static Vst3ChainFeedbackTests()
    {
        if (VstBridgeClient.BridgeExecutableExists)
            return;

        string baseDirectory = AppContext.BaseDirectory;
        string configuration = new DirectoryInfo(baseDirectory).Parent?.Name ?? "Debug";
        string repo = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", ".."));
        string candidate = Path.Combine(repo, "TuneLab", "bin", configuration, "net8.0", "bridge");

        if (Directory.Exists(candidate))
            Environment.SetEnvironmentVariable("TUNELAB_BRIDGE_DIR", candidate);
    }

    static InsertPluginInfo? FindTubeAmp(Vst3Backend backend)
    {
        var dirs = new[] { @"Y:\Insts\VSTEffects\VST3", @"C:\Program Files\Common Files\VST3" }
            .Where(Directory.Exists)
            .ToList();

        return backend.ListPlugins(dirs, probeInstruments: false)
            .FirstOrDefault(p => p.Name.Equals("Tube Amp", StringComparison.OrdinalIgnoreCase));
    }

    static (double[] Rms, IMixInsert First, IMixInsert Second) RenderChain(
        IMixInsert first, IMixInsert second, int blocks, int blockSize, int sampleRate)
    {
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new TestState());
        var track = builder.AddNode("track-0", MixNodeKind.Track, new TestState(),
            new BurstSource(sampleRate, sampleRate), [first, second]);
        builder.Connect(track, master, 1f);

        var mixer = new Mixer();
        mixer.SetSnapshot(builder.Build());

        var buffer = new float[blockSize * 2];
        var rms = new double[blocks];

        for (int b = 0; b < blocks; b++)
        {
            Array.Clear(buffer);
            mixer.Render(b * blockSize, blockSize, buffer, 0);

            double sum = 0;
            for (int i = 0; i < buffer.Length; i++)
                sum += buffer[i] * buffer[i];

            rms[b] = Math.Sqrt(sum / buffer.Length);
        }

        return (rms, first, second);
    }

    [Fact]
    public void TwoActiveRealVst3Inserts_DoNotSelfOscillate()
    {
        var backend = new Vst3Backend();
        Skip.If(!backend.IsAvailable, $"the isolated host is missing: {backend.UnavailableReason}");

        var plugin = FindTubeAmp(backend);
        Skip.If(plugin == null, "'Tube Amp' is not installed");

        const int sampleRate = 44100;
        const int blockSize = 512;
        const int blocks = 400;   // ~4.6 s；输入只在前 1 s

        var first = backend.Create(plugin!, new InsertSettings { SampleRate = sampleRate, MaxBlockSize = blockSize }, out var r1);
        var second = backend.Create(plugin!, new InsertSettings { SampleRate = sampleRate, MaxBlockSize = blockSize }, out var r2);

        Assert.True(first != null, r1);
        Assert.True(second != null, r2);

        var (rms, _, _) = RenderChain(first!, second!, blocks, blockSize, sampleRate);

        // 输入在 block 86 左右结束；从 block 120 开始应当只剩插件尾巴（如果有）。
        double tailMax = rms.Skip(120).Max();
        double tailAvg = rms.Skip(120).Average();

        Assert.True(tailMax < 0.01, $"chain self-oscillates: tail max rms={tailMax:0.000000}, avg={tailAvg:0.000000}");
    }

    [Fact]
    public void FirstBypassed_SecondActive_DoesNotSelfOscillate()
    {
        var backend = new Vst3Backend();
        Skip.If(!backend.IsAvailable, $"the isolated host is missing: {backend.UnavailableReason}");

        var plugin = FindTubeAmp(backend);
        Skip.If(plugin == null, "'Tube Amp' is not installed");

        const int sampleRate = 44100;
        const int blockSize = 512;
        const int blocks = 400;

        var first = backend.Create(plugin!, new InsertSettings { SampleRate = sampleRate, MaxBlockSize = blockSize, Bypass = true }, out _);
        var second = backend.Create(plugin!, new InsertSettings { SampleRate = sampleRate, MaxBlockSize = blockSize }, out _);

        Assert.NotNull(first);
        Assert.NotNull(second);

        var (rms, _, _) = RenderChain(first!, second!, blocks, blockSize, sampleRate);
        double tailMax = rms.Skip(120).Max();
        Assert.True(tailMax < 0.01, $"bypassed-first chain self-oscillates: tail max rms={tailMax:0.000000}");
    }
}
