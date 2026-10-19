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

    public MixSnapshot Compile(AudioGraph graph, IReadOnlyCollection<IAudioTrack> tracks, IReadOnlyCollection<AuxBus> auxes)
    {
        var trackList = tracks.ToList();
        var auxList = auxes.ToList();

        var trackStates = new List<TrackChannelState>(trackList.Count);
        var auxStates = new List<AuxChannelState>(auxList.Count);

        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new MasterChannelState());

        // v1 只有一个设备：所有"设备"输出都进同一个 sink（DESIGN.md §4.4）。
        var device = builder.AddNode("device", MixNodeKind.Device);

        var nodes = new Dictionary<string, MixGraphBuilder.NodeDraft>();

        foreach (var aux in auxList)
        {
            var state = new AuxChannelState(aux);
            auxStates.Add(state);
            nodes[aux.Id] = builder.AddNode(aux.Id, MixNodeKind.Aux, state);
        }

        for (int i = 0; i < trackList.Count; i++)
        {
            var track = trackList[i];
            var state = new TrackChannelState(track);
            trackStates.Add(state);

            string id = "track-" + i;
            nodes[id] = builder.AddNode(id, MixNodeKind.Track, state, new TrackMixSource(graph, track));
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
                builder.Connect(node, Resolve(send.Target.Value), 1f, 0, new SendEdgeGain(send));
        }

        foreach (var aux in auxList)
        {
            var node = nodes[aux.Id];

            builder.Connect(node, Resolve(aux.Output.Value));

            foreach (var send in aux.Sends)
                builder.Connect(node, Resolve(send.Target.Value), 1f, 0, new SendEdgeGain(send));
        }

        var snapshot = builder.Build();

        mTrackStates = trackStates.ToArray();
        mAuxStates = auxStates.ToArray();
        return snapshot;
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

// 一条 send 的活增益：读 Send.Gain（dB）现换算成线性。
internal sealed class SendEdgeGain(Send send) : IMixEdgeGain
{
    public float Gain => (float)MusicTheory.dB2Level(send.Gain.Value);
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

    public void AddData(int position, int count, float[] buffer, int offset, float leftGain, float rightGain)
        => mGraph.AddData(mTrack, position, position + count, true, buffer, offset, leftGain, rightGain);
}

// Master 的推子就是 AudioEngine.MasterGain（dB → 线性）。读活值，不缓存。
internal sealed class MasterChannelState : IMixChannelState
{
    public double Volume => MusicTheory.dB2Level(AudioEngine.MasterGain);

    public double Pan => 0;

    public bool IsSilent => false;
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
