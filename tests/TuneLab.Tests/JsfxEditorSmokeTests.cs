using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.ExperimentalPatch.VstBridge;
using Xunit;

namespace TuneLab.Tests;

[Collection("IsolatedBridge")]
public class JsfxEditorSmokeTests
{
    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hWnd);

    static JsfxEditorSmokeTests()
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
    public void HostInsert_OpensAndClosesEditor()
    {
        Skip.If(!OperatingSystem.IsWindows(), "native editor windows are only asserted on Windows");
        Skip.If(!VstBridgeClient.BridgeExecutableExists, "the 64-bit bridge is not published beside the tests");

        string directory = Path.Combine(Path.GetTempPath(), "tunelab-jsfx-host-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string file = Path.Combine(directory, "editor.jsfx");
        File.WriteAllText(file, """
            desc: TuneLab JSFX host editor smoke
            @init
            frames = 0;
            @sample
            spl0 = spl0;
            spl1 = spl1;
            @gfx
            frames += 1;
            gfx_set(0.2, 0.7, 0.3, 1);
            gfx_rect(0, 0, gfx_w, gfx_h);
            """);

        try
        {
            var plugin = new InsertPluginInfo
            {
                Format = JsfxBackend.FormatName,
                PluginId = "jsfx:editor.jsfx",
                Name = "TuneLab JSFX host editor smoke",
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
            var parameterInsert = (IParameterInsert)insert!;

            Assert.True(parameterInsert.HasEditor);
            Assert.True(parameterInsert.OpenEditor(IntPtr.Zero));
            Assert.True(parameterInsert.IsEditorOpen);
            Assert.NotEqual(IntPtr.Zero, parameterInsert.FindEditorWindowSince(parameterInsert.SnapshotTopLevelWindows()));

            Thread.Sleep(300);

            parameterInsert.CloseEditor();
            Assert.False(parameterInsert.IsEditorOpen);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Bridge_OpensAndClosesGfxWindow()
    {
        Skip.If(!OperatingSystem.IsWindows(), "native editor windows are only asserted on Windows");
        Skip.If(!VstBridgeClient.BridgeExecutableExists, "the 64-bit bridge is not published beside the tests");

        var client = VstBridgeClient.ForBitness(64);
        client.ClearBridgeError();

        Skip.If(!client.EnsureStartedBlocking(), "the bridge did not start: " + client.LastBridgeError);
        Skip.If(client.BridgeFormats == null
                || !client.BridgeFormats.Contains(PluginFormats.Jsfx, StringComparer.OrdinalIgnoreCase),
            "this bridge does not carry the ysfx runtime: " + client.LastBridgeError);

        string directory = Path.Combine(Path.GetTempPath(), "tunelab-jsfx-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string file = Path.Combine(directory, "editor.jsfx");
        File.WriteAllText(file, """
            desc: TuneLab JSFX editor smoke
            @init
            frames = 0;
            @sample
            spl0 = spl0;
            spl1 = spl1;
            @gfx
            frames += 1;
            gfx_set(0.2, 0.7, 0.3, 1);
            gfx_rect(0, 0, gfx_w, gfx_h);
            """);

        int slot = 9177;

        try
        {
            var loaded = client.LoadBlocking(slot, file, 48000, 512, false,
                PluginFormats.Jsfx, "jsfx:editor.jsfx", directory);

            Assert.NotNull(loaded);
            Assert.True(loaded!.Ok, loaded.Reason + " | " + client.LastBridgeError);
            Assert.True(loaded.HasEditor);

            var (ok, reason, _, window) = client.OpenEditorBlocking(slot, "JSFX editor smoke");
            Assert.True(ok, reason + " | " + client.LastBridgeError);
            Assert.NotEqual(0, window);
            Assert.True(IsWindow((IntPtr)window), "the bridge did not return a live native window");

            Thread.Sleep(500);
            Assert.True(IsWindow((IntPtr)window), "the @gfx window did not survive the event pump");

            Assert.True(client.CloseEditorBlocking(slot), client.LastBridgeError);
        }
        finally
        {
            client.UnloadBlocking(slot);
            Directory.Delete(directory, recursive: true);
        }
    }
}
