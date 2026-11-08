using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.ExperimentalPatch.VstBridge;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 一个跑在**桥进程**里的 JSFX 实例：宿主侧的 IMixInsert / IParameterInsert 实现。
//
// 与 ClapBridgeInsert 同构：音频线程把交织块翻成平面、用共享环发一条 process 指令；
// JSFX 崩 / 卡死只死桥，宿主继续跑。区别全在桥侧（JsfxPluginHost 调 ysfx C ABI）。
//
// @gfx 的图形窗口是 P4；现在 HasEditor 一律 false，界面自动退到参数面板。
public sealed class JsfxBridgeInsert : IMixInsert, IParameterInsert, IRuntimeInsertFailure, IDisposable
{
    readonly IBridgeChannel mChannel;
    readonly int mSlotId;
    readonly int mInputChannels;
    readonly int mBlockSize;
    readonly bool mHasEditor;
    readonly float[][] mInPlanes;
    readonly float[][] mOutPlanes;

    volatile bool mBypassed;
    bool mDisposed;
    bool mEditorOpen;
    IntPtr mEditorWindow;

    JsfxBridgeInsert(InsertPluginInfo plugin, IBridgeChannel channel, int slotId, int blockSize,
        int latencySamples, bool bypassed, int inputChannels, bool hasEditor)
    {
        Plugin = plugin;
        Name = plugin.Name;

        mChannel = channel;
        mSlotId = slotId;
        mBlockSize = Math.Max(blockSize, 32);
        mInputChannels = Math.Max(0, inputChannels);
        mHasEditor = hasEditor;

        const int maxChannels = SharedAudioLayout.MaxChannels;

        mInPlanes = new float[maxChannels][];
        mOutPlanes = new float[maxChannels][];

        for (int ch = 0; ch < maxChannels; ch++)
        {
            mInPlanes[ch] = new float[mBlockSize];
            mOutPlanes[ch] = new float[mBlockSize];
        }

        LatencySamples = Math.Max(0, latencySamples);
        mBypassed = bypassed;
    }

    public string Name { get; }

    public InsertPluginInfo Plugin { get; }

    public bool IsBypassed => mBypassed;

    public int LatencySamples { get; }

    public int InputChannels => mInputChannels;

    public int SidechainChannels => 0;

    public bool HasEditor => mHasEditor;

    public bool NeedsOpenGl => false;

    public (int Width, int Height)? EditorSize => null;

    public void SetBypassed(bool bypassed) => mBypassed = bypassed;

    public string? RuntimeFailureReason
        => mChannel.AudioStalled ? IRuntimeInsertFailure.AudioStallReason : null;

    public void Process(Span<float> buffer, int offset, int count)
        => Process(buffer, offset, count, default);

    public void Process(Span<float> buffer, int offset, int count, ReadOnlySpan<float> sidechain)
    {
        if (mDisposed || mBypassed || count <= 0)
            return;

        for (int done = 0; done < count; done += mBlockSize)
        {
            int n = Math.Min(mBlockSize, count - done);

            if (!ProcessChunk(buffer, offset + done * 2, n))
                return;
        }
    }

    bool ProcessChunk(Span<float> buffer, int offset, int n)
    {
        for (int ch = 0; ch < 2; ch++)
        {
            var plane = mInPlanes[ch];

            for (int i = 0; i < n; i++)
                plane[i] = buffer[offset + i * 2 + ch];

            Array.Clear(mOutPlanes[ch], 0, n);
        }

        bool ok;

        try
        {
            ok = mChannel.SendProcess(mSlotId, mInPlanes, mOutPlanes, 2, n, bypass: false);
        }
        catch (Exception)
        {
            ok = false;
        }

        if (!ok)
            return false;

        for (int i = 0; i < n; i++)
        {
            float left = mOutPlanes[0][i];
            float right = mOutPlanes[1][i];

            buffer[offset + i * 2] = float.IsFinite(left) ? left : 0f;
            buffer[offset + i * 2 + 1] = float.IsFinite(right) ? right : 0f;
        }

        return true;
    }

    // ── 状态与参数（只在 UI 线程用）──

    public byte[]? SaveState() => mChannel.GetStateBlocking(mSlotId);

    public bool LoadState(byte[] data) => data.Length > 0 && mChannel.SetStateBlocking(mSlotId, data);

    public IReadOnlyList<InsertParameter> Parameters
    {
        get
        {
            var raw = mChannel.GetParametersBlocking(mSlotId);

            if (raw == null)
                return [];

            return raw.Select(p => new InsertParameter
            {
                Id = (int)p.Id,
                Name = p.Name,
                MinValue = p.Min,
                MaxValue = p.Max,
                DefaultValue = p.DefaultValue,
                CurrentValue = p.Value,
            }).ToList();
        }
    }

    public double GetParameter(int id) => mChannel.GetParametersBlocking(mSlotId)?
        .FirstOrDefault(p => p.Id == id)?.Value ?? 0;

    public void SetParameter(int id, double value) => mChannel.SetParameterBlocking(mSlotId, id, value);

    public void FlushParameterChanges()
    {
        if (mDisposed || mBypassed)
            return;

        const int frames = 512;
        var silence = new float[frames * 2];
        Process(silence, 0, frames);
    }

    // ── @gfx 编辑器 ──
    //
    // 窗口由桥进程里的 ysfx 自己创建，所以宿主侧只负责开关与句柄查询；
    // 消息循环跑在桥的专用 UI 线程上，PumpEditorMessages 不需要再泵。

    public bool OpenEditor(IntPtr parent)
    {
        if (!mHasEditor || mDisposed)
            return false;

        if (mEditorOpen && (!OperatingSystem.IsWindows() || mEditorWindow == IntPtr.Zero || IsWindow(mEditorWindow)))
            return true;

        var (ok, _, _, window) = mChannel.OpenEditorBlocking(mSlotId, Name);

        if (!ok)
        {
            mEditorOpen = false;
            mEditorWindow = IntPtr.Zero;
            return false;
        }

        mEditorWindow = window == 0 ? IntPtr.Zero : new IntPtr(window);
        mEditorOpen = true;
        return true;
    }

    public bool IsEditorOpen
    {
        get
        {
            if (!mEditorOpen)
                return false;

            if (OperatingSystem.IsWindows() && mEditorWindow != IntPtr.Zero && !IsWindow(mEditorWindow))
            {
                mEditorOpen = false;
                return false;
            }

            return true;
        }
    }

    public void PumpEditorMessages()
    {
    }

    public void CloseEditor()
    {
        if (!mEditorOpen && mEditorWindow == IntPtr.Zero)
            return;

        mChannel.CloseEditorBlocking(mSlotId);
        mEditorOpen = false;
        mEditorWindow = IntPtr.Zero;
    }

    // 窗口尺寸由 @gfx 自己和用户拖动决定，宿主不强行改。
    public void ResizeEditor(int width, int height)
    {
    }

    public HashSet<IntPtr> SnapshotTopLevelWindows() => [];

    public IntPtr FindEditorWindowSince(HashSet<IntPtr> before) => mEditorWindow;

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hWnd);

    public string? BridgeError => mChannel.LastBridgeError;

    public void Dispose()
    {
        if (mDisposed)
            return;

        mDisposed = true;

        try
        {
            CloseEditor();
            mChannel.UnloadBlocking(mSlotId);
        }
        catch (Exception)
        {
        }
    }

    // ── 建实例 ──

    public static bool PlatformSupported =>
        Environment.Is64BitProcess &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

    public static bool IsolationAvailable =>
        PlatformSupported && VstBridgeClient.BridgeExecutableExists;

    public static string? IsolationUnavailableReason => PlatformSupported
        ? "the 64-bit bridge executable is not installed beside the application"
        : "the isolated JSFX host requires a 64-bit Windows, Linux or macOS host";

    public static JsfxBridgeInsert? Create(InsertPluginInfo plugin, InsertSettings settings,
        out string? reason, out bool bridgeUnavailable)
    {
        reason = null;
        bridgeUnavailable = false;

        if (!PlatformSupported)
        {
            reason = "the isolated JSFX host requires a 64-bit Windows, Linux or macOS host";
            bridgeUnavailable = true;
            return null;
        }

        if (string.IsNullOrWhiteSpace(plugin.Locator) || !File.Exists(plugin.Locator))
        {
            reason = "the JSFX file is missing (rescan the JSFX paths)";
            return null;
        }

        int blockSize = Math.Clamp(settings.MaxBlockSize, 32, SharedAudioLayout.MaxBlockFrames);

        var client = VstBridgeClient.ForBitness(64);
        client.ClearBridgeError();

        if (!client.EnsureStartedBlocking())
        {
            reason = "the 64-bit bridge could not be started";

            if (client.LastBridgeError is { Length: > 0 } detail)
                reason += " — " + detail;

            bridgeUnavailable = true;
            return null;
        }

        if (client.BridgeFormats is { } formats
            && !formats.Contains(PluginFormats.Jsfx, StringComparer.OrdinalIgnoreCase))
        {
            reason = "the 64-bit bridge does not carry the ysfx runtime";
            bridgeUnavailable = true;
            return null;
        }

        int slotId = client.AllocateSlot();
        string importRoot = GuessImportRoot(plugin.Locator, plugin.PluginId);

        var loaded = client.LoadBlocking(slotId, plugin.Locator, settings.SampleRate, blockSize,
            settings.Bypass, format: PluginFormats.Jsfx, pluginId: plugin.PluginId, importRoot: importRoot);

        if (loaded == null)
        {
            reason = "the 64-bit bridge did not answer the load request";

            if (client.LastBridgeError is { Length: > 0 } detail)
                reason += " — " + detail;

            return null;
        }

        if (!loaded.Ok)
        {
            reason = loaded.Reason ?? "the JSFX refused to load";
            return null;
        }

        if (loaded.InputChannels != 2 || loaded.OutputChannels != 2)
        {
            client.UnloadBlocking(slotId);
            reason = "JSFX inserts require 2 inputs and 2 outputs";
            return null;
        }

        return new JsfxBridgeInsert(plugin, client, slotId, blockSize,
            loaded.LatencySamples, settings.Bypass, loaded.InputChannels, loaded.HasEditor);
    }

    // PluginId 是相对配置根路径的（jsfx:sub/foo.jsfx）；Locator 是绝对路径。
    // import 根 = Locator 去掉那段相对路径。取不出来就退回文件所在目录。
    static string GuessImportRoot(string locator, string pluginId)
    {
        const string prefix = "jsfx:";
        string relative = pluginId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? pluginId[prefix.Length..]
            : string.Empty;

        if (relative.Length == 0)
            return Path.GetDirectoryName(locator) ?? string.Empty;

        string full = Path.GetFullPath(locator).Replace('\\', '/');
        string suffix = relative.Replace('\\', '/').TrimStart('/');

        if (suffix.Length > 0 && full.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            string root = full[..^suffix.Length].TrimEnd('/');
            if (root.Length > 0)
                return root.Replace('/', Path.DirectorySeparatorChar);
        }

        return Path.GetDirectoryName(locator) ?? string.Empty;
    }
}
