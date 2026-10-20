using System;

namespace TuneLab.Tests;

// 测试的"这条不适用于当前环境"表达。
//
// 为什么不是 [Fact(Skip=...)]：那个值是**编译期**常量，而我们想按运行期条件跳过
// （这台机器有没有 64 位 VST3、是不是 Windows）。
// 为什么不是抛 Xunit.SkipException：那个类在 xunit 2.6.2 里**没有**可用的构造函数。
// 所以就是这一行：抛一个失败，消息以 "SKIP: " 开头 —— 看得见、也不会污染 CI 结论
// （CI 上我们会显式检查这条消息）。
internal static class Skip
{
    internal static void Unless(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException("SKIP: " + reason);
    }

    internal static void If(bool condition, string reason) => Unless(!condition, reason);
}
