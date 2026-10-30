using TuneLab.Extensions.Formats.TLP;

namespace TuneLab.Data;

// 崩溃恢复：把自动保存的 native 工程套回【已经存在】的 Project。
//
// 普通打开会 new Project，再让 ProjectDocument 切过去；恢复不能换对象（编辑器、音频图、
// 文档路径都挂在当前实例上），所以只能 SetInfo。两条路径的装载内容必须一致，否则就会
// 出现"普通打开有混音路由、恢复出来却没有"的分叉——epMixer 正是在这里漏过一次。
internal static class ProjectRecovery
{
    public static void Apply(Project project, NativeProjectFile file, string fallbackProjectPath)
    {
        project.SetInfo(file.Project);
        project.SetExportConfig(file.Export);

        // 自动保存的 .tlpx 同样带 epMixer。文件内载荷优先；老自动保存没有这个键时，
        // 退回到 fallbackProjectPath 的 sidecar（通常传自动保存记录里的原工程路径）。
        MixerPersistence.Load(project, fallbackProjectPath, file.EpMixer);
    }
}
