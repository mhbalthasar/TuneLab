using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Threading;
using TuneLab.Audio;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.Foundation;
using HostLog = TuneLab.Foundation.Log;

namespace TuneLab;

// [EP-MIXER-9] 混音台模型适配器：把 internal 的 Project / Track / AudioEngine 翻成 IMixerModel。
//
// 为什么是**新增文件**：上游不可能有同名文件 → 合并冲突恒为零。它和 ExperimentalPatchBootstrap
// 一样属于"宿主侧适配面"，不是上游代码。逐条接驳说明见 ExperimentalPatch/.agents/mixer-reconnect.md。
//
// 单一真相源：所有读写都直接落到 Track 的 DataProperty（Gain/Pan/IsMute/IsSolo/Name/Color），
// 所以 undo、序列化、脚本、编辑器双向同步全部免费。补丁侧不缓存任何值。
internal sealed class MixerModelAdapter : IMixerModel, IDisposable
{
    public MixerModelAdapter(Project project)
    {
        mProject = project;
        mMaster = new MixerMasterChannel(this);

        BuildOutputs();
        BuildFormats();
        Rebuild();

        mProject.Tracks.MembershipModified.Subscribe(OnMembershipModified);
        AudioEngine.CurrentDevice.Modified.Subscribe(OnDeviceModified);
        AudioEngine.ProgressChanged += OnProgressChanged;
    }

    public IReadOnlyList<IMixerChannel> Channels => mChannels;
    public IMixerChannel Master => mMaster;
    public IReadOnlyList<IMixerOutput> Outputs => mOutputs;
    public IReadOnlyList<IMixerInsertFormat> InsertFormats => mFormats;

    public event Action? ChannelsChanged;
    public event Action? RoutingChanged;
    public event Action? MetersUpdated;

    // AUX 在 M4 接入。现在明确地"没有"，而不是假装成功。
    public void AddAux() => HostLog.Warning("mixer: AUX buses are not implemented yet (M4)");

    public void RemoveAux(IMixerChannel aux) => HostLog.Warning("mixer: AUX buses are not implemented yet (M4)");

    public void Dispose()
    {
        mProject.Tracks.MembershipModified.Unsubscribe(OnMembershipModified);
        AudioEngine.CurrentDevice.Modified.Unsubscribe(OnDeviceModified);
        AudioEngine.ProgressChanged -= OnProgressChanged;

        foreach (var channel in mChannels)
            channel.Dispose();
        mChannels.Clear();
    }

    // ── 内部：供通道用 ──

    internal IMixerOutput MasterOutput => mOutputs[0];
    internal IMixerOutput DeviceOutput => mOutputs[1];

    internal void RaiseRoutingChanged() => RoutingChanged?.Invoke();

    void BuildOutputs()
    {
        mOutputs.Clear();

        string deviceName = AudioEngine.CurrentDevice.Value;
        if (string.IsNullOrWhiteSpace(deviceName))
            deviceName = "默认输出设备";

        mOutputs.Add(new MixerOutput(MixerOutputKind.Master, "master", "Master", "Master", true));
        mOutputs.Add(new MixerOutput(MixerOutputKind.Device, "device:default", deviceName, "设备", true));
        // daw_cable：菜单里保留这一类，但它是禁用项（DESIGN.md §4.5）。
        mOutputs.Add(new MixerOutput(MixerOutputKind.Cable, "cable", "（未实现）", "Cable", false));
    }

    void BuildFormats()
    {
        mFormats.Clear();
        // 插入效果是阶段二（M6 起）。现在三类都不可用，且写明原因 —— 菜单里禁用并显示原因。
        mFormats.Add(new MixerInsertFormat("VST3", false, "尚未接入"));
        mFormats.Add(new MixerInsertFormat("VST2", false, OperatingSystem.IsWindows() ? "尚未接入" : "仅 Windows"));
        mFormats.Add(new MixerInsertFormat("JSFX", false, "尚未实现"));
    }

    void OnMembershipModified()
    {
        Rebuild();
        ChannelsChanged?.Invoke();
    }

    void OnDeviceModified()
    {
        BuildOutputs();
        RoutingChanged?.Invoke();
    }

    // 电平：播放中每帧一次（宿主 ProgressChanged 来自音频线程，必须回到 UI 线程再通知补丁）。
    void OnProgressChanged()
    {
        if (Interlocked.Exchange(ref mMetersPending, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref mMetersPending, 0);
            MetersUpdated?.Invoke();
        }, DispatcherPriority.Background);
    }

    void Rebuild()
    {
        foreach (var channel in mChannels)
            channel.Dispose();

        mChannels.Clear();

        for (int i = 0; i < mProject.Tracks.Count; i++)
            mChannels.Add(new MixerTrackChannel(this, mProject.Tracks[i], i));
    }

    readonly Project mProject;
    readonly MixerMasterChannel mMaster;
    readonly List<MixerTrackChannel> mChannels = [];
    readonly List<IMixerOutput> mOutputs = [];
    readonly List<IMixerInsertFormat> mFormats = [];
    int mMetersPending;
}

// 一条轨的通道视图。
internal sealed class MixerTrackChannel : IMixerChannel, IDisposable
{
    public MixerTrackChannel(MixerModelAdapter owner, ITrack track, int index)
    {
        mOwner = owner;
        mTrack = track;
        mIndex = index;
        mOutput = owner.MasterOutput;   // 默认输出 = Master（DESIGN.md §4.2）

        track.Gain.Modified.Subscribe(OnStateModified);
        track.Pan.Modified.Subscribe(OnStateModified);
        track.IsMute.Modified.Subscribe(OnStateModified);
        track.IsSolo.Modified.Subscribe(OnStateModified);
        track.Name.Modified.Subscribe(OnStateModified);
        track.Color.Modified.Subscribe(OnStateModified);
        track.SelectionChanged.Subscribe(OnStateModified);
    }

    public MixerChannelKind Kind => MixerChannelKind.Track;

    // TODO(M4): 换序/删轨后序号会变。等 AUX/路由落地时改成稳定 id（随工程序列化）。
    public string Id => "track:" + mIndex;

    public string Name => mTrack.Name.Value;

    public string ColorHex => mTrack.Color.Value;

    public bool IsSelected => mTrack.IsSelected;

    public double GainDb
    {
        get => mTrack.Gain.Value;
        set => Write(mTrack.Gain, value);
    }

    public double Pan
    {
        get => mTrack.Pan.Value;
        set => Write(mTrack.Pan, value);
    }

    public bool IsMute
    {
        get => mTrack.IsMute.Value;
        set => Write(mTrack.IsMute, value);
    }

    public bool IsSolo
    {
        get => mTrack.IsSolo.Value;
        set => Write(mTrack.IsSolo, value);
    }

    // M4 接入路由前，所有轨都直接去 Master。
    public IMixerOutput Output
    {
        get => mOutput;
        set
        {
            if (value.Kind is not (MixerOutputKind.Master or MixerOutputKind.Device))
                return;

            mOutput = value;
            mOwner.RaiseRoutingChanged();
        }
    }

    // M4 / M6 接入。
    public IReadOnlyList<IMixerSend> Sends => [];

    public IReadOnlyList<IMixerInsert> Inserts => [];

    public (double LeftDb, double RightDb)? Peak
    {
        get
        {
            AudioEngine.InvokeRealtimeAmplitude(mTrack, out var amplitude);
            if (amplitude == null)
                return null;

            return (amplitude.Item1, amplitude.Item2);
        }
    }

    public event Action? StateChanged;
    public event Action? RoutingChanged;

    public void BeginEdit()
    {
        if (mEditing)
            return;

        mEditing = true;
        mTrack.BeginMergeNotify();
        mHead = mTrack.Head;
    }

    public void EndEdit()
    {
        if (!mEditing)
            return;

        mEditing = false;
        mTrack.EndMergeNotify();

        if (mHead != mTrack.Head)
            mTrack.Commit();
    }

    public IMixerSend AddSend(IMixerOutput target) => throw new NotSupportedException("mixer sends are not implemented yet (M4)");

    public void RemoveSend(IMixerSend send) => throw new NotSupportedException("mixer sends are not implemented yet (M4)");

    public IMixerInsert AddInsert(IMixerInsertFormat format) => throw new NotSupportedException("mixer inserts are not implemented yet (M6)");

    public void RemoveInsert(IMixerInsert insert) => throw new NotSupportedException("mixer inserts are not implemented yet (M6)");

    public void Dispose()
    {
        mTrack.Gain.Modified.Unsubscribe(OnStateModified);
        mTrack.Pan.Modified.Unsubscribe(OnStateModified);
        mTrack.IsMute.Modified.Unsubscribe(OnStateModified);
        mTrack.IsSolo.Modified.Unsubscribe(OnStateModified);
        mTrack.Name.Modified.Unsubscribe(OnStateModified);
        mTrack.Color.Modified.Unsubscribe(OnStateModified);
        mTrack.SelectionChanged.Unsubscribe(OnStateModified);
    }

    // 编辑会话里：先把上一次中间态回退掉，再写新值 —— 整段拖动因此只留一条命令。
    // 不在会话里：一次写入 = 一步 undo（写完就 Commit，否则命令停在"未提交"区，Undo 看不到它）。
    void Write<T>(IDataProperty<T> property, T value) where T : notnull
    {
        if (mEditing)
        {
            property.DiscardTo(mHead);
            property.Set(value);
            return;
        }

        property.Set(value);
        mTrack.Commit();
    }

    void OnStateModified() => StateChanged?.Invoke();

    readonly MixerModelAdapter mOwner;
    readonly ITrack mTrack;
    readonly int mIndex;
    IMixerOutput mOutput = null!;
    bool mEditing;
    Head mHead;
}

// Master 条：推子就是 AudioEngine.MasterGain（不是 DataProperty → 不进 undo、不序列化）。
internal sealed class MixerMasterChannel : IMixerChannel
{
    public MixerMasterChannel(MixerModelAdapter owner)
    {
        mOwner = owner;
    }

    public MixerChannelKind Kind => MixerChannelKind.Master;

    public string Id => "master";

    public string Name => "MASTER";

    public string ColorHex => string.Empty;

    public bool IsSelected => false;

    public double GainDb
    {
        get => AudioEngine.MasterGain;
        set => AudioEngine.MasterGain = value;
    }

    // Master 没有 pan / M / S（DESIGN.md §4.1）。
    public double Pan
    {
        get => 0;
        set { }
    }

    public bool IsMute
    {
        get => false;
        set { }
    }

    public bool IsSolo
    {
        get => false;
        set { }
    }

    public IMixerOutput Output
    {
        get => mOwner.DeviceOutput;
        set
        {
            if (value.Kind is MixerOutputKind.Device or MixerOutputKind.Cable)
                mOwner.RaiseRoutingChanged();
        }
    }

    public IReadOnlyList<IMixerSend> Sends => [];

    public IReadOnlyList<IMixerInsert> Inserts => [];

    // Master 电平查询是 M4 的宿主改动（§6 第 5 项）。在那之前表是黑的，不编数据。
    public (double LeftDb, double RightDb)? Peak => null;

    public event Action? StateChanged;
    public event Action? RoutingChanged;

    public void BeginEdit() { }

    public void EndEdit() { }

    public IMixerSend AddSend(IMixerOutput target) => throw new NotSupportedException("Master cannot have sends");

    public void RemoveSend(IMixerSend send) => throw new NotSupportedException("Master cannot have sends");

    public IMixerInsert AddInsert(IMixerInsertFormat format) => throw new NotSupportedException("mixer inserts are not implemented yet (M6)");

    public void RemoveInsert(IMixerInsert insert) => throw new NotSupportedException("mixer inserts are not implemented yet (M6)");

    readonly MixerModelAdapter mOwner;
}

internal sealed class MixerOutput(MixerOutputKind kind, string id, string name, string shortName, bool isAvailable) : IMixerOutput
{
    public MixerOutputKind Kind => kind;
    public string Id => id;
    public string Name => name;
    public string ShortName => shortName;
    public bool IsAvailable => isAvailable;
}

internal sealed class MixerInsertFormat(string name, bool isAvailable, string? unavailableReason) : IMixerInsertFormat
{
    public string Name => name;
    public bool IsAvailable => isAvailable;
    public string? UnavailableReason => unavailableReason;
}
