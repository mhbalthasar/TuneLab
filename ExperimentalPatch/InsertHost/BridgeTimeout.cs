using System;
using System.Threading.Tasks;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 等桥回一条消息时的统一上限。
//
// 【为什么必须有】桥那边的插件是第三方 native 代码，我们**没法从托管侧打断**它：
// 它可能在 Create / process / state / effClose 里死循环、弹模态框、等一个永远不来的
// 外部资源。实测里 CHOWTapeModel / ChowMatrix 就卡在 Create，BYOD / ChowCentaur
// 卡在建第二个实例。没有上限的话，宿主 UI 线程会在 .GetAwaiter().GetResult() 上
// 永远停住 —— 用户看到的是 TuneLab 死掉，比进程内直载还糟（直载至少崩得干脆）。
//
// 【超时之后怎么办】杀桥。管道里还压着那条没回的消息，留着它会答错下一个问题
//（见 VstBridgeClient.EnsureStartedAsync 的注记）。杀掉之后 IsRunning 变 false，
// 下一次操作会重启一座干净的桥。
//
// 【为什么抽出来】它是一小段可以脱离真实桥单测的逻辑：给一个永不完成的 Task、
// 一个假的上限、一个记账的 onTimeout，就能钉住"超时返回兜底值 + 触发杀桥"。
internal static class BridgeTimeout
{
    /// <summary>
    /// 跑 <paramref name="action"/>（会被甩到线程池，避免 UI 线程自等）并最多等
    /// <paramref name="timeout"/>。超时就调 <paramref name="onTimeout"/>（通常是杀桥）并返回
    /// <paramref name="fallback"/>。动作本身抛异常也返回兜底值（桥的操作都自带 catch，
    /// 这里只是最后一道保险）。
    /// </summary>
    public static T Run<T>(Func<Task<T>> action, TimeSpan timeout, T fallback, Action onTimeout)
    {
        var task = Task.Run(action);

        bool completed;
        try
        {
            completed = task.Wait(timeout);
        }
        catch (Exception)
        {
            // 动作自己抛了（含 AggregateException）——按失败处理，不额外杀桥。
            return fallback;
        }

        if (!completed)
        {
            onTimeout();
            return fallback;
        }

        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
