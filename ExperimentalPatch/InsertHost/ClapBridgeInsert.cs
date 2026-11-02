using System;
using System.Collections.Generic;
using System.Linq;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.ExperimentalPatch.VstBridge;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 一个跑在**桥进程**里的 CLAP 插件实例：宿主侧的 IMixInsert / IParameterInsert 实现（P-C2）。
//
// 【与 Vst3BridgeInsert 的关系】两者同构：音频线程把交织块翻成平面、用共享环发一条
// process 指令；插件崩 / 挂只死桥，宿主继续跑。区别全在桥侧（ClapPluginHost 走 C ABI，
// Vst3PluginHost 走 vendored 的 ownvst3）。
//
// 【为什么也隔离】CLAP 的 `clap_entry.init()` 就是执行任意插件代码：一条坏插件足以
// 把进程带走（chowdsp 那几个在 VST3 下真机复现过）。扫描已经因此过桥；播放同理，
// 没有进程内回退。
//
// 【为什么要实现 IParameterInsert】桥只负责"插件那边"；旁通、参数面板、编辑器壳认的都是
// 宿主侧契约 IParameterInsert（与 Vst3BridgeInsert 同一份）。参数面板只认能力，不认格式。
//
// 【实时纪律】与 Vst2BridgeInsert / Vst3BridgeInsert 相同：Process 里不分配、不加锁、
// 不抛异常。
public sealed class ClapBridgeInsert : IMixInsert, IParameterInsert, IRuntimeInsertFailure, IDisposable
{
    readonly IBridgeChannel mChannel;
    readonly int mSlotId;
    readonly int mInputChannels;
    readonly int mSidechainChannels;
    readonly int mBlockSize;
    readonly float[][] mInPlanes;
    readonly float[][] mOutPlanes;

    volatile bool mBypassed;
    bool mDisposed;

    ClapBridgeInsert(InsertPluginInfo plugin, IBridgeChannel channel, int slotId, int blockSize,
        int latencySamples, bool bypassed, int inputChannels)
    {
        Plugin = plugin;
        Name = plugin.Name;

        mChannel = channel;
        mSlotId = slotId;
        mBlockSize = Math.Max(blockSize, 32);

        mInputChannels = Math.Max(0, inputChannels);
        mSidechainChannels = Math.Clamp(mInputChannels - 2, 0, 2);

        // 平面缓冲按共享环的最大声道数（4）分配，与另外两条桥路同口径。
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

    public int SidechainChannels => mSidechainChannels;

    // 编辑器是 P-C3（CLAP.md §6）。在它落地之前界面自动退参数面板 ——
    // 与 VST3 在桥不带原生库时的降级同一条路。
    public bool HasEditor => false;

    public bool NeedsOpenGl => false;

    public (int Width, int Height)? EditorSize => null;

    public void SetBypassed(bool bypassed) => mBypassed = bypassed;

    // 运行期音频挂起：插件在桥的 process 里卡死 → SendProcess 连续失败到阈值，
    // 桥的音频路停用（见 VstBridgeClient.AudioStalled）。界面据此把这一行标红。
    public string? RuntimeFailureReason
        => mChannel.AudioStalled ? IRuntimeInsertFailure.AudioStallReason : null;

    public void Process(Span<float> buffer, int offset, int count)
        => Process(buffer, offset, count, default);

    public void Process(Span<float> buffer, int offset, int count, ReadOnlySpan<float> sidechain)
    {
        if (mDisposed || mBypassed || count <= 0)
            return;

        int channels = sidechain.IsEmpty || mSidechainChannels == 0 ? 2 : 2 + mSidechainChannels;

        for (int done = 0; done < count; done += mBlockSize)
        {
            int n = Math.Min(mBlockSize, count - done);

            if (!ProcessChunk(buffer, offset + done * 2, n, sidechain, done, channels))
                return;   // 桥超时 / 死了：剩下的部分保持直通
        }
    }

    bool ProcessChunk(Span<float> buffer, int offset, int n,
        ReadOnlySpan<float> sidechain, int sidechainOffset, int channels)
    {
        // 交织 → 平面（与另外两条桥路逐位相同的填充公式）。
        for (int ch = 0; ch < channels; ch++)
        {
            var plane = mInPlanes[ch];

            if (ch < 2)
            {
                for (int i = 0; i < n; i++)
                    plane[i] = buffer[offset + i * 2 + ch];

                continue;
            }

            if (mSidechainChannels == 1)
            {
                for (int i = 0; i < n; i++)
                {
                    int frame = sidechainOffset + i;
                    plane[i] = frame < sidechain.Length / 2
                        ? (sidechain[frame * 2] + sidechain[frame * 2 + 1]) * 0.5f
                        : 0f;
                }

                continue;
            }

            int source = ch - 2;

            for (int i = 0; i < n; i++)
            {
                int frame = sidechainOffset + i;
                plane[i] = frame < sidechain.Length / 2 ? sidechain[frame * 2 + source] : 0f;
            }
        }

        for (int ch = 0; ch < channels; ch++)
            Array.Clear(mOutPlanes[ch], 0, n);

        bool ok;

        try
        {
            ok = mChannel.SendProcess(mSlotId, mInPlanes, mOutPlanes, channels, n, bypass: false);
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
            float right = channels > 1 ? mOutPlanes[1][i] : mOutPlanes[0][i];

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

    // 停止播放时把参数改动冲进处理器：发一小块静音过插件，桥那边会跑一次 process()
    // （CLAP 的参数事件也是在 process 里交付的，与 VST3 同一个道理）。
    public void FlushParameterChanges()
    {
        if (mDisposed || mBypassed)
            return;

        const int frames = 512;
        var silence = new float[frames * 2];
        Process(silence, 0, frames);
    }

    // ── 编辑器（P-C3）──

    public bool OpenEditor(IntPtr parent) => false;

    public bool IsEditorOpen => false;

    public void PumpEditorMessages()
    {
    }

    public void CloseEditor()
    {
    }

    public void ResizeEditor(int width, int height)
    {
    }

    public HashSet<IntPtr> SnapshotTopLevelWindows() => [];

    public IntPtr FindEditorWindowSince(HashSet<IntPtr> before) => IntPtr.Zero;

    // 桥进程的 stderr（最近几十行）。加载失败时带上它。
    public string? BridgeError => mChannel.LastBridgeError;

    public void Dispose()
    {
        if (mDisposed)
            return;

        mDisposed = true;

        try
        {
            mChannel.UnloadBlocking(mSlotId);
        }
        catch (Exception)
        {
            // 卸载阶段桥已经死了：没有可做的补救，别往 UI 线程抛。
        }
    }

    // ── 建实例 ──

    // 隔离在当前平台是否**原则上**被支持：64 位 + Windows / Linux / macOS。
    // （CLAP 锁 64 位：32 位的桥不声明 clap，见 VST3-BRIDGE.md §9.1 的同一条理由。）
    public static bool PlatformSupported =>
        Environment.Is64BitProcess &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

    // 本机**静态**上能不能跑 CLAP：平台 + 64 位 + 64 位桥的可执行文件就位。
    // 设置页 / 菜单渲染时会调它，不能为了画一行状态去起一个进程。
    public static bool IsolationAvailable =>
        PlatformSupported && VstBridgeClient.BridgeExecutableExists;

    /// <summary>IsolationAvailable 为 false 时的短原因（进菜单与设置页状态行）。</summary>
    public static string? IsolationUnavailableReason => PlatformSupported
        ? "the 64-bit bridge executable is not installed beside the application"
        : "the isolated CLAP host requires a 64-bit Windows, Linux or macOS host";

    /// <summary>
    /// 把 CLAP 装进 64 位桥。失败返回 null 并写 reason。
    /// bridgeUnavailable = 失败在"桥这一侧"（没起来 / 不带 clap）；false = 桥是好的，
    /// 是插件自己拒绝 / 加载时把桥弄没了。两种都只是"这个插入装不进来"，没有回退。
    /// 只在 UI 线程调（会同步等桥的回复）。
    /// </summary>
    public static ClapBridgeInsert? Create(InsertPluginInfo plugin, InsertSettings settings,
        out string? reason, out bool bridgeUnavailable)
    {
        reason = null;
        bridgeUnavailable = false;

        if (!PlatformSupported)
        {
            reason = "the isolated CLAP host requires a 64-bit Windows, Linux or macOS host";
            bridgeUnavailable = true;
            return null;
        }

        // Locator 是"文件\npluginId"两段（一个 .clap 可以声明多个插件）。
        if (!ClapBackend.TrySplitLocator(plugin.Locator, out string path, out string pluginId))
        {
            reason = "plugin location is unknown (rescan first)";
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

        // 桥自报支持 clap 才继续：旧桥只声明 vst2/vst3。
        if (client.BridgeFormats is { } formats
            && !formats.Contains(PluginFormats.Clap, StringComparer.OrdinalIgnoreCase))
        {
            reason = "the 64-bit bridge does not carry CLAP";
            bridgeUnavailable = true;
            return null;
        }

        // ── 从这条线以下：桥是好的，失败都算插件的（不回退进程内）──

        int slotId = client.AllocateSlot();

        var loaded = client.LoadBlocking(slotId, path, settings.SampleRate, blockSize,
            settings.Bypass, format: PluginFormats.Clap, pluginId: pluginId);

        if (loaded == null)
        {
            reason = "the 64-bit bridge did not answer the load request";

            if (client.LastBridgeError is { Length: > 0 } detail)
                reason += " — " + detail;

            return null;
        }

        if (!loaded.Ok)
        {
            reason = loaded.Reason ?? "the plugin refused to load";
            return null;
        }

        if (loaded.IsSynth)
        {
            client.UnloadBlocking(slotId);
            reason = "this plugin is an instrument; inserts need an effect";
            return null;
        }

        if (loaded.InputChannels <= 0 || loaded.OutputChannels <= 0)
        {
            client.UnloadBlocking(slotId);
            reason = "the plugin has no audio channels (it is a MIDI utility, not an effect)";
            return null;
        }

        return new ClapBridgeInsert(plugin, client, slotId, blockSize,
            loaded.LatencySamples, settings.Bypass, loaded.InputChannels);
    }
}
