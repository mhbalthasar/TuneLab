using System;
using System.IO;
using System.Linq;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.ExperimentalPatch.VstBridge;
using Xunit;

namespace TuneLab.Tests;

// JSFX 端到端验证：真桥 + 真 ysfx，加载 .jsfx、跑一块音频、读参数、存取 state。
//
// 与 Vst3IntegrationTests 一样，桥不在 / 这台机器没放 ysfx 时按 SKIP 报出来，
// 不让“部署差异”变成红灯。
[Collection("IsolatedBridge")]
public class JsfxIntegrationTests
{
    static JsfxIntegrationTests()
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

    [Fact]
    public void HostInsert_LoadsProcessesAndRestoresState()
    {
        Skip.If(!VstBridgeClient.BridgeExecutableExists, "the 64-bit bridge is not published beside the tests");

        string directory = Path.Combine(Path.GetTempPath(), "tunelab-jsfx-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string file = Path.Combine(directory, "gain.jsfx");

        File.WriteAllText(file, """
            desc: TuneLab JSFX host test
            slider1:1<0,2,0.01>Gain
            slider2:0.5<0,1,0.01>Mix

            @init
            gain = slider1;

            @slider
            gain = slider1;

            @sample
            spl0 = spl0 * gain;
            spl1 = spl1 * gain;
            """);

        try
        {
            var plugin = new InsertPluginInfo
            {
                Format = JsfxBackend.FormatName,
                PluginId = "jsfx:gain.jsfx",
                Name = "TuneLab JSFX host test",
                Locator = file,
                IsAvailable = true,
            };

            var insert = new JsfxBackend().Create(plugin, new InsertSettings
            {
                SampleRate = 48000,
                MaxBlockSize = 512,
                Bypass = false,
            }, out string? reason);

            Skip.If(insert == null, "the JSFX bridge is not available: " + reason);

            using var disposable = insert as IDisposable;
            var parameters = (IParameterInsert)insert!;

            Assert.Equal(2, insert!.InputChannels);
            Assert.Contains(parameters.Parameters, p => p.Name == "Gain");

            parameters.SetParameter(0, 0.5);

            var buffer = new float[512 * 2];

            Fill(buffer);
            insert.Process(buffer, 0, 512);

            Assert.InRange(buffer[200], 0.124f, 0.126f);
            Assert.InRange(buffer[201], -0.126f, -0.124f);

            byte[]? state = parameters.SaveState();
            Assert.NotNull(state);
            Assert.NotEmpty(state!);

            parameters.SetParameter(0, 0.1);
            Fill(buffer);
            insert.Process(buffer, 0, 512);
            Assert.InRange(buffer[200], 0.024f, 0.026f);

            Assert.True(parameters.LoadState(state!));
            Assert.InRange(parameters.GetParameter(0), 0.49, 0.51);
            Fill(buffer);
            insert.Process(buffer, 0, 512);
            Assert.InRange(buffer[200], 0.124f, 0.126f);

            insert.SetBypassed(true);
            Assert.True(insert.IsBypassed);

            static void Fill(float[] buffer)
            {
                for (int i = 0; i < buffer.Length; i += 2)
                {
                    buffer[i] = 0.25f;
                    buffer[i + 1] = -0.25f;
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Bridge_RestoresSerializeData()
    {
        Skip.If(!VstBridgeClient.BridgeExecutableExists, "the 64-bit bridge is not published beside the tests");

        var client = VstBridgeClient.ForBitness(64);
        client.ClearBridgeError();

        Skip.If(!client.EnsureStartedBlocking(), "the bridge did not start: " + client.LastBridgeError);

        Skip.If(client.BridgeFormats == null
                || !client.BridgeFormats.Contains(PluginFormats.Jsfx, StringComparer.OrdinalIgnoreCase),
            "this bridge does not carry the ysfx runtime: " + client.LastBridgeError);

        string directory = Path.Combine(Path.GetTempPath(), "tunelab-jsfx-serialize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string file = Path.Combine(directory, "counter.jsfx");

        File.WriteAllText(file, """
            desc: TuneLab JSFX serialize test

            @init
            counter = 0;

            @sample
            counter += 1;
            spl0 = counter;
            spl1 = counter;

            @serialize
            file_var(0, counter);
            """);

        int slot = client.AllocateSlot();

        try
        {
            var loaded = client.LoadBlocking(slot, file, 48000, 512, false,
                PluginFormats.Jsfx, "jsfx:counter.jsfx", directory);

            Assert.NotNull(loaded);
            Assert.True(loaded!.Ok, loaded.Reason + " | " + client.LastBridgeError);

            var input = new[] { new float[64], new float[64] };
            var output = new[] { new float[64], new float[64] };

            Assert.True(client.SendProcess(slot, input, output, 2, 64, false));
            Assert.InRange(output[0][0], 0.9f, 1.1f);

            var state = client.GetStateBlocking(slot);
            Assert.NotNull(state);
            Assert.NotEmpty(state!);

            Assert.True(client.SendProcess(slot, input, output, 2, 64, false));
            Assert.InRange(output[0][0], 64.9f, 65.1f);

            Assert.True(client.SetStateBlocking(slot, state!));

            Assert.True(client.SendProcess(slot, input, output, 2, 64, false));
            Assert.InRange(output[0][0], 64.9f, 65.1f);
        }
        finally
        {
            client.UnloadBlocking(slot);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Bridge_LoadsAndProcessesJsfx()
    {
        Skip.If(!VstBridgeClient.BridgeExecutableExists, "the 64-bit bridge is not published beside the tests");

        var client = VstBridgeClient.ForBitness(64);
        client.ClearBridgeError();

        Skip.If(!client.EnsureStartedBlocking(), "the bridge did not start: " + client.LastBridgeError);

        Skip.If(client.BridgeFormats == null
                || !client.BridgeFormats.Contains(PluginFormats.Jsfx, StringComparer.OrdinalIgnoreCase),
            "this bridge does not carry the ysfx runtime: " + client.LastBridgeError);

        string directory = Path.Combine(Path.GetTempPath(), "tunelab-jsfx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string file = Path.Combine(directory, "gain.jsfx");

        File.WriteAllText(file, """
            desc: TuneLab JSFX integration test
            slider1:1<0,2,0.01>Gain
            slider2:0.5<0,1,0.01>Mix

            @init
            gain = slider1;
            custom = 1234;

            @slider
            gain = slider1;

            @sample
            spl0 = spl0 * gain;
            spl1 = spl1 * gain;

            @serialize
            file_var(0, custom);
            """);

        int slot = client.AllocateSlot();

        try
        {
            var loaded = client.LoadBlocking(slot, file, 48000, 512, false,
                PluginFormats.Jsfx, "jsfx:gain.jsfx", directory);

            Assert.NotNull(loaded);
            Assert.True(loaded!.Ok, loaded.Reason + " | " + client.LastBridgeError);
            Assert.Equal(2, loaded.InputChannels);
            Assert.Equal(2, loaded.OutputChannels);

            var parameters = client.GetParametersBlocking(slot);
            Assert.NotNull(parameters);
            Assert.Contains(parameters!, p => p.Name == "Gain");
            Assert.Contains(parameters!, p => p.Name == "Mix");

            Assert.True(client.SetParameterBlocking(slot, 0, 0.5));

            var inPlanes = new[]
            {
                Enumerable.Repeat(0.25f, 512).ToArray(),
                Enumerable.Repeat(-0.25f, 512).ToArray(),
            };

            var outPlanes = new[]
            {
                new float[512],
                new float[512],
            };

            Assert.True(client.SendProcess(slot, inPlanes, outPlanes, 2, 512, false),
                "process failed: " + client.LastBridgeError);
            Assert.InRange(outPlanes[0][100], 0.124f, 0.126f);
            Assert.InRange(outPlanes[1][100], -0.126f, -0.124f);

            var state = client.GetStateBlocking(slot);
            Assert.NotNull(state);
            Assert.NotEmpty(state!);
            Assert.True(client.SetStateBlocking(slot, state!));
        }
        finally
        {
            client.UnloadBlocking(slot);
            Directory.Delete(directory, recursive: true);
        }
    }
}
