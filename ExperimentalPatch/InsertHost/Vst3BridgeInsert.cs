using System;
using System.Collections.Generic;
using System.Linq;
using OwnVST3Host;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.ExperimentalPatch.VstBridge;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 一个跑在**桥进程**里的 VST3 插件实例：宿主侧的 IVst3Insert 实现。
//
// 【它在整条链里的位置】与 Vst2Insert 完全同构：宿主音频线程把交织块翻成平面、
// 用共享环发一条 process 指令；桥那边真正的插件崩 / 挂只死桥，宿主继续跑。
// 区别只在插件格式：VST2 走 AEffect，VST3 走桥侧的 Vst3PluginHost（它复用 Vst3Insert）。
//
// 【为什么 VST3 也要隔离】实测（NOTES-vst3-vendor.md）：chowdsp 的 CHOWTapeModel /
// ChowMatrix 在 Create 里直接卡死，BYOD / ChowCentaur 建第二个实例时卡死；还有 Blue Cat
// 历史性的 native abort。进程内遇到这些 = 整个 TuneLab 没了，连工程都没得救。
// 过桥之后，超时（P4）一杀桥，宿主只是"这个插入旁通了"。
//
// 【实时纪律】与 Vst2Insert 相同：Process 里不分配、不加锁、不抛异常。
public sealed class Vst3BridgeInsert : IMixInsert, IVst3Insert, IDisposable
{
    readonly IBridgeChannel mChannel;
    readonly int mSlotId;
    readonly int mInputChannels;
    readonly int mSidechainChannels;
    readonly int mBlockSize;
    readonly float[][] mInPlanes;
    readonly float[][] mOutPlanes;
    readonly bool mNeedsOpenGl;

    volatile bool mBypassed;
    bool mDisposed;

    Vst3BridgeInsert(InsertPluginInfo plugin, IBridgeChannel channel, int slotId, int blockSize,
        int latencySamples, bool hasEditor, bool needsOpenGl, bool bypassed, int inputChannels)
    {
        Plugin = plugin;
        Name = plugin.Name;

        mChannel = channel;
        mSlotId = slotId;
        mBlockSize = Math.Max(blockSize, 32);
        HasEditor = hasEditor;
        mNeedsOpenGl = needsOpenGl;

        mInputChannels = Math.Max(0, inputChannels);
        mSidechainChannels = Math.Clamp(mInputChannels - 2, 0, 2);

        // 平面缓冲按共享环的最大声道数（4）分配，与 Vst2Insert 同口径。
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

    // 桥模式 = 真的隔离了。
    public bool IsIsolated => true;

    public bool IsBypassed => mBypassed;

    public int LatencySamples { get; }

    public int InputChannels => mInputChannels;

    public int SidechainChannels => mSidechainChannels;

    // P1：桥侧还不开插件 GUI（见 VST3-BRIDGE.md），所以这里是桥回报的值 —— 目前恒 false。
    public bool HasEditor { get; }

    public bool NeedsOpenGl => mNeedsOpenGl;

    public (int Width, int Height)? EditorSize => null;

    public void SetBypassed(bool bypassed) => mBypassed = bypassed;

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
        // 交织 → 平面（与 Vst2Insert 逐位相同的填充公式）。
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

    public IReadOnlyList<VST3Parameter> Parameters
    {
        get
        {
            var raw = mChannel.GetParametersBlocking(mSlotId);
            if (raw == null)
                return [];

            return raw.Select(p => new VST3Parameter
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

    // 停止播放时把参数改动冲进处理器：发一小块静音过插件，桥那边会跑一次 process()。
    // 只在 UI 线程、且不在播放时调（父类契约如此），所以直接复用一个栈上的静音块即可。
    public void FlushParameterChanges()
    {
        if (mDisposed || mBypassed)
            return;

        const int frames = 512;
        var silence = new float[frames * 2];
        Process(silence, 0, frames);
    }

    // ── 编辑器（P1 一律不可用；P2 由桥侧开窗 + 回传 HWND）──

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

    // 桥进程的 stderr（最近几十行）。加载 / 编辑器失败时带上它。
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

    /// <summary>
    /// 把 VST3 装进 64 位桥。失败返回 null 并写 reason。
    /// bridgeUnavailable = 失败在"桥这一侧"（没起来 / 不带 vst3）—— 这时调用方应回退进程内；
    /// false 表示桥是好的、是插件自己拒绝 / 加载时把桥弄没了，调用方应如实报失败（别回退）。
    /// 只在 UI 线程调（会同步等桥的回复）。
    /// </summary>
    public static Vst3BridgeInsert? Create(InsertPluginInfo plugin, InsertSettings settings,
        out string? reason, out bool bridgeUnavailable)
    {
        reason = null;
        bridgeUnavailable = false;

        // VST3 的 native 只 vendor 了 win-x64 一份；别的平台 / 位数没有可用的桥。
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            reason = "the isolated VST3 host is only available on Windows x64";
            bridgeUnavailable = true;
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

        // 桥自报支持 vst3 才继续：旧桥 / 缺 native 的桥只声明 vst2。
        if (client.BridgeFormats is { } formats
            && !formats.Contains(PluginFormats.Vst3, StringComparer.OrdinalIgnoreCase))
        {
            reason = "the 64-bit bridge does not carry VST3";
            bridgeUnavailable = true;
            return null;
        }

        // ── 从这条线以下：桥是好的，失败都算插件的（不回退进程内）──

        int slotId = client.AllocateSlot();
        var loaded = client.LoadBlocking(slotId, plugin.Locator, settings.SampleRate, blockSize,
            settings.Bypass, format: PluginFormats.Vst3);

        if (loaded == null)
        {
            // 加载超时（插件卡在 Create）会走到这里：LastBridgeError 里带着"没在 Ns 内回、桥已杀"。
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

        return new Vst3BridgeInsert(plugin, client, slotId, blockSize,
            loaded.LatencySamples, loaded.HasEditor, loaded.NeedsOpenGl, settings.Bypass, loaded.InputChannels);
    }

    // 测试用的入口：直接注入一个假的桥通道，不碰 VstBridgeClient。
    internal static Vst3BridgeInsert CreateForTest(InsertPluginInfo plugin, IBridgeChannel channel, int slotId,
        int blockSize, int latencySamples, bool hasEditor, bool needsOpenGl, bool bypassed, int inputChannels)
        => new(plugin, channel, slotId, blockSize, latencySamples, hasEditor, needsOpenGl, bypassed, inputChannels);
}
