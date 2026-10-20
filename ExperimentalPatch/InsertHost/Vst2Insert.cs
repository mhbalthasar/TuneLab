using System;
using System.Collections.Generic;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.ExperimentalPatch.VstBridge;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 一个装进**桥进程**的 VST2 插件实例：音频线程上跑的那一半。
//
// 【为什么 VST2 一律过桥，不在宿主进程内直载】
// 实证：46 个 64 位 VST2 插件里有 1 个（REAPER 的 ReaControlMIDI）在
// effSetChunk 里直接 AccessViolation；进程内调用意味着 TuneLab 整个退出。
// VST2 没有沙箱、没有进程内隔离手段，而 VST3 那套 wrapper 至少还有参数校验。
// 所以位数不再是"直载 / 过桥"的分界 —— 统一过桥，桥按插件位数选 32/64 位。
// 代价是每块音频多一次共享内存往返（实测单块几十微秒），换来的是插件怎么崩
// 都只是桥进程重启，工程与界面不受影响。
//
// 【实时纪律】与 Vst3Insert 相同：Process 里不分配、不加锁、不抛异常。
public sealed class Vst2Insert : IMixInsert, IDisposable
{
    readonly VstBridgeClient mClient;
    readonly int mSlotId;
    readonly int mChannels;
    readonly int mBlockSize;
    readonly float[][] mInPlanes;
    readonly float[][] mOutPlanes;

    bool mBypassed;
    bool mDisposed;

    Vst2Insert(InsertPluginInfo plugin, VstBridgeClient client, int slotId,
        int blockSize, int latencySamples, bool bypassed)
    {
        Plugin = plugin;
        Name = plugin.Name;
        mClient = client;
        mSlotId = slotId;
        mBlockSize = Math.Max(blockSize, 32);

        // 桥是立体声的（SharedAudioLayout.MaxChannels = 2）。
        mChannels = SharedAudioLayout.MaxChannels;

        mInPlanes = new float[mChannels][];
        mOutPlanes = new float[mChannels][];
        for (int ch = 0; ch < mChannels; ch++)
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

    // VST2 的 latency 在 AEffect.initialDelay 里，加载时读一次。插件运行中不该变。
    public int LatencySamples { get; }

    public void SetBypassed(bool bypassed) => mBypassed = bypassed;

    public void Process(Span<float> buffer, int offset, int count)
    {
        if (mDisposed || mBypassed || count <= 0)
            return;

        int n = Math.Min(count, mBlockSize);

        // 交织 → 平面。
        for (int ch = 0; ch < mChannels; ch++)
        {
            var plane = mInPlanes[ch];
            for (int i = 0; i < n; i++)
                plane[i] = buffer[offset + i * 2 + (ch % 2)];
        }

        for (int ch = 0; ch < mChannels; ch++)
            Array.Clear(mOutPlanes[ch], 0, n);

        bool ok;
        try
        {
            ok = mClient.SendProcess(mSlotId, mInPlanes, mOutPlanes, mChannels, n, bypass: false);
        }
        catch (Exception)
        {
            ok = false;
        }

        // 失败 = 这一块直通（buffer 原样不动）。桥死了的话下一块也失败，
        // 上层看到持续失败会把这个槽位旁通掉。
        if (!ok)
            return;

        // 平面 → 交织。
        for (int i = 0; i < n; i++)
        {
            buffer[offset + i * 2] = mOutPlanes[0][i];
            buffer[offset + i * 2 + 1] = mChannels > 1 ? mOutPlanes[1][i] : mOutPlanes[0][i];
        }
    }

    // ── 状态与参数（只在 UI 线程用）──

    public byte[]? SaveState() => mClient.GetStateBlocking(mSlotId);

    public bool LoadState(byte[] data) => data.Length > 0 && mClient.SetStateBlocking(mSlotId, data);

    public IReadOnlyList<ParameterInfo> GetParameters() => mClient.GetParametersBlocking(mSlotId) ?? [];

    public bool SetParameter(long id, double value) => mClient.SetParameterBlocking(mSlotId, id, value);

    public void Dispose()
    {
        if (mDisposed)
            return;

        mDisposed = true;

        try
        {
            mClient.UnloadBlocking(mSlotId);
        }
        catch (Exception)
        {
            // 卸载阶段桥已经死了：没有可做的补救，别往 UI 线程抛。
        }
    }

    // ── 建实例 ──

    /// <summary>
    /// 按插件位数选桥、启动它、把插件装进去。失败返回 null 并写 reason。
    /// 只在 UI 线程调（会同步等桥的回复）。
    /// </summary>
    public static Vst2Insert? Create(InsertPluginInfo plugin, InsertSettings settings, out string? reason)
    {
        reason = null;

        int bitness = PluginBitness.Read(plugin.Locator);
        if (bitness == 0)
        {
            reason = "the file is not a valid 32/64-bit Windows dll";
            return null;
        }

        var client = VstBridgeClient.ForBitness(bitness);
        client.ClearBridgeError();

        if (!client.EnsureStartedBlocking())
        {
            reason = $"the VST2 bridge ({bitness}-bit) could not be started";
            if (client.LastBridgeError is { Length: > 0 } detail)
                reason += " — " + detail;

            return null;
        }

        int slotId = client.AllocateSlot();
        var loaded = client.LoadBlocking(slotId, plugin.Locator, settings.SampleRate, settings.MaxBlockSize,
            settings.Bypass);

        if (loaded == null)
        {
            reason = "the VST2 bridge did not answer the load request";
            return null;
        }

        if (!loaded.Ok)
        {
            reason = loaded.Reason ?? "the plugin refused to load";
            return null;
        }

        // 0 进 0 出 = 纯 MIDI 工具，装进来也处理不了音频。
        if (loaded.InputChannels <= 0 || loaded.OutputChannels <= 0)
        {
            client.UnloadBlocking(slotId);
            reason = "the plugin has no audio channels (it is a MIDI utility, not an effect)";
            return null;
        }

        return new Vst2Insert(plugin, client, slotId, settings.MaxBlockSize,
            loaded.LatencySamples, settings.Bypass);
    }
}
