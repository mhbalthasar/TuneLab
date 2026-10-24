using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TuneLab.Audio;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace TuneLab.Data;

internal interface ITrack : IDataObject<TrackInfo>, IAudioTrack, ISelectable
{
    IProject Project { get; }
    ITempoManager TempoManager { get; }
    ITimeSignatureManager TimeSignatureManager { get; }
    IDataProperty<string> Name { get; }
    new IDataProperty<bool> IsMute { get; }
    new IDataProperty<bool> IsSolo { get; }

    // [EP-MIXER-25] Solo 安全：别人 solo 时不被压制。
    new IDataProperty<bool> IsSoloSafe { get; }

    // [EP-MIXER-26] 所属 VCA 的 id（空 = 不属于任何 VCA）。
    new IDataProperty<string> VcaId { get; }
    IDataProperty<bool> AsRefer { get; }
    IDataProperty<double> Gain { get; }
    new IDataProperty<double> Pan { get; }
    IDataProperty<string> Color { get; }
    new IDataProperty<MixerRoute> Output { get; }
    new IReadOnlyDataObjectList<Send> Sends { get; }
    new IReadOnlyDataObjectList<InsertSlot> Inserts { get; }

    // [EP-MIXER-20] 输入级（混音台通道条）：Trim / 相位 / 高通。
    new IDataProperty<double> TrimDb { get; }
    new IDataProperty<bool> PhaseInvert { get; }
    new IDataProperty<double> HpfHz { get; }

    IDataProperty<MixerPin> Pinned { get; }    bool ExportEnabled { get; set; }
    int ExportChannels { get; set; }
    IReadOnlyDataObjectLinkedList<IPart> Parts { get; }

    void InsertPart(IPart part);
    bool RemovePart(IPart part);
    // 同轨内重排（改 pos/dur）：摘除→跑 mutate→按新键重插。跨轨迁移属换父，走 RemovePart/InsertPart。
    void MovePart(IPart part, Action mutate);
    MidiPart CreatePart(MidiPartInfo info);
    AudioPart CreatePart(AudioPartInfo info);
    Part CreatePart(PartInfo info);

    void Activate();
    void Deactivate();

    bool IAudioTrack.IsMute => IsMute.Value;
    bool IAudioTrack.IsSolo => IsSolo.Value;

    // [EP-MIXER-25] 默认 false = 普通通道（会被别人的 solo 压制）。
    bool IAudioTrack.IsSoloSafe => IsSoloSafe.Value;
    string IAudioTrack.VcaId => VcaId.Value;
    double IAudioTrack.Volume => MusicTheory.dB2Level(Gain.Value);
    double IAudioTrack.Pan => Pan.Value;

    // [EP-MIXER-20] 输入级：DIM 把它们接到数据槽位上（Track 只提供 DataProperty）。
    double IAudioTrack.TrimDb => TrimDb.Value;
    bool IAudioTrack.PhaseInvert => PhaseInvert.Value;
    double IAudioTrack.HpfHz => HpfHz.Value;
    double IAudioTrack.EndTime => Parts.MaxBy(part => part.EndTime())?.EndTime() ?? 0;
    IEnumerable<IAudioSource> IAudioTrack.AudioSources => Parts;

    MixerRoute IAudioTrack.Output => Output.Value;
    IReadOnlyList<Send> IAudioTrack.Sends => Sends;
}


