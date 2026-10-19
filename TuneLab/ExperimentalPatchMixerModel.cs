using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Threading;
using TuneLab.Audio;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.Foundation;
using HostLog = TuneLab.Foundation.Log;

namespace TuneLab;

// [EP-MIXER-9] 混音台模型适配器：把 internal 的 Project / Track / AuxBus / AudioEngine 翻成 IMixerModel。
//
// 为什么是**新增文件**：上游不可能有同名文件 → 合并冲突恒为零。它和 ExperimentalPatchBootstrap
// 一样属于"宿主侧适配面"，不是上游代码。逐条接驳说明见 ExperimentalPatch/.agents/mixer-reconnect.md。
//
// 单一真相源：读写一律落到 DataProperty（Gain/Pan/M/S/Output/Sends），于是 undo、脚本、
// 编辑器双向同步全部免费。补丁侧不缓存任何值。
internal sealed class MixerModelAdapter : IMixerModel, IDisposable
{
    public MixerModelAdapter(Project project)
    {
        mProject = project;
        mMaster = new MixerMasterChannel(this);

        Rebuild();
        BuildOutputs();
        BuildFormats();

        mProject.Tracks.MembershipModified.Subscribe(OnMembershipModified);
        mProject.Auxes.MembershipModified.Subscribe(OnMembershipModified);
        AudioEngine.CurrentDevice.Modified.Subscribe(OnDeviceModified);
        AudioEngine.ProgressChanged += OnProgressChanged;
    }

    public IReadOnlyList<IMixerChannel> Channels => mChannels;
    public IMixerChannel Master => mMaster;
    public IReadOnlyList<IMixerOutput> Outputs => mOutputs;
    public IReadOnlyList<IMixerInsertFormat> InsertFormats => mFormats;

    // 阶段一：AUX / output / send 已接通；插入效果仍是阶段二。
    public bool SupportsAux => true;
    public bool SupportsSends => true;
    public bool SupportsInserts => false;

    public event Action? ChannelsChanged;
    public event Action? RoutingChanged;
    public event Action? MetersUpdated;

    public void AddAux()
    {
        mProject.AddAux("AUX " + (mProject.Auxes.Count + 1));
        mProject.Commit();   // 一步 undo：加 AUX 自成一个撤销单元
        BuildOutputs();
    }

    // 删除 AUX：所有指向它的 output / send 改指 Master（DESIGN.md §4.1）——静默丢路由比报错更糟。
    public void RemoveAux(IMixerChannel aux)
    {
        if (aux is not MixerAuxChannel channel)
            return;

        var bus = channel.Bus;
        int repointed = 0;

        foreach (var other in mChannels)
        {
            if (other is not MixerChannelBase source)
                continue;

            if (source.RouteId == bus.Id)
            {
                source.SetOutputRoute(MixerRoute.Master);
                repointed++;
            }

            repointed += source.RepointSends(bus.Id, MixerRoute.Master);
        }

        mProject.RemoveAux(bus);
        mProject.Commit();   // 删 AUX（含改指）自成一个撤销单元
        BuildOutputs();

        if (repointed > 0)
            HostLog.Warning($"mixer: removed AUX '{bus.Name.Value}', {repointed} route(s) now go to Master");
    }

    public void Dispose()
    {
        mProject.Tracks.MembershipModified.Unsubscribe(OnMembershipModified);
        mProject.Auxes.MembershipModified.Unsubscribe(OnMembershipModified);
        AudioEngine.CurrentDevice.Modified.Unsubscribe(OnDeviceModified);
        AudioEngine.ProgressChanged -= OnProgressChanged;

        foreach (var channel in mChannels)
            (channel as IDisposable)?.Dispose();

        mChannels.Clear();
    }

    // ── 内部：给通道用 ──

    internal IMixerOutput MasterOutput => mOutputs[0];

    internal IMixerOutput DeviceOutput => FindOutput(MixerOutputKind.Device, "device:default") ?? mOutputs[0];

    internal void RaiseRoutingChanged()
    {
        // 结构变了（output / send 增删）→ 重建混音图的边，再通知界面。
        AudioEngine.RebuildMixerGraph();
        RoutingChanged?.Invoke();
    }

    // 把 UI 给的 IMixerOutput 翻回内存里的路由值。
    internal MixerRoute RouteOf(IMixerOutput output) => new(output.Kind, output.Id, output.Name);

    // 从路由值找当前描述符（找不到就回落到 Master）。
    internal IMixerOutput Resolve(MixerRoute route) => FindOutput(route.Kind, route.Id) ?? MasterOutput;

    IMixerOutput? FindOutput(MixerOutputKind kind, string id)
    {
        foreach (var output in mOutputs)
        {
            if (output.Kind == kind && output.Id == id)
                return output;
        }

        return null;
    }

    // 成环预判：把当前所有 output + send 摊成边表，问引擎"再加 from→to 会不会成环"。
    internal bool WouldCycle(string fromId, string toId)
    {
        var edges = new List<(string From, string To)>();
        foreach (var channel in mChannels)
        {
            if (channel is not MixerChannelBase source)
                continue;

            edges.Add((source.Id, source.RouteId));
            foreach (var send in source.SendRoutes)
                edges.Add((source.Id, send.Id));
        }

        return MixGraphBuilder.WouldCreateCycle(edges, fromId, toId);
    }

    void BuildOutputs()
    {
        mOutputs.Clear();

        mOutputs.Add(new MixerOutput(MixerOutputKind.Master, "master", "Master", "Master", true));

        foreach (var aux in mProject.Auxes)
            mOutputs.Add(new MixerOutput(MixerOutputKind.Aux, aux.Id, aux.Name.Value, aux.Name.Value, true));

        string deviceName = AudioEngine.CurrentDevice.Value;
        if (string.IsNullOrWhiteSpace(deviceName))
            deviceName = "默认输出设备";

        mOutputs.Add(new MixerOutput(MixerOutputKind.Device, "device:default", deviceName, "设备", true));

        // daw_cable：菜单里保留这一类，但它是禁用项（DESIGN.md §4.5）。
        mOutputs.Add(new MixerOutput(MixerOutputKind.Cable, "cable", "（未实现）", "Cable", false));
    }

    void BuildFormats()
    {
        mFormats.Clear();
        mFormats.Add(new MixerInsertFormat("VST3", false, "尚未接入"));
        mFormats.Add(new MixerInsertFormat("VST2", false, OperatingSystem.IsWindows() ? "尚未接入" : "仅 Windows"));
        mFormats.Add(new MixerInsertFormat("JSFX", false, "尚未实现"));
    }

    void OnMembershipModified()
    {
        Rebuild();
        BuildOutputs();
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
            (channel as IDisposable)?.Dispose();

        mChannels.Clear();

        for (int i = 0; i < mProject.Tracks.Count; i++)
            mChannels.Add(new MixerTrackChannel(this, mProject.Tracks[i], i));

        foreach (var aux in mProject.Auxes)
            mChannels.Add(new MixerAuxChannel(this, aux));
    }

    readonly Project mProject;
    readonly MixerMasterChannel mMaster;
    readonly List<IMixerChannel> mChannels = [];
    readonly List<IMixerOutput> mOutputs = [];
    readonly List<IMixerInsertFormat> mFormats = [];
    int mMetersPending;
}

// 一次交互式编辑 = 一步 undo（宿主的 merge 作用域，与轨道头滑杆同一套）。
internal sealed class EditSession
{
    readonly IDataObject mTarget;
    bool mEditing;
    Head mHead;

    public EditSession(IDataObject target)
    {
        mTarget = target;
    }

    public void Begin()
    {
        if (mEditing)
            return;

        mEditing = true;
        mTarget.BeginMergeNotify();
        mHead = mTarget.Head;
    }

    public void End()
    {
        if (!mEditing)
            return;

        mEditing = false;
        mTarget.EndMergeNotify();

        if (mHead != mTarget.Head)
            mTarget.Commit();
    }

    public void Write<T>(IDataProperty<T> property, T value) where T : notnull
    {
        if (mEditing)
        {
            property.DiscardTo(mHead);
            property.Set(value);
            return;
        }

        property.Set(value);
        mTarget.Commit();
    }
}

// 轨与 AUX 的共同部分（路由 / send / 编辑会话 / 事件）。
internal abstract class MixerChannelBase : IMixerChannel, IDisposable
{
    protected MixerChannelBase(MixerModelAdapter owner, IDataObject data)
    {
        Owner = owner;
        Data = data;
        Edit = new EditSession(data);
    }

    public abstract MixerChannelKind Kind { get; }
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string ColorHex { get; }
    public abstract bool IsSelected { get; }

    public abstract double GainDb { get; set; }
    public abstract double Pan { get; set; }
    public abstract bool IsMute { get; set; }
    public abstract bool IsSolo { get; set; }

    public abstract IDataProperty<MixerRoute> OutputProperty { get; }
    public abstract DataObjectList<Send> SendList { get; }

    internal IDataObject Data { get; }

    public IMixerOutput Output
    {
        get => Owner.Resolve(OutputProperty.Value);
        set
        {
            var route = Owner.RouteOf(value);

            // 不能输出到自己，也不能成环（DESIGN.md §3.6）。
            if (route.Id == Id || Owner.WouldCycle(Id, route.Id))
            {
                HostLog.Warning($"mixer: refusing output {Id} -> {route.Id} (self or cycle)");
                return;
            }

            SetOutputRoute(route);
            // Output.Modified 会走 OnRoutingModified（重建图 + 通知界面），这里不再重复触发。
        }
    }

    public IReadOnlyList<IMixerSend> Sends
    {
        get
        {
            // 活读：undo 可能把一条 send 从列表里撤掉，缓存一份就会显示过期的东西。
            // 数量是人的尺度，重建开销可以忽略。
            var sends = new List<IMixerSend>(SendList.Count);
            foreach (var send in SendList)
                sends.Add(new MixerSendAdapter(Owner, send));

            return sends;
        }
    }

    public virtual IReadOnlyList<IMixerInsert> Inserts => [];

    public abstract (double LeftDb, double RightDb)? Peak { get; }

    public event Action? StateChanged;
    public event Action? RoutingChanged;

    public void BeginEdit() => Edit.Begin();

    public void EndEdit() => Edit.End();

    public IMixerSend AddSend(IMixerOutput target)
    {
        var route = Owner.RouteOf(target);

        if (route.Id == Id || Owner.WouldCycle(Id, route.Id))
        {
            HostLog.Warning($"mixer: refusing send {Id} -> {route.Id} (self or cycle)");
            return null!;
        }

        // 先把值填好再加进列表：此时 Send 还没接上 document，命令被丢弃，
        // 于是"加一条 send"只留一步 undo（而不是"加 + 设目的地 + 设增益"三步）。
        var send = new Send(Data);
        send.Target.SetInfo(route);
        send.Gain.SetInfo(0);
        SendList.Add(send);   // 成员变化 → OnRoutingModified（重建图 + 通知界面）
        Data.Commit();        // 一步 undo：加一条 send 自成一个撤销单元

        return new MixerSendAdapter(Owner, send);
    }

    public void RemoveSend(IMixerSend send)
    {
        if (send is not MixerSendAdapter adapter)
            return;

        SendList.Remove(adapter.Data);   // 成员变化 → OnRoutingModified
        Data.Commit();
    }

    public virtual IMixerInsert AddInsert(IMixerInsertFormat format)
        => throw new NotSupportedException("mixer inserts are not implemented yet (M6)");

    public virtual void RemoveInsert(IMixerInsert insert)
        => throw new NotSupportedException("mixer inserts are not implemented yet (M6)");

    public void Dispose()
    {
        Unsubscribe();
    }

    // ── 供适配器用 ──

    internal string RouteId => OutputProperty.Value.Id;

    internal IEnumerable<MixerRoute> SendRoutes
    {
        get
        {
            foreach (var send in SendList)
                yield return send.Target.Value;
        }
    }

    internal void SetOutputRoute(MixerRoute route) => Edit.Write(OutputProperty, route);

    // 把指向 auxId 的 send 改指到 replacement；返回改了几条。
    internal int RepointSends(string auxId, MixerRoute replacement)
    {
        int count = 0;
        foreach (var send in SendList)
        {
            if (send.Target.Value.Id != auxId)
                continue;

            send.Target.SetInfo(replacement);
            count++;
        }

        if (count > 0)
        {
            Owner.RaiseRoutingChanged();
            RoutingChanged?.Invoke();
        }

        return count;
    }

    protected MixerModelAdapter Owner { get; }
    protected EditSession Edit { get; }
    protected void RaiseStateChanged() => StateChanged?.Invoke();

    // 结构变化（output / send 增删）：重建图的边 + 让界面刷新标签。
    protected void RaiseRoutingChanged()
    {
        Owner.RaiseRoutingChanged();
        RoutingChanged?.Invoke();
    }

    protected abstract void Unsubscribe();
}

// 一条轨的通道视图。
internal sealed class MixerTrackChannel : MixerChannelBase
{
    public MixerTrackChannel(MixerModelAdapter owner, ITrack track, int index) : base(owner, track)
    {
        mTrack = track;
        mIndex = index;

        track.Gain.Modified.Subscribe(OnStateModified);
        track.Pan.Modified.Subscribe(OnStateModified);
        track.IsMute.Modified.Subscribe(OnStateModified);
        track.IsSolo.Modified.Subscribe(OnStateModified);
        track.Name.Modified.Subscribe(OnStateModified);
        track.Color.Modified.Subscribe(OnStateModified);
        track.Output.Modified.Subscribe(RaiseRoutingChanged);
        track.Sends.MembershipModified.Subscribe(RaiseRoutingChanged);
        track.SelectionChanged.Subscribe(OnStateModified);
    }

    public override MixerChannelKind Kind => MixerChannelKind.Track;

    // TODO(持久化): 换序/删轨后序号会变。落盘时会换成随工程保存的稳定 id。
    public override string Id => "track:" + mIndex;

    public override string Name => mTrack.Name.Value;
    public override string ColorHex => mTrack.Color.Value;
    public override bool IsSelected => mTrack.IsSelected;

    public override double GainDb
    {
        get => mTrack.Gain.Value;
        set => Edit.Write(mTrack.Gain, value);
    }

    public override double Pan
    {
        get => mTrack.Pan.Value;
        set => Edit.Write(mTrack.Pan, value);
    }

    public override bool IsMute
    {
        get => mTrack.IsMute.Value;
        set => Edit.Write(mTrack.IsMute, value);
    }

    public override bool IsSolo
    {
        get => mTrack.IsSolo.Value;
        set => Edit.Write(mTrack.IsSolo, value);
    }

    public override IDataProperty<MixerRoute> OutputProperty => mTrack.Output;
    public override DataObjectList<Send> SendList => ((Track)mTrack).Sends;

    public override (double LeftDb, double RightDb)? Peak
    {
        get
        {
            AudioEngine.InvokeRealtimeAmplitude(mTrack, out var amplitude);
            if (amplitude == null)
                return null;

            return (amplitude.Item1, amplitude.Item2);
        }
    }

    protected override void Unsubscribe()
    {
        mTrack.Gain.Modified.Unsubscribe(OnStateModified);
        mTrack.Pan.Modified.Unsubscribe(OnStateModified);
        mTrack.IsMute.Modified.Unsubscribe(OnStateModified);
        mTrack.IsSolo.Modified.Unsubscribe(OnStateModified);
        mTrack.Name.Modified.Unsubscribe(OnStateModified);
        mTrack.Color.Modified.Unsubscribe(OnStateModified);
        mTrack.Output.Modified.Unsubscribe(RaiseRoutingChanged);
        mTrack.Sends.MembershipModified.Unsubscribe(RaiseRoutingChanged);
        mTrack.SelectionChanged.Unsubscribe(OnStateModified);
    }

    void OnStateModified() => RaiseStateChanged();

    readonly ITrack mTrack;
    readonly int mIndex;
}

// 一条 AUX 的通道视图。与轨的差别：没有音源、没有选中态；电平表暂时没有宿主查询。
internal sealed class MixerAuxChannel : MixerChannelBase
{
    public MixerAuxChannel(MixerModelAdapter owner, AuxBus aux) : base(owner, aux)
    {
        Bus = aux;

        aux.Gain.Modified.Subscribe(OnStateModified);
        aux.Pan.Modified.Subscribe(OnStateModified);
        aux.IsMute.Modified.Subscribe(OnStateModified);
        aux.IsSolo.Modified.Subscribe(OnStateModified);
        aux.Name.Modified.Subscribe(OnStateModified);
        aux.Output.Modified.Subscribe(RaiseRoutingChanged);
        aux.Sends.MembershipModified.Subscribe(RaiseRoutingChanged);
    }

    public AuxBus Bus { get; }

    public override MixerChannelKind Kind => MixerChannelKind.Aux;

    public override string Id => Bus.Id;
    public override string Name => Bus.Name.Value;
    public override string ColorHex => string.Empty;
    public override bool IsSelected => false;

    public override double GainDb
    {
        get => Bus.Gain.Value;
        set => Edit.Write(Bus.Gain, value);
    }

    public override double Pan
    {
        get => Bus.Pan.Value;
        set => Edit.Write(Bus.Pan, value);
    }

    public override bool IsMute
    {
        get => Bus.IsMute.Value;
        set => Edit.Write(Bus.IsMute, value);
    }

    public override bool IsSolo
    {
        get => Bus.IsSolo.Value;
        set => Edit.Write(Bus.IsSolo, value);
    }

    public override IDataProperty<MixerRoute> OutputProperty => Bus.Output;
    public override DataObjectList<Send> SendList => Bus.Sends;

    // AUX 电平：走混音图的节点峰值（推子之后，DESIGN.md §3.4）。停止播放时返回 null → 表归零。
    public override (double LeftDb, double RightDb)? Peak
    {
        get
        {
            AudioEngine.InvokeRealtimeChannelAmplitude(Id, out var amplitude);
            return amplitude == null ? null : (amplitude.Item1, amplitude.Item2);
        }
    }

    protected override void Unsubscribe()
    {
        Bus.Gain.Modified.Unsubscribe(OnStateModified);
        Bus.Pan.Modified.Unsubscribe(OnStateModified);
        Bus.IsMute.Modified.Unsubscribe(OnStateModified);
        Bus.IsSolo.Modified.Unsubscribe(OnStateModified);
        Bus.Name.Modified.Unsubscribe(OnStateModified);
        Bus.Output.Modified.Unsubscribe(RaiseRoutingChanged);
        Bus.Sends.MembershipModified.Unsubscribe(RaiseRoutingChanged);
    }

    void OnStateModified() => RaiseStateChanged();
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

    // Master 电平：同一个节点查询（"master" 节点，推子之后）。
    public (double LeftDb, double RightDb)? Peak
    {
        get
        {
            AudioEngine.InvokeRealtimeChannelAmplitude("master", out var amplitude);
            return amplitude == null ? null : (amplitude.Item1, amplitude.Item2);
        }
    }

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

// 一条 send 的视图。
internal sealed class MixerSendAdapter(MixerModelAdapter model, Send send) : IMixerSend
{
    public Send Data => send;

    public IMixerOutput Target => model.Resolve(send.Target.Value);

    public double GainDb
    {
        get => send.Gain.Value;
        set => send.Gain.Set(value);
    }

    // 只在"目的地"这种结构变化时触发；推子拖动不该重建浮窗（否则拖到一半控件被换掉）。
    public event Action? Changed;
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
