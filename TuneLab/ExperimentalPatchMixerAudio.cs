using System;
using System.Collections.Generic;
using System.Linq;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.Foundation;
using HostLog = TuneLab.Foundation.Log;

namespace TuneLab.Audio;

// [EP-MIXER-9] 宿主侧的「模型 → 混音图」编译器。
//
// 位置说明：本文件是**新增**的宿主文件（上游不会有同名文件），所以合并冲突恒为零；
// 与 ExperimentalPatchBootstrap 一样，它属于补丁层的宿主侧适配面，而不是上游代码。
// 逐条接驳说明见 ExperimentalPatch/.agents/mixer-reconnect.md。
//
// 职责边界：引擎不认识 Track / AuxBus；这里把每条轨、每条 AUX 翻成节点，
// 再按 output + send 连边（每条 send 一条边，增益是**活值**——拖 send 推子不重建图）。
internal sealed class MixerGraphCompiler
{
    // 音频线程每块读它，UI 线程重建图时整份替换 —— 不可变数组 + 原子换引用。
    volatile TrackChannelState[] mTrackStates = [];
    volatile AuxChannelState[] mAuxStates = [];

    public MixSnapshot Compile(AudioGraph graph, IReadOnlyCollection<IAudioTrack> tracks,
        IReadOnlyCollection<AuxBus> auxes, IReadOnlyList<InsertSlot>? masterInserts = null)
    {
        // Master 是"总轨"：它的插入链挂在 master 节点上（DESIGN §5.7.18）。
        // 没有就传 null，行为与以前完全一致。
        // 插件的**装配参数**必须跟着当前图走，且每次编译都刷一遍：
        //   · 块长：插件被喂的块长必须 ≥ 引擎实际交出来的块长。早先这里没人设，
        //     工厂停在默认 512，而引擎按 1024（设备缓冲）交块 —— 于是每块只处理了前 512 帧、
        //     后 512 帧原样直通。听感就是"音频线接触不良"那种爆音（每 10ms 一次硬接缝）。
        //   · 采样率：早先也停在默认 48000。工程是 44.1k 时插件按 48k 跑，音高与时值全错。
        sInsertFactory.SampleRate = graph.SampleRate;
        sInsertFactory.MaxBlockSize = Mixer.DefaultMaxBlockSize;

        var trackList = tracks.ToList();
        var auxList = auxes.ToList();

        var trackStates = new List<TrackChannelState>(trackList.Count);
        var auxStates = new List<AuxChannelState>(auxList.Count);

        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new MasterChannelState(),
            null, BuildInserts("master", masterInserts ?? []));

        // v1 只有一个设备：所有"设备"输出都进同一个 sink（DESIGN.md §4.4）。
        var device = builder.AddNode("device", MixNodeKind.Device);

        var nodes = new Dictionary<string, MixGraphBuilder.NodeDraft>();

        foreach (var aux in auxList)
        {
            // 防御：进程级的 AudioGraph 里若还留着上一个工程的 AUX（id 都从 aux-1 起），
            // 这里会撞 id。撞了只跳过这一条并记日志，绝不让整张图编译失败 ——
            // 一张图建不出来会退回旧混音路径，等于"所有接线突然失效"，比丢一条 AUX 严重得多。
            if (nodes.ContainsKey(aux.Id))
            {
                HostLog.Warning($"mixer: duplicate aux id '{aux.Id}' (stale bus from a previous project?); skipped");
                continue;
            }

            var state = new AuxChannelState(aux);
            auxStates.Add(state);
            nodes[aux.Id] = builder.AddNode(aux.Id, MixNodeKind.Aux, state, null,
                BuildInserts(aux.Id, aux.Inserts));
        }

        for (int i = 0; i < trackList.Count; i++)
        {
            var track = trackList[i];
            var state = new TrackChannelState(track);
            trackStates.Add(state);

            string id = "track-" + i;
            nodes[id] = builder.AddNode(id, MixNodeKind.Track, state, new TrackMixSource(graph, track),
                BuildInserts(id, track.Inserts));
        }

        // 目的地解析：AUX 按 id 找节点；设备进 device sink；Cable 未实现 → 回落到 Master 并记日志。
        MixGraphBuilder.NodeDraft Resolve(MixerRoute route)
        {
            switch (route.Kind)
            {
                case MixerOutputKind.Aux when nodes.TryGetValue(route.Id, out var aux):
                    return aux;

                case MixerOutputKind.Device:
                    return device;

                case MixerOutputKind.Cable:
                    HostLog.Warning("mixer: cable output is not implemented yet; routing to Master instead");
                    return master;

                default:
                    return master;
            }
        }

        for (int i = 0; i < trackList.Count; i++)
        {
            var track = trackList[i];
            var node = nodes["track-" + i];

            builder.Connect(node, Resolve(track.Output));

            foreach (var send in track.Sends)
                builder.Connect(node, Resolve(send.Target.Value), 1f, 0, new SendEdgeGain(send), send.IsPreFader.Value);
        }

        foreach (var aux in auxList)
        {
            var node = nodes[aux.Id];

            builder.Connect(node, Resolve(aux.Output.Value));

            foreach (var send in aux.Sends)
                builder.Connect(node, Resolve(send.Target.Value), 1f, 0, new SendEdgeGain(send), send.IsPreFader.Value);
        }

        var snapshot = builder.Build();

        mTrackStates = trackStates.ToArray();
        mAuxStates = auxStates.ToArray();
        return snapshot;
    }

    // 把一条通道的数据槽位变成引擎的 IMixInsert 链（EP-MIXER-14）。
    //
    // 三条纪律：
    //   ① 建不出来的槽位**不能**让整张图失败 —— 换成"直通"占位，并记日志。
    //      一个插件卸了就让混音台整张图建不起来，比丢一个效果器严重得多。
    //   ② 顺序就是数据里的顺序（= 界面看到的顺序），别在这里重排。
    //   ③ 实例的释放归工厂：这里只负责"这次编译用了哪些"。
    static List<IMixInsert> BuildInserts(string channelId, IReadOnlyList<InsertSlot> slots)
    {
        if (slots.Count == 0)
            return [];

        // 与落盘（MixerPersistence）共用同一个工厂：图里跑的那个实例，
        // 就是保存工程时采 state 的那个。各建一份会采到空。
        var factory = sInsertFactory;

        var chain = new List<IMixInsert>(slots.Count);

        foreach (var slot in slots)
        {
            if (slot.IsBypassed.Value)
            {
                // 旁通的槽位连实例都不建：插件白加载一次只是浪费，
                // 而"旁通时不碰插件"还顺手避开了"旁通时插件还在跑"的 CPU 开销。
                chain.Add(new BypassedInsert(slot.Name.Value, slot.Format));
                continue;
            }

            var insert = sInsertFactory.Create(slot, out var reason);

            if (insert == null)
            {
                HostLog.Warning($"mixer: insert '{slot.Name.Value}' on {channelId} is unavailable ({reason}); passing through");
                chain.Add(new BypassedInsert(slot.Name.Value, slot.Format));
                continue;
            }

            chain.Add(insert);
        }

        return chain;
    }

    // 与 MixerGraphCompiler 共用同一个工厂：图里跑的那个实例，就是落盘时采 state 的那个。
    static readonly InsertInstanceFactory sInsertFactory = InsertInstanceFactory.Shared;

    /// <summary>占位插入：不做事，只是让链的长度与界面显示的一致。</summary>
    sealed class BypassedInsert(string name, string format) : IMixInsert
    {
        public string Name => name;
        public bool IsBypassed => true;
        public int LatencySamples => 0;

        // 永远失败的占位（插件建不出来时用到）。没有"不旁通"这种状态。
        public void SetBypassed(bool bypassed) { }

        public void Process(Span<float> buffer, int offset, int count)
        {
            // 直通：什么都不做。放在这里而不是让引擎跳过，链的长度因此是稳定的。
        }
    }

    // 每个块开头刷新一次 solo 判据。**两个独立域**（DESIGN.md §3.3）：
    // 轨道 solo 只压未 solo 的轨；AUX solo 只压未 solo 的 AUX。互不影响，也不影响 Master。
    public void RefreshSolo()
    {
        var tracks = mTrackStates;
        var auxes = mAuxStates;

        bool trackSolo = false;
        foreach (var state in tracks)
        {
            if (state.IsSolo)
            {
                trackSolo = true;
                break;
            }
        }

        bool auxSolo = false;
        foreach (var state in auxes)
        {
            if (state.IsSolo)
            {
                auxSolo = true;
                break;
            }
        }

        foreach (var state in tracks)
            state.HasSolo = trackSolo;

        foreach (var state in auxes)
            state.HasSolo = auxSolo;
    }
}

// 一条 send 的活增益：读 Send 的四个字段现算（dB → 线性、声向、静音）。
//
// 为什么左右分开算：send 可以有自己的声向（与通道条那个无关）。同一路送进两个 AUX 时，
// 一个偏左一个偏右，不必拆两条轨道。公式与通道条同构：l = v(1−p)、r = v(1+p)，
// 这样 pan 在正中央时左右都是 v，与"没有声向"逐位相同。
internal sealed class SendEdgeGain(Send send) : IMixEdgeGain
{
    public float Gain => (float)MusicTheory.dB2Level(send.Gain.Value);

    public float LeftGain
    {
        get
        {
            if (send.IsMute.Value)
                return 0f;

            return (float)(MusicTheory.dB2Level(send.Gain.Value) * (1 - send.Pan.Value));
        }
    }

    public float RightGain
    {
        get
        {
            if (send.IsMute.Value)
                return 0f;

            return (float)(MusicTheory.dB2Level(send.Gain.Value) * (1 + send.Pan.Value));
        }
    }

    // 静音直接跳过整条边 —— 不给目标贡献任何东西，连"写 0"都不做。
    public bool IsSilent => send.IsMute.Value;
}

// 一条轨的活状态：直接读 Track，不做缓存（单一真相源）。
internal sealed class TrackChannelState : IMixChannelState
{
    readonly IAudioTrack mTrack;

    public TrackChannelState(IAudioTrack track)
    {
        mTrack = track;
    }

    // 由 RefreshSolo 每块写一次。
    public bool HasSolo { get; set; }

    public bool IsSolo => mTrack.IsSolo;

    public double Volume => mTrack.Volume;

    public double Pan => mTrack.Pan;

    // 与旧路径 MixData 的判据**逐字一致**：`!IsSolo && (IsMute || hasSolo)` 才跳过。
    // 注意上游的既有语义：solo 会盖过同一条轨的 mute（solo 时不看 IsMute）——照抄，不"修正"。
    public bool IsSilent => !mTrack.IsSolo && (mTrack.IsMute || HasSolo);

    // 输入级：读的是数据槽位（活值，不重建图）。
    public double TrimDb => mTrack.TrimDb;
    public bool PhaseInvert => mTrack.PhaseInvert;
    public double HpfHz => mTrack.HpfHz;
}

// 一条 AUX 的活状态。
internal sealed class AuxChannelState : IMixChannelState
{
    readonly AuxBus mAux;

    public AuxChannelState(AuxBus aux)
    {
        mAux = aux;
    }

    public bool HasSolo { get; set; }

    public bool IsSolo => mAux.IsSolo.Value;

    public double Volume => MusicTheory.dB2Level(mAux.Gain.Value);

    public double Pan => mAux.Pan.Value;

    public bool IsSilent => !mAux.IsSolo.Value && (mAux.IsMute.Value || HasSolo);

    public double TrimDb => mAux.TrimDb.Value;
    public bool PhaseInvert => mAux.PhaseInvert.Value;
    public double HpfHz => mAux.HpfHz.Value;
}

// 把 Track 的音源按节点算好的左右增益喂给引擎。
internal sealed class TrackMixSource : IMixSource
{
    readonly AudioGraph mGraph;
    readonly IAudioTrack mTrack;

    public TrackMixSource(AudioGraph graph, IAudioTrack track)
    {
        mGraph = graph;
        mTrack = track;
    }

    // 【为什么 seekOffset 已经不用管】引擎把偏移后的 position 传进来（Mixer.RenderChunk 里
    // 那一处），源侧按收到的 position 直接读。补偿只发生一次 —— 在引擎里。
    public void AddData(int position, int count, float[] buffer, int offset, float leftGain, float rightGain)
        => mGraph.AddData(mTrack, position, position + count, true, buffer, offset, leftGain, rightGain);
}

// Master 的推子就是 AudioEngine.MasterGain（dB → 线性）。读活值，不缓存。
internal sealed class MasterChannelState : IMixChannelState
{
    public double Volume => MusicTheory.dB2Level(AudioEngine.MasterGain);

    public double Pan => 0;

    public bool IsSilent => false;

    // Master 没有输入级（没有音源可 Trim / 高通），三项恒为默认值 ——
    // 引擎因此整段跳过，行为与以前完全一致。
    public double TrimDb => 0;
    public bool PhaseInvert => false;
    public double HpfHz => 0;
}

// 预览音（AudioPlayer 的一次性片段）作为"直接混进 Master 输入"的音源。
// 它已经自带增益，故忽略引擎给的左右增益。
internal sealed class PreviewMixSource : IMixSource
{
    readonly AudioPlayer mPlayer;

    public PreviewMixSource(AudioPlayer player)
    {
        mPlayer = player;
    }

    public void AddData(int position, int count, float[] buffer, int offset, float leftGain, float rightGain)
        => mPlayer.AddData(count, buffer, offset);
}







