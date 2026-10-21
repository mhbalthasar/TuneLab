using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TuneLab.Audio;
using TuneLab.Extensions.Formats.TLP;
using TuneLab.Foundation;
using TuneLab.Utils;
using TuneLab.SDK;

namespace TuneLab.Data;

internal class Project : DataObject, IProject
{
    public ITempoManager TempoManager => mTempoManager;
    public ITimeSignatureManager TimeSignatureManager => mTimeSignatureManager;
    public IReadOnlyDataObjectList<ITrack> Tracks => mTracks;

    // [EP-MIXER-19] Master 总线上的插入效果（总轨效果）。与 AUX 的 Inserts 同构。
    public DataObjectList<InsertSlot> MasterInserts { get; }

    // AUX 总线（DESIGN.md §4.1）。**暂不落盘**：见 §9 与 Patches/Mixer/DESIGN.md 的说明——
    // 打开另一个工程时这里会被清空（它们属于上一个工程），UI 上有明确提示。
    public IReadOnlyDataObjectList<AuxBus> Auxes => mAuxes;
    public string ExportPath { get; set; } = string.Empty;
    public string ExportFileName { get; set; } = string.Empty;
    public string ExportFormat { get; set; } = "wav";
    public int ExportSampleRate { get; set; } = 44100;
    public int ExportBitDepth { get; set; } = 16;
    public int ExportBitrate { get; set; } = 320;
    public bool MasterExportEnabled { get; set; } = true;
    public int MasterExportChannels { get; set; } = 2;

    public Project() : this(new ProjectInfo()) { }
    public Project(ProjectInfo info)
    {
        mTimeSignatureManager = new(this);
        mTempoManager = new(this);
        mTracks = new(this);
        mAuxes = new(this);

        // [EP-MIXER-19] Master 的插入链：Master 是"总轨"，效果器挂在它上面 = 总线效果。
        // 数据挂在 Project 上（与 Tracks/Auxes 同级），所以 undo / 落盘都跟着工程走。
        MasterInserts = new DataObjectList<InsertSlot>(this);
        AudioEngine.SetMasterInserts(MasterInserts);
        mTracks.ItemAdded.Subscribe(OnTrackAdded);
        mTracks.ItemRemoved.Subscribe(OnTrackRemoved);
        mAuxes.ItemAdded.Subscribe(OnAuxAdded);
        mAuxes.ItemRemoved.Subscribe(OnAuxRemoved);

        SetInfo(info);
    }

    public ProjectInfo GetInfo()
    {
        ProjectInfo info = new();

        info.Tempos = mTempoManager.GetInfo();

        for (int i = 0; i < mTimeSignatureManager.TimeSignatures.Count; i++)
        {
            var timeSignature = mTimeSignatureManager.TimeSignatures[i];
            info.TimeSignatures.Add(new TimeSignatureInfo
            {
                BarIndex = timeSignature.BarIndex,
                Numerator = timeSignature.Numerator,
                Denominator = timeSignature.Denominator,
            });
        }

        info.Tracks = mTracks.GetInfo().ToInfo();

        return info;
    }

    public void SetInfo(ProjectInfo info)
    {
        using var _ = MergeNotify();
        mTempoManager.SetInfo(info.Tempos);
        mTimeSignatureManager.SetInfo(info.TimeSignatures);
        mTracks.SetInfo(info.Tracks.Convert(CreateTrack).ToArray());

        // AUX 暂不随工程保存：装载新工程时必须清掉（它们属于上一个工程，留着就是错的数据）。
        if (mAuxes.Count > 0)
            mAuxes.SetInfo([]);
    }

    // AUX 的增删。id 只在本进程内唯一（暂不落盘，所以不需要跨会话稳定）。
    public AuxBus AddAux(string name) => AddAuxWithId("aux-" + ++mAuxSeq, name);

    // 落盘恢复用：按保存下来的 id 重建 —— 路由引用的是 id，换号就等于把路由全弄丢。
    public AuxBus AddAuxWithId(string id, string name)
    {
        if (id.StartsWith("aux-", StringComparison.Ordinal) && int.TryParse(id[4..], out int number) && number > mAuxSeq)
            mAuxSeq = number;   // 之后 AddAux 生成的号不会与恢复出来的撞车

        var aux = new AuxBus(id, name, this);
        mAuxes.Add(aux);
        return aux;
    }

    public bool RemoveAux(AuxBus aux) => mAuxes.Remove(aux);

    // [EP-MIXER-5] AUX 的位置调整（混音台轨道头右键）。与轨道同一套原语：
    // RemoveAt + Insert 会走列表的增删通知，AudioEngine 的 AUX 集合跟着变，
    // 混音图因此重建 —— 顺序变了，节点的下标也会变，必须重建。
    public void RemoveAuxAt(int index)
    {
        if ((uint)index >= mAuxes.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        mAuxes.RemoveAt(index);
    }

    public void InsertAux(int index, AuxBus aux)
    {
        if ((uint)index > mAuxes.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        mAuxes.Insert(index, aux);
    }
    void OnAuxAdded(AuxBus aux) => AudioEngine.AddAux(aux);

    void OnAuxRemoved(AuxBus aux) => AudioEngine.RemoveAux(aux);

    public void AddTrack(TrackInfo info)
    {
        mTracks.Add(CreateTrack(info));
    }

    public bool RemoveTrack(ITrack track)
    {
        return mTracks.Remove(track);
    }

    // 越界抛而非静默 no-op：按 .NET 惯例，按下标寻址的成员里"下标非法"是编程错误而非正常情形——
    // 这也正是 RemoveAt 无返回值的前提（Remove(item) 才需要 bool 表达"它原本在不在"）。
    // 宽容版会把 bug 藏起来：调用方既拿不到返回值、也收不到异常，彻底无从得知什么都没发生。
    public void RemoveTrackAt(int trackIndex)
    {
        if ((uint)trackIndex >= mTracks.Count)
            throw new ArgumentOutOfRangeException(nameof(trackIndex));

        mTracks.RemoveAt(trackIndex);
    }

    // 插入的合法下标含末位（== Count 即追加）。
    public void InsertTrack(int trackIndex, ITrack track)
    {
        if ((uint)trackIndex > mTracks.Count)
            throw new ArgumentOutOfRangeException(nameof(trackIndex));

        mTracks.Insert(trackIndex, track);
    }

    public Track CreateTrack(TrackInfo info)
    {
        return new Track(this, info);
    }

    ITrack IProject.CreateTrack(TrackInfo info) => CreateTrack(info);

    void OnTrackAdded(ITrack track)
    {
        track.Activate();
    }

    void OnTrackRemoved(ITrack track)
    {
        track.Deactivate();
    }

    public void Dispose()
    {
        // 工程没了：Master 的插入链也从音频图摘掉（否则下一个工程会用上旧槽位）。
        AudioEngine.SetMasterInserts(null);
        foreach (var track in mTracks)
        {
            track.Deactivate();
        }

        // [EP-MIXER-5] AUX 也必须从音频图里摘掉。
        //
        // AudioGraph 是**进程级**的（AudioEngine 的静态成员），而 AUX 的 id 每个工程都从
        // aux-1 开始。只 Deactivate 轨道、不摘 AUX 的话，切一次工程图里就多留一组旧的
        // aux-1/aux-2…；新工程的 aux-1 与它撞 id，MixGraphBuilder.AddNode 抛
        // "duplicate node id"，整张图编译失败 → 退回旧混音路径 ——
        // 表现就是"工程里的 AUX 接线全失效、声音直接进 Master"，而且每改一次接线都要
        // 再编译一次、再失败一次（看起来像卡死）。
        foreach (var aux in mAuxes)
        {
            AudioEngine.RemoveAux(aux);
        }
    }

    TempoManager mTempoManager;
    TimeSignatureManager mTimeSignatureManager;
    readonly DataObjectList<ITrack> mTracks;
    readonly DataObjectList<AuxBus> mAuxes;
    int mAuxSeq;
}






