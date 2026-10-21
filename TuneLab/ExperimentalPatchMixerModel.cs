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

    // 阶段一：AUX / output / send 已接通；插入效果的模型侧已接上（实例的装配见 InsertInstanceFactory）。
    public bool SupportsAux => true;
    public bool SupportsSends => true;
    public bool SupportsInserts => true;

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
    // 整段套一个 merge 作用域：**删除 + 改指 = 一步 undo**。补丁层没有对话框服务，所以不做确认框，
    // 用"可撤销"来保证安全（Ctrl+Z 会把 AUX 与全部改指一起还原）。
    public void RemoveAux(IMixerChannel aux)
    {
        if (aux is not MixerAuxChannel channel)
            return;

        var bus = channel.Bus;
        int repointed = 0;

        mProject.BeginMergeNotify();
        try
        {
            foreach (var other in mChannels)
            {
                if (other is not MixerChannelBase source)
                    continue;

                if (source.RouteId == bus.Id)
                {
                    source.SetOutputRouteRaw(MixerRoute.Master);
                    repointed++;
                }

                repointed += source.RepointSends(bus.Id, MixerRoute.Master);
            }

            mProject.RemoveAux(bus);
        }
        finally
        {
            mProject.EndMergeNotify();
            mProject.Commit();
        }

        BuildOutputs();
        RaiseRoutingChanged();

        if (repointed > 0)
            HostLog.Warning($"mixer: removed AUX '{bus.Name.Value}', {repointed} route(s) now go to Master");
    }

    // ── 通道位置（轨道头右键菜单）──
    //
    // 顺序就是工程里的顺序（Project.Tracks / Project.Auxes），所以这里改的是数据，
    // 主界面的轨道头会跟着变。两组各自独立：轨道置底只到轨道组末尾，不会排到 AUX 后面。

    public bool CanMoveChannel(IMixerChannel channel, MixerChannelMove move)
    {
        int index = GroupIndexOf(channel);
        if (index < 0)
            return false;

        int count = GroupCount(channel);

        return move switch
        {
            MixerChannelMove.Up => index > 0,
            MixerChannelMove.Down => index < count - 1,
            MixerChannelMove.Top => index > 0,
            MixerChannelMove.Bottom => index < count - 1,
            _ => false,
        };
    }

    public void MoveChannel(IMixerChannel channel, MixerChannelMove move)
    {
        int index = GroupIndexOf(channel);
        if (index < 0)
            return;

        int count = GroupCount(channel);

        int target = move switch
        {
            MixerChannelMove.Up => index - 1,
            MixerChannelMove.Down => index + 1,
            MixerChannelMove.Top => 0,
            MixerChannelMove.Bottom => count - 1,
            _ => index,
        };

        if (target < 0 || target >= count || target == index)
            return;

        MoveToIndex(channel, target);
    }

    public void MoveChannelTo(IMixerChannel channel, IMixerChannel target)
    {
        // 只在同组内：轨道不会因为"移动到某条 AUX 的位置"而跨组。
        if (channel.Kind != target.Kind)
            return;

        int from = GroupIndexOf(channel);
        int to = GroupIndexOf(target);

        if (from < 0 || to < 0 || from == to)
            return;

        MoveToIndex(channel, to);
    }

    int GroupCount(IMixerChannel channel)
        => channel.Kind == MixerChannelKind.Track ? mProject.Tracks.Count : mProject.Auxes.Count;

    int GroupIndexOf(IMixerChannel channel)
    {
        if (channel is not MixerChannelBase basis)
            return -1;

        int index = 0;

        if (channel.Kind == MixerChannelKind.Track)
        {
            foreach (var track in mProject.Tracks)
            {
                if (ReferenceEquals(track, basis.Data))
                    return index;

                index++;
            }
        }
        else
        {
            foreach (var aux in mProject.Auxes)
            {
                if (ReferenceEquals(aux, basis.Data))
                    return index;

                index++;
            }
        }

        return -1;
    }

    // 整段一个 merge 作用域：一次移动 = 一步 undo（RemoveAt + Insert 本来会记两步）。
    void MoveToIndex(IMixerChannel channel, int target)
    {
        if (channel is not MixerChannelBase basis)
            return;

        int from = GroupIndexOf(channel);
        if (from < 0 || from == target)
            return;

        mProject.BeginMergeNotify();
        try
        {
            if (channel.Kind == MixerChannelKind.Track)
            {
                var track = (ITrack)basis.Data;
                mProject.RemoveTrackAt(from);
                mProject.InsertTrack(target, track);
            }
            else
            {
                var aux = (AuxBus)basis.Data;
                mProject.RemoveAuxAt(from);
                mProject.InsertAux(target, aux);
            }
        }
        finally
        {
            mProject.EndMergeNotify();
            mProject.Commit();
        }
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

    // 插入效果器的实例工厂（宿主侧，EP-MIXER-14）。通道的 Inserts 视图要用它建实例。
    internal InsertInstanceFactory InsertFactory => sInsertFactory;

    internal void RaiseRoutingChanged()
    {
        // 结构变了（output / send 增删）→ 重建混音图的边，再通知界面。
        AudioEngine.RebuildMixerGraph();
        RoutingChanged?.Invoke();
    }

    // pin 变化会让**布局**变（进/出滚动区），所以要整表重建。
    internal void RaiseChannelsChanged() => ChannelsChanged?.Invoke();

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

        mOutputs.Add(new MixerOutput(MixerOutputKind.Master, "master", static () => "Master", static () => "Master", true));

        // 名字传**委托**而不是拷好的字符串：AUX 改名后，send 目标、output 接线菜单 / 通道条上的短名
        // 都要立刻跟着变（见 MixerOutput.Name 的注释）。
        foreach (var aux in mProject.Auxes)
        {
            var bus = aux;
            mOutputs.Add(new MixerOutput(MixerOutputKind.Aux, bus.Id, () => bus.Name.Value, () => bus.Name.Value, true));
        }

        mOutputs.Add(new MixerOutput(MixerOutputKind.Device, "device:default", DeviceName, DeviceShortName, true));

        // daw_cable：菜单里保留这一类，但它是禁用项（DESIGN.md §4.5）。
        mOutputs.Add(new MixerOutput(MixerOutputKind.Cable, "cable", static () => "（未实现）", static () => "Cable", false));
    }

    static string DeviceName()
    {
        string name = AudioEngine.CurrentDevice.Value;
        return string.IsNullOrWhiteSpace(name) ? "默认输出设备" : name;
    }

    // 通道条上那一行只有 84px：完整设备名塞不下（DESIGN.md §4.5）。
    static string DeviceShortName()
        => TuneLab.I18N.TranslationManager.CurrentLanguage.Value.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "设备"
            : "Device";

    void BuildFormats()
    {
        mFormats.Clear();

        // 可用性按后端实况：原生库在 → VST3 可用；不在 → 禁用 + 原因（界面上直接显示）。
        foreach (var backend in sInsertFactory.Backends)
        {
            mFormats.Add(new MixerInsertFormat(
                backend.FormatName,
                backend.IsAvailable,
                backend.IsAvailable ? null : backend.UnavailableReason));
        }

        // JSFX 还没接：保留条目但禁用 + 写明原因（DESIGN.md §6.3 的纪律——界面不画做不到的入口）。
        // VST2 不再在这里占位：它已经是 Backends 里的真后端（走桥进程），
        // 可用性由后端自己报，重复列一行会让菜单出现两个 VST2。
        mFormats.Add(new MixerInsertFormat("JSFX", false, "not implemented yet"));
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

    // Master 的插入链挂在工程上（Project.MasterInserts），通道视图要用它。
    internal Project Project => mProject;

    // 插入实例工厂：进程内一份。实例持有原生句柄，而适配器会随工程替换重建 ——
    // 工厂跟着适配器走就会"换一次工程全部插件重新初始化、参数回默认"。
    static readonly InsertInstanceFactory sInsertFactory = InsertInstanceFactory.Shared;

    int mMetersPending;
}

// 一个"跟着工程走"的门面：工程被替换（打开另一个工程）时，换掉里面的适配器并通知界面。
//
// 为什么需要它：补丁只拿一次 IMixerModel 并长期持有（窗口、浮窗都挂在上面）。如果直接给出
// 绑定某个 Project 实例的适配器，用户换了工程之后窗口就指着旧工程了。
// 门面自己订阅 ProjectDocument 的 ProjectHolder，所以补丁侧什么都不用做（DESIGN.md §5.5）。
internal sealed class MixerModelFacade : IMixerModel, IDisposable
{
    public MixerModelFacade(ProjectDocument document)
    {
        mDocument = document;
        mInner = new MixerModelAdapter(document.Project!);

        Hook();
        mDocument.ProjectHolder.Modified.Subscribe(OnProjectReplaced);
    }

    public IReadOnlyList<IMixerChannel> Channels => mInner.Channels;
    public IMixerChannel Master => mInner.Master;
    public IReadOnlyList<IMixerOutput> Outputs => mInner.Outputs;
    public IReadOnlyList<IMixerInsertFormat> InsertFormats => mInner.InsertFormats;

    public bool SupportsAux => mInner.SupportsAux;
    public bool SupportsSends => mInner.SupportsSends;
    public bool SupportsInserts => mInner.SupportsInserts;

    public event Action? ChannelsChanged;
    public event Action? RoutingChanged;
    public event Action? MetersUpdated;

    public void AddAux() => mInner.AddAux();
    public void RemoveAux(IMixerChannel aux) => mInner.RemoveAux(aux);

    public bool CanMoveChannel(IMixerChannel channel, MixerChannelMove move)
        => mInner.CanMoveChannel(channel, move);

    public void MoveChannel(IMixerChannel channel, MixerChannelMove move)
        => mInner.MoveChannel(channel, move);

    public void MoveChannelTo(IMixerChannel channel, IMixerChannel target)
        => mInner.MoveChannelTo(channel, target);
    public void Dispose()
    {
        mDocument.ProjectHolder.Modified.Unsubscribe(OnProjectReplaced);
        Unhook();
        mInner.Dispose();
    }

    void OnProjectReplaced()
    {
        var project = mDocument.Project;
        if (project == null)
            return;

        Unhook();
        mInner.Dispose();

        mInner = new MixerModelAdapter(project);
        Hook();

        // 整表重建：界面按 ChannelsChanged 重新取通道（窗口已经支持这条路径）。
        ChannelsChanged?.Invoke();
    }

    void Hook()
    {
        mInner.ChannelsChanged += OnChannelsChanged;
        mInner.RoutingChanged += OnRoutingChanged;
        mInner.MetersUpdated += OnMetersUpdated;
    }

    void Unhook()
    {
        mInner.ChannelsChanged -= OnChannelsChanged;
        mInner.RoutingChanged -= OnRoutingChanged;
        mInner.MetersUpdated -= OnMetersUpdated;
    }

    void OnChannelsChanged() => ChannelsChanged?.Invoke();
    void OnRoutingChanged() => RoutingChanged?.Invoke();
    void OnMetersUpdated() => MetersUpdated?.Invoke();

    readonly ProjectDocument mDocument;
    MixerModelAdapter mInner;
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

    // 名字可写：双击就地改名（写回 Name 属性，一步 undo）。
    public string Name
    {
        get => NameProperty.Value;
        set => Edit.Write(NameProperty, value);
    }

    public abstract string ColorHex { get; }
    public abstract bool IsSelected { get; }

    public abstract IDataProperty<string> NameProperty { get; }
    public abstract IDataProperty<MixerPin> PinProperty { get; }

    // 固定到窗口左/右（不随横向滚动）。
    public MixerPin Pin
    {
        get => PinProperty.Value;
        set => Edit.Write(PinProperty, value);
    }

    public abstract double GainDb { get; set; }
    public abstract double Pan { get; set; }
    public abstract bool IsMute { get; set; }
    public abstract bool IsSolo { get; set; }

    public abstract IDataProperty<MixerRoute> OutputProperty { get; }
    public abstract DataObjectList<Send> SendList { get; }
    public abstract DataObjectList<InsertSlot> InsertList { get; }

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

    // 与 Output 同一个判据（不能到自己、不能成环），只是**问**而不是**改**：
    // send 菜单用它筛目标（点了会被拒绝的条目不列出来）。
    public bool CanSendTo(IMixerOutput target)
    {
        var route = Owner.RouteOf(target);
        return route.Id != Id && !Owner.WouldCycle(Id, route.Id);
    }

    // 串联插入（按顺序处理）。活读：增删 / 旁通要立刻反映在浮窗里。
    public virtual IReadOnlyList<IMixerInsert> Inserts
    {
        get
        {
            var list = new List<IMixerInsert>(InsertList.Count);
            foreach (var slot in InsertList)
                list.Add(new InsertSlotAdapter(slot, Owner.InsertFactory, Owner.RaiseRoutingChanged));

            return list;
        }
    }

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
    {
        // 加在末尾。换序留到 M8（▲▼ / 拖拽），v1 不做。
        var slot = new InsertSlot(
            "insert-" + Guid.NewGuid().ToString("N"),
            format.Name,
            string.Empty,
            string.Empty,
            (DataObject)Data);

        InsertList.Add(slot);   // 成员变化 → RaiseRoutingChanged（重建图 + 通知界面）
        Data.Commit();          // 一步 undo：加一个槽位自成一个撤销单元

        return new InsertSlotAdapter(slot, Owner.InsertFactory, Owner.RaiseRoutingChanged);
    }

    public virtual void RemoveInsert(IMixerInsert insert)
    {
        if (insert is not InsertSlotAdapter adapter)
            return;

        // 先放掉原生实例（插件句柄不是 DataObject 该持有的东西，见 InsertInstanceFactory 的注释），
        // 再从列表里摘。顺序反了会出现"图里还指着它、而它已经没了"的窗口。
        Owner.InsertFactory.Release(adapter.SlotId);
        InsertList.Remove(adapter.Slot);
        Data.Commit();
    }

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

    // 不带提交的版本：批量改指时用，整段由调用方套 merge 作用域（否则每条路由各留一步 undo）。
    internal void SetOutputRouteRaw(MixerRoute route) => OutputProperty.SetInfo(route);

    // 把指向 auxId 的 send 改指到 replacement；返回改了几条。
    // 不在这里通知/重建：调用方（RemoveAux）整段做完再统一做一次。
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
        track.Inserts.MembershipModified.Subscribe(RaiseRoutingChanged);
        track.Pinned.Modified.Subscribe(OnPinChanged);
        track.SelectionChanged.Subscribe(OnStateModified);
    }

    public override MixerChannelKind Kind => MixerChannelKind.Track;

    // TODO(持久化): 换序/删轨后序号会变。落盘时会换成随工程保存的稳定 id。
    public override string Id => "track:" + mIndex;

    public override IDataProperty<string> NameProperty => mTrack.Name;
    public override IDataProperty<MixerPin> PinProperty => mTrack.Pinned;
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
    public override DataObjectList<InsertSlot> InsertList => ((Track)mTrack).Inserts;

    public override (double LeftDb, double RightDb)? Peak
    {
        get
        {
            // 图节点（推子 / 声像之后）—— 不是音源本身，否则表不随推子与声像动。
            AudioEngine.InvokeRealtimeTrackAmplitude(mTrack, out var amplitude);
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
        mTrack.Inserts.MembershipModified.Unsubscribe(RaiseRoutingChanged);
        mTrack.Pinned.Modified.Unsubscribe(OnPinChanged);
        mTrack.SelectionChanged.Unsubscribe(OnStateModified);
    }

    void OnStateModified() => RaiseStateChanged();

    void OnPinChanged() => Owner.RaiseChannelsChanged();

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
        // 改名不是本通道自己的事：别的轨的 send 目标行、所有 output 接线菜单与通道条短名都在显示这个名字。
        // 所以走"整表重建"（不动音频图），而不是只刷新自己那一条。
        aux.Name.Modified.Subscribe(OnNameModified);
        aux.Output.Modified.Subscribe(RaiseRoutingChanged);
        aux.Sends.MembershipModified.Subscribe(RaiseRoutingChanged);
        aux.Inserts.MembershipModified.Subscribe(RaiseRoutingChanged);
        aux.Pinned.Modified.Subscribe(OnPinChanged);
    }

    public AuxBus Bus { get; }

    public override MixerChannelKind Kind => MixerChannelKind.Aux;

    public override string Id => Bus.Id;

    public override IDataProperty<string> NameProperty => Bus.Name;
    public override IDataProperty<MixerPin> PinProperty => Bus.Pinned;
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
    public override DataObjectList<InsertSlot> InsertList => Bus.Inserts;

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
        Bus.Name.Modified.Unsubscribe(OnNameModified);
        Bus.Output.Modified.Unsubscribe(RaiseRoutingChanged);
        Bus.Sends.MembershipModified.Unsubscribe(RaiseRoutingChanged);
        Bus.Inserts.MembershipModified.Unsubscribe(RaiseRoutingChanged);
        Bus.Pinned.Modified.Unsubscribe(OnPinChanged);
    }

    void OnStateModified() => RaiseStateChanged();

    void OnNameModified() => Owner.RaiseChannelsChanged();

    void OnPinChanged() => Owner.RaiseChannelsChanged();
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
    public string Name
    {
        get => "MASTER";
        set { }   // Master 不能改名
    }

    public MixerPin Pin
    {
        get => MixerPin.None;
        set { }   // Master 本来就不滚动，不需要固定
    }
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

    // Master 的插入链：与轨道 / AUX 同一套适配器，只是槽位挂在工程上（Project.MasterInserts）。
    public IReadOnlyList<IMixerInsert> Inserts
    {
        get
        {
            var list = new List<IMixerInsert>(mOwner.Project.MasterInserts.Count);

            foreach (var slot in mOwner.Project.MasterInserts)
                list.Add(new InsertSlotAdapter(slot, mOwner.InsertFactory, mOwner.RaiseRoutingChanged));

            return list;
        }
    }

    // Master 也能挂插入（母带链，DESIGN.md §6.3）—— 只是它的槽位不在 Project 里，
    // 所以宿主侧还没处可放。现在返回空表、AddInsert 说明原因，而不是画一个假按钮。
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
    public bool CanSendTo(IMixerOutput target) => false;

    // Master 的插入链挂在哪儿还是 TODO：它不在 Project.Auxes 里，需要一个等价的位置。
    // 在此之前如实说"不支持"，比让用户点出一个异常好。
    // Master 是"总轨"：效果器挂在这里就是总线效果。槽位与轨道 / AUX 完全同构，
    // 只是父对象是 Project（所以 undo / 落盘跟着工程走）。
    public IMixerInsert AddInsert(IMixerInsertFormat format)
    {
        var project = mOwner.Project;

        var slot = new InsertSlot(
            "insert-" + Guid.NewGuid().ToString("N"),
            format.Name,
            string.Empty,
            string.Empty,
            project.MasterInserts);

        project.MasterInserts.Add(slot);
        project.Commit();

        // 成员变化没有经过 MembershipModified 的订阅（Master 不在 Tracks/Auxes 里），
        // 所以这里自己重建图 + 通知界面。
        mOwner.RaiseRoutingChanged();

        return new InsertSlotAdapter(slot, mOwner.InsertFactory, mOwner.RaiseRoutingChanged);
    }

    public void RemoveInsert(IMixerInsert insert)
    {
        if (insert is not InsertSlotAdapter adapter)
            return;

        var project = mOwner.Project;

        mOwner.InsertFactory.Release(adapter.SlotId);
        project.MasterInserts.Remove(adapter.Slot);
        project.Commit();

        mOwner.RaiseRoutingChanged();
    }

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

internal sealed class MixerOutput(MixerOutputKind kind, string id, Func<string> name, Func<string> shortName, bool isAvailable) : IMixerOutput
{
    public MixerOutputKind Kind => kind;
    public string Id => id;

    // 名字是**活读**的，不是构造时拷下来的字符串：AUX 一改名，别处显示这个名字的地方
    // （send 目标行、output 接线菜单、通道条上的短名）必须立刻跟上。
    // 早先它在 BuildOutputs 时把名字拷成 string，于是改名只改了 AUX 自己那一条。
    public string Name => name();
    public string ShortName => shortName();
    public bool IsAvailable => isAvailable;
}

internal sealed class MixerInsertFormat(string name, bool isAvailable, string? unavailableReason) : IMixerInsertFormat
{
    public string Name => name;
    public bool IsAvailable => isAvailable;
    public string? UnavailableReason => unavailableReason;
}






