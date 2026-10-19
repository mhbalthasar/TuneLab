namespace TuneLab.ExperimentalPatch.Abstractions;

// 锚点 id：稳定标识，只用于日志 / 诊断 / 约定文档。
// 【改实现不改 id】——补丁与宿主各自独立演进，id 一旦写进日志就是排查时的抓手。
public static class AnchorIds
{
    public const string AppStartup = "app.startup";
    public const string AppReady = "app.ready";
    public const string AppShutdown = "app.shutdown";
}
