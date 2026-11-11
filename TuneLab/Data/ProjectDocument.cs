using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TuneLab.Audio;
using TuneLab.Foundation;
using TuneLab.I18N;

namespace TuneLab.Data;

internal class ProjectDocument : DataDocument
{
    public IActionEvent ProjectNameChanged => mProjectNameChanged;
    public IHolder<Project> ProjectHolder => mProject;
    public Project? Project => mProject;
    public string Name => mName;
    public bool IsSaved => mLastSavedHead == Head;
    public string Path => mPath;
    // 非空 = 当前工程来自崩溃恢复，且它当时所在的文件仍然存在 → 可以「存回原位」。
    // 恢复本身不绑这个路径（见 SetRecovered），要不要覆盖原文件由用户显式决定。
    public string? RecoveredOriginalPath => mRecoveredOriginalPath;
    public ProjectDocument() 
    {
        mLastSavedHead = Head;
        mProject.WillModify.Subscribe(() =>
        {
            // [EP-MIXER-19] 换工程：先把 Master 插入从音频图摘下来，新工程随后挂上自己那份。
            // [EP-MIXER-28] Master Ø 与插入链同寿：先换引用，再让 SetMasterInserts 那次重建
            // 把新的 MasterChannelState 建出来。
            AudioEngine.SetMasterPhaseInvert(null);
            AudioEngine.SetMasterInserts(null);
            AudioEngine.SetMasterOutput(null);

            // [EP-MIXER-AUTO] 时间换算一并摘掉（订阅、换算器、"当前 tick"提供者都随工程走）。
            UnwireAutomationTimeMapper();

            Project?.Detach();
            Project?.Dispose();
        });

        mProject.Modified.Subscribe(() =>
        {
            Project?.Attach(this);

            // [EP-MIXER-19] Master 插入跟随**当前文档的工程**，而不是"最后一次 new 出来的 Project"。
            // 以前接在 Project 构造/析构里，于是两件常事都会把它变成 null：
            //   1. 打开另一个工程：新 Project 先构造（挂上自己的），旧 Project 再 Dispose（清空）；
            //   2. 导入轨道：TrackScrollView 为了拿 TempoManager 临时 new 一个 Project，直接把指针抢走。
            // 指针一空，master 节点就没有插入实例，Master 轨 FX 的「界面」键永远是灰的。
            AudioEngine.SetMasterPhaseInvert(Project?.MasterPhaseInvert);
            AudioEngine.SetMasterInserts(Project?.MasterInserts);
            AudioEngine.SetMasterOutput(Project?.MasterOutput);

            // [EP-MIXER-AUTO] 换算器 + "当前 tick"提供者跟随当前工程；tempo / 采样率变化时重建。
            WireAutomationTimeMapper();
        });

        mProject.When(project => project.Tracks.ItemAdded).Subscribe(track =>
        {
            var dir = AudioPartBaseDirectory();
            if (string.IsNullOrEmpty(dir))
                return;

            foreach (var audioPart in track.Parts.OfType<IAudioPart>())
            {
                audioPart.BaseDirectory.Value = dir;
                ApplyBaseHref(audioPart);
            }
        });

        mProject.When(project => project.Tracks.WhenAny(track => track.Parts.ItemAdded)).Subscribe(part =>
        {
            if (part is not IAudioPart audioPart)
                return;

            var dir = AudioPartBaseDirectory();
            if (string.IsNullOrEmpty(dir))
            {
                // 没有默认基准时仍要把 basehref 交给 part：崩溃恢复可能只有 basehref。
                ApplyBaseHref(audioPart);
                return;
            }

            audioPart.BaseDirectory.Value = dir;
            ApplyBaseHref(audioPart);
        });
    }

    public void SetProject(Project project, string path = "")
    {
        Clear();
        mProject.Set(project);
        SetSavePath(path);
    }

    public void SetSavePath(string path)
    {
        mPath = path;
        // 有了真实保存路径就回到"从保存路径推基准目录"，清掉显式来源——否则内存里的解析基准会与
        // "把这个文件重新打开一次"所得的基准不一致，成为一处看不见的分歧。
        mBaseDirectoryOverride = null;
        mBaseHrefOverride = null;
        mRecoveredOriginalPath = null;
        ResetAudioPartBaseDirectory();
        mName = File.Exists(path) ? new FileInfo(path).Name : "Untitled Project".Tr(TC.Document);
        mLastSavedHead = Head;
        mProjectNameChanged?.Invoke();
    }

    // [EP-MIXER-32] 崩溃恢复专用：工程【保持未保存态】（无保存路径，故一次 Ctrl+S 不会覆盖原文件），
    // 但展示名与解析基准来自自动保存的元数据。originalPath 为空（工程从未保存过）时不设基准，
    // 相对音频引用无法解析——那是"未保存工程没有基准目录"的必然结果，如实降级不猜测。
    public void SetRecovered(string displayName, string? originalPath, string? baseHref = null)
    {
        mPath = string.Empty;
        mRecoveredOriginalPath = File.Exists(originalPath) ? originalPath : null;
        mBaseDirectoryOverride = mRecoveredOriginalPath == null
            ? null
            : System.IO.Path.GetDirectoryName(mRecoveredOriginalPath);
        // 自动保存工程自己带的基准目录（原工程目录）。sidecar 元数据丢失时，这是
        // 相对音频引用唯一的落脚点；与默认基准不同时才作为兜底使用。
        mBaseHrefOverride = string.IsNullOrEmpty(baseHref) ? null : baseHref;
        ResetAudioPartBaseDirectory();
        mName = displayName;
        mLastSavedHead = Head;
        mProjectNameChanged?.Invoke();
    }

    // 「保存路径」与「基准目录」是两回事：崩溃恢复就是"有基准、无保存路径"的合法组合。
    // 取值规则 = 显式来源优先，缺省才从保存路径推。
    string? AudioPartBaseDirectory()
    {
        if (Project == null)
            return null;

        return mBaseDirectoryOverride ?? System.IO.Path.GetDirectoryName(mPath);
    }

    void ResetAudioPartBaseDirectory()
    {
        if (Project == null)
            return;

        var dir = AudioPartBaseDirectory();

        foreach (var audioPart in Project.AllAudioParts())
        {
            if (!string.IsNullOrEmpty(dir))
                audioPart.BaseDirectory.Value = dir;

            ApplyBaseHref(audioPart);
        }
    }

    // [EP-MIXER-32] basehref 只对内部 AudioPart 生效（IAudioPart 是冻结的 SDK 面，不加成员）。
    // 保存到新路径时必须清空：旧 basehref 属于"恢复态"，留着会让相对路径继续指向原目录。
    void ApplyBaseHref(IAudioPart audioPart)
    {
        if (audioPart is AudioPart concrete)
            concrete.BaseHref.Value = mBaseHrefOverride ?? string.Empty;
    }

    string mPath = string.Empty;
    string mName = string.Empty;
    string? mBaseDirectoryOverride = null;
    string? mBaseHrefOverride = null;
    string? mRecoveredOriginalPath = null;
    Head mLastSavedHead;
    readonly Holder<Project> mProject = new();
    readonly ActionEvent mProjectNameChanged = new();

    // [EP-MIXER-AUTO] 插入参数自动化的时间接线（tempo / 采样率订阅）。
    readonly DisposableManager mTimeSubscriptions = new();

    void WireAutomationTimeMapper()
    {
        UnwireAutomationTimeMapper();

        var project = Project;
        if (project == null)
            return;

        void Refresh()
        {
            AudioEngine.SetTimeMapper(MixerAutomation.CreateTimeMapper(project));
            InsertInstanceFactory.AutomationTickProvider =
                () => project.TempoManager.GetTick(AudioEngine.CurrentTime);
        }

        Refresh();

        // tempo 任何变化（拖动 / 增删 / undo）都重建：换算器持的是不可变快照，
        // 重建 = 换一个引用，音频线程无感切到新的一份。
        project.TempoManager.Modified.Subscribe(Refresh, mTimeSubscriptions);
        AudioEngine.SampleRate.Modified.Subscribe(Refresh, mTimeSubscriptions);
    }

    void UnwireAutomationTimeMapper()
    {
        mTimeSubscriptions.DisposeAll();

        AudioEngine.SetTimeMapper(null);
        InsertInstanceFactory.AutomationTickProvider = null;
    }
}
