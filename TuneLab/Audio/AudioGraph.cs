using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.Foundation;

namespace TuneLab.Audio;

internal class AudioGraph()
{
    public int SampleRate 
    {
        get => mSampleRate;
        set
        {
            mSampleRate = value;
            mMixer.Limiter.SampleRate = value;
            lock (mTrackLockObject)
            {
                foreach (var track in mTracks)
                {
                    foreach (var audioSource in track.AudioSources)
                    {
                        audioSource.OnSampleRateChanged();
                    }
                }
            }
        }
    }

    public IReadOnlyCollection<IAudioTrack> Tracks => mTracks;

    public void AddTrack(IAudioTrack track)
    {
        lock (mTrackLockObject)
        {
            mTracks.Add(track);
            RebuildSnapshot();
        }
    }

    public void RemoveTrack(IAudioTrack track)
    {
        lock (mTrackLockObject)
        {
            mTracks.Remove(track);
            RebuildSnapshot();
        }
    }

    // [EP-MIXER-5] AUX 总线：与轨并列的结构成员。
    public void AddAux(AuxBus aux)
    {
        lock (mTrackLockObject)
        {
            mAuxes.Add(aux);
            RebuildSnapshot();
        }
    }

    public void RemoveAux(AuxBus aux)
    {
        lock (mTrackLockObject)
        {
            mAuxes.Remove(aux);
            RebuildSnapshot();
        }
    }

    public void AddData(IAudioTrack track, int position, int endPosition, bool isStereo, float[] buffer, int offset)
    {
        double volume = track.Volume;
        double pan = track.Pan;
        AddData(track, position, endPosition, isStereo, buffer, offset,
            (float)(volume * (1 - pan)), (float)(volume * (1 + pan)));
    }

    // [EP-MIXER-1] 供混音图按"节点算好的左右增益"取数据。
    // 增益留在**逐段**循环里，而不是先求和再整体乘 —— 否则与旧路径的浮点结果不再逐位相同
    // （DESIGN.md §7.2 的硬验收要求）。见 ExperimentalPatch/.agents/mixer-reconnect.md。
    public void AddData(IAudioTrack track, int position, int endPosition, bool isStereo, float[] buffer, int offset,
        float leftVolume, float rightVolume)
    {
        foreach (var audioSource in track.AudioSources)
        {
            int audioSourceStart = (int)(audioSource.StartTime * SampleRate);
            int audioSourceEnd = audioSourceStart + audioSource.SampleCount;
            if (audioSourceEnd < position)
                continue;

            if (audioSourceStart > endPosition)
                break;

            int start = Math.Max(position, audioSourceStart);
            int end = Math.Min(endPosition, audioSourceEnd);
            if (start == end)
                continue;

            var audioData = audioSource.GetAudioData(start - audioSourceStart, end - start);
            if (isStereo)
            {
                for (int i = start; i < end; i++)
                {
                    buffer[2 * (i - position) + offset] += leftVolume * audioData.GetLeft(i - start);
                    buffer[2 * (i - position) + offset + 1] += rightVolume * audioData.GetRight(i - start);
                }
            }
            else
            {
                for (int i = start; i < end; i++)
                {
                    buffer[i - position + offset] += (leftVolume * audioData.GetLeft(i - start) + rightVolume * audioData.GetRight(i - start)) / 2;
                }
            }
        }
    }

    // [EP-MIXER-1] 立体声走混音图；单声道导出暂时仍走旧的逐轨累加
    // （下混语义与图里的"先求和再除 2"不是逐位相同，等接入插入时一起处理）。
    public void MixData(int position, int endPosition, bool isStereo, float[] buffer, int offset, IMixSource? masterInput = null)
    {
        if (isStereo && mMixer.HasSnapshot)
        {
            mCompiler.RefreshSolo();
            mMixer.Render(position, endPosition - position, buffer, offset, masterInput);
            return;
        }

        MixDataLegacy(position, endPosition, isStereo, buffer, offset);

        // 走旧路径时补上"预览音 + Master 增益"（与改动前的播放回调同序）。
        // 只有播放路径会传 masterInput；导出不传，故导出语义不受影响。
        if (isStereo && masterInput != null)
            ApplyPreviewAndMasterGain(endPosition - position, buffer, offset, masterInput);

        // [EP-MIXER-10] 旧路径（单声道导出 / 图建失败时的兜底）也要限幅，
        // 否则"单声道导出"与"设备输出"的听感会不一致。
        if (mMixer.LimiterEnabled)
            mMixer.Limiter.Process(buffer, offset, endPosition - position, isStereo ? 2 : 1);
    }

    void MixDataLegacy(int position, int endPosition, bool isStereo, float[] buffer, int offset)
    {
        lock (mTrackLockObject)
        {
            bool hasSolo = false;
            foreach (var track in mTracks)
            {
                if (track.IsSolo)
                {
                    hasSolo = true;
                    break;
                }
            }

            foreach (var track in mTracks)
            {
                if (!track.IsSolo && (track.IsMute || hasSolo))
                    continue;

                AddData(track, position, endPosition, isStereo, buffer, offset);
            }
        }
    }

    public double EndTime
    {
        get
        {
            // 与 AddTrack / RemoveTrack / MixData 同一把锁。这里原先是本类里唯一不加锁的枚举，
            // 于是「渲染期间加一条轨」能让它当场抛 Collection was modified。
            lock (mTrackLockObject)
            {
                double endTime = 0;
                foreach (var track in mTracks)
                {
                    endTime = Math.Max(endTime, track.EndTime);
                }
                endTime += 1;
                return endTime;
            }
        }
    }

    // [EP-MIXER-1] 结构变化（加删轨 / 加删 AUX / 改路由）才重建快照；推子/声像/静音是活值，每块现读。
    internal void RebuildMixerGraph()
    {
        lock (mTrackLockObject)
        {
            RebuildSnapshot();
        }
    }

    void RebuildSnapshot()
    {
        try
        {
            mMixer.SetSnapshot(mCompiler.Compile(this, mTracks, mAuxes));
        }
        catch (Exception ex)
        {
            // 图建不出来（理论上不该发生）→ 退回旧路径，至少还能出声。
            mMixer.SetSnapshot(null);
            Log.Error("failed to build mixer graph: " + ex);
        }
    }

    // [EP-MIXER-3] 未播放时也要有预览音 + Master 增益（旧路径的这段逻辑在播放回调里，
    // 但它在 IsPlaying 之外，所以不能随图一起被跳过）。
    public void MixIdle(int count, float[] buffer, int offset, IMixSource masterInput)
        => ApplyPreviewAndMasterGain(count, buffer, offset, masterInput);

    // [EP-MIXER-5] 节点电平（AUX / Master 的表用）。
    public bool TryGetChannelPeak(string channelId, out double leftDb, out double rightDb)
        => mMixer.TryGetPeakDb(channelId, out leftDb, out rightDb);

    void ApplyPreviewAndMasterGain(int count, float[] buffer, int offset, IMixSource masterInput)
    {
        masterInput.AddData(0, count, buffer, offset, 1f, 1f);

        float volume = (float)MusicTheory.dB2Level(AudioEngine.MasterGain);
        if (volume == 1f)
            return;

        for (int i = 0; i < count * 2; i++)
            buffer[offset + i] *= volume;
    }

    int mSampleRate = 44100;
    List<IAudioTrack> mTracks = [];
    readonly List<AuxBus> mAuxes = [];

    readonly object mTrackLockObject = new();

    readonly Mixer mMixer = new() { LimiterEnabled = true };
    readonly MixerGraphCompiler mCompiler = new();
}
