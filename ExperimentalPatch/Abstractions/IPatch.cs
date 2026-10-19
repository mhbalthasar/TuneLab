namespace TuneLab.ExperimentalPatch.Abstractions;

// 一个补丁 = 一个 IPatch 实现 + 一个 [Patch] 特性。
// 放哪：ExperimentalPatch/Patches/<你的功能>/XxxPatch.cs。加了就会被发现，不需要改任何注册表。
public interface IPatch
{
    // 稳定且唯一。用于日志归属、失败报告与排序兜底。约定：反向域名或 小组件名.功能名。
    string Id { get; }

    // 订阅锚点、注册补丁自己的东西。抛异常会被 PatchHost 兜住并记日志，不影响其它补丁与主程序。
    void Attach(IAnchorTable anchors);

    // 卸载：解掉 Attach 里做的非订阅型副作用。订阅型（IAnchorTable 返回的 IDisposable）由
    // FunctionHooker 统一回收，这里不必重复。宿主退出时调用一次。
    void Detach();
}
