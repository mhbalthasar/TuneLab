namespace TuneLab.ExperimentalPatch.InsertHost;

// 一个**已经建好、后来在运行期坏了**的插入的面：音频路已经放弃（桥在 process 里卡死）。
//
// 【与建实例失败的区别】建实例失败发生在建图那一刻，由 InsertInstanceFactory 记进
// mFailures；这里说的是"实例是好的，跑着跑着音频停了" —— 只有活着的实例自己知道。
// 两个来源在工厂里合流，界面看到的都是同一个 IMixerInsert.FailureReason。
public interface IRuntimeInsertFailure
{
    // 运行期音频挂起的显示原因。界面把它放进那一行的 tooltip。
    // 与其它失败原因同口径写英文（日志 / 异常消息一律英文，见 AGENTS.md）。
    public const string AudioStallReason =
        "the plugin stopped responding; its audio was bypassed (the bridge was stalled)";

    /// <summary>运行期失败原因；null = 正常。只在界面线程读。</summary>
    string? RuntimeFailureReason { get; }
}
