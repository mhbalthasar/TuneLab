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

    volatile bool mBypassed;
    bool mDisposed;

    Vst2Insert(InsertPluginInfo plugin, VstBridgeClient client, int slotId,
        int blockSize, int latencySamples, bool hasEditor, bool bypassed)
    {
        Plugin = plugin;
        Name = plugin.Name;
        mClient = client;
        mSlotId = slotId;
        mBlockSize = Math.Max(blockSize, 32);
        HasEditor = hasEditor;

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

    // AEffect.flags 的 effFlagsHasEditor，加载时读一次。
    public bool HasEditor { get; }

    public void SetBypassed(bool bypassed) => mBypassed = bypassed;

    // 编辑器窗口由**桥进程**开（插件在哪个进程加载，窗口就只能在哪个进程），
    // 所以这里只是发一条消息 + 把失败原因带回来。调用会等插件建完 UI。
    public (bool Ok, string? Reason) OpenEditor() => mClient.OpenEditorBlocking(mSlotId, Name);

    // 窗口已经在桥那边开着：把它带到前台。已经没开（插件没界面 / 窗口被关过）时返回 false。
    public bool TryActivateEditor() => mClient.BringEditorToFrontBlocking(mSlotId);

    public bool CloseEditor() => mClient.CloseEditorBlocking(mSlotId);

    public void Process(Span<float> buffer, int offset, int count)
    {
        if (mDisposed || mBypassed || count <= 0)
            return;

        // 整块都要过插件：逐段处理（每段 ≤ mBlockSize）。早先只处理 Math.Min(count, mBlockSize)
        // 那一小段就返回，块比声明值大时后半块原样直通 —— 每 10ms 一次硬接缝合成的爆音。
        for (int done = 0; done < count; done += mBlockSize)
        {
            int n = Math.Min(mBlockSize, count - done);

            if (!ProcessChunk(buffer, offset + done * 2, n))
                return;   // 桥超时 / 死了：剩下的部分保持直通
        }
    }

    bool ProcessChunk(Span<float> buffer, int offset, int n)
    {
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
            return false;

        // 平面 → 交织。NaN / ±Inf 就地清零（见 Vst3Insert 里同一处注释）。
        for (int i = 0; i < n; i++)
        {
            float left = mOutPlanes[0][i];
            float right = mChannels > 1 ? mOutPlanes[1][i] : mOutPlanes[0][i];

            buffer[offset + i * 2] = float.IsFinite(left) ? left : 0f;
            buffer[offset + i * 2 + 1] = float.IsFinite(right) ? right : 0f;
        }

        return true;
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

        // 声明给插件的块长**不能超过共享环一块能装下的帧数**：超了 TryPublish 直接失败、
        // 那一块就白直通了（又一个"看着在跑其实没处理"的坑）。Process 里还会按这个值分段。
        int blockSize = Math.Clamp(settings.MaxBlockSize, 32, SharedAudioLayout.MaxBlockFrames);

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
        var loaded = client.LoadBlocking(slotId, plugin.Locator, settings.SampleRate, blockSize,
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

        // 乐器（effFlagsIsSynth）：插入点是"处理已经存在的音频"，乐器在这里没有意义 ——
        // 它要 MIDI 输入、要自己产声。菜单已经按扫描结果过滤掉了，这一道是防"缓存过期 / 手改工程"。
        if (loaded.IsSynth)
        {
            client.UnloadBlocking(slotId);
            reason = "this plugin is an instrument; inserts need an effect";
            return null;
        }

        // 0 进 0 出 = 纯 MIDI 工具，装进来也处理不了音频。
        if (loaded.InputChannels <= 0 || loaded.OutputChannels <= 0)
        {
            client.UnloadBlocking(slotId);
            reason = "the plugin has no audio channels (it is a MIDI utility, not an effect)";
            return null;
        }

        return new Vst2Insert(plugin, client, slotId, blockSize,
            loaded.LatencySamples, loaded.HasEditor, settings.Bypass);
    }
}
