using System;
using System.Collections.Generic;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.Foundation;

namespace TuneLab.Audio;

// [EP-MIXER-9] 宿主侧的「模型 → 混音图」编译器。
//
// 位置说明：本文件是**新增**的宿主文件（上游不会有同名文件），所以合并冲突恒为零；
// 与 ExperimentalPatchBootstrap 一样，它属于补丁层的宿主侧适配面，而不是上游代码。
// 逐条接驳说明见 ExperimentalPatch/.agents/mixer-reconnect.md。
//
// 职责边界：引擎不认识 Track；这里把每条轨翻成一个 Track 节点（带 IMixSource + IMixChannelState），
// 再连到唯一的 Master 节点。M3 只有"轨 → Master"这一种拓扑；AUX / send / 设备出口在 M4 加进来。
internal sealed class MixerGraphCompiler
{
    // 音频线程每块读它，UI 线程重建图时整份替换 —— 所以是"不可变数组 + 原子换引用"。
    // 用可变 List 的话，加轨时正在渲染的音频线程会撞上 List 的内部状态。
    volatile TrackChannelState[] mStates = [];

    public MixSnapshot Compile(AudioGraph graph, IReadOnlyCollection<IAudioTrack> tracks)
    {
        var states = new List<TrackChannelState>(tracks.Count);
        var builder = new MixGraphBuilder();
        var master = builder.AddNode("master", MixNodeKind.Master, new MasterChannelState());

        int index = 0;
        foreach (var track in tracks)
        {
            var state = new TrackChannelState(track);
            states.Add(state);

            var node = builder.AddNode("track-" + index, MixNodeKind.Track, state, new TrackMixSource(graph, track));
            builder.Connect(node, master, 1f);
            index++;
        }

        var snapshot = builder.Build();
        mStates = states.ToArray();
        return snapshot;
    }

    // 每个块开头刷新一次 solo 判据：引擎只问"这条静不静"，不猜宿主语义。
    public void RefreshSolo()
    {
        var states = mStates;

        bool hasSolo = false;
        foreach (var state in states)
        {
            if (state.IsSolo)
            {
                hasSolo = true;
                break;
            }
        }

        foreach (var state in states)
            state.HasSolo = hasSolo;
    }
}

// 一条轨的活状态：直接读 Track，不做缓存（单一真相源）。
internal sealed class TrackChannelState : IMixChannelState
{
    readonly IAudioTrack mTrack;

    public TrackChannelState(IAudioTrack track)
    {
        mTrack = track;
    }

    // 由 RefreshSolo 每块写一次（同一条轨的所有状态共享同一个值）。
    public bool HasSolo { get; set; }

    public bool IsSolo => mTrack.IsSolo;

    public double Volume => mTrack.Volume;

    public double Pan => mTrack.Pan;

    // 与旧路径 MixData 的判据**逐字一致**：`!IsSolo && (IsMute || hasSolo)` 才跳过。
    // 注意上游的既有语义：solo 会盖过同一条轨的 mute（solo 时不看 IsMute）——照抄，不"修正"。
    public bool IsSilent => !mTrack.IsSolo && (mTrack.IsMute || HasSolo);
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
