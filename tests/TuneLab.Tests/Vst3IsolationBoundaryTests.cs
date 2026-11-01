using System;
using System.Linq;
using TuneLab.ExperimentalPatch.InsertHost;
using Xunit;

namespace TuneLab.Tests;

// [VST3-BRIDGE] 架构边界的哨兵：**宿主程序集里不许有插件的进程内实现**。
//
// 为什么值得一条测试：这条边界原本只是一个"开关"（IsolateVst3），代码、native dll、
// 回退路径都还在 patch 本体里 —— 任何人改一行就能把它拉回进程内，而且不会有人注意到。
// 现在进程内实现（vendored OwnVST3 包装层 + Vst3Insert）整体搬到了桥工程，
// 这里钉住"搬走"这件事本身：只要有人把那些文件再编译进宿主，测试立刻红。
//
// 注意：桥工程（VstBridge）里仍然有它们 —— 那是另一个进程，正是本测试要保护的样子。
public class Vst3IsolationBoundaryTests
{
    [Fact]
    public void TheHostAssemblyCarriesNoInProcessVst3Wrapper()
    {
        var host = typeof(Vst3Backend).Assembly;

        // vendored 包装层在命名空间 OwnVST3Host 里（OwnVst3Wrapper / PluginScanner / ...）。
        Assert.Null(host.GetType("OwnVST3Host.OwnVst3Wrapper", throwOnError: false));

        Assert.DoesNotContain(host.GetTypes(),
            type => string.Equals(type.Namespace, "OwnVST3Host", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHostAssemblyCarriesNoInProcessVst3Insert()
    {
        var host = typeof(Vst3Backend).Assembly;

        Assert.Null(host.GetType("TuneLab.ExperimentalPatch.InsertHost.Vst3Insert", throwOnError: false));
        Assert.Null(host.GetType("TuneLab.ExperimentalPatch.VstBridge.InProcess.Vst3Insert", throwOnError: false));

        // 宿主侧唯一的 VST3 实现是过桥的那个。
        Assert.NotNull(host.GetType("TuneLab.ExperimentalPatch.InsertHost.Vst3BridgeInsert", throwOnError: false));
    }
}
