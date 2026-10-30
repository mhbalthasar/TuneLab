using System;
using System.Threading.Tasks;
using TuneLab.ExperimentalPatch.InsertHost;
using Xunit;

namespace TuneLab.Tests;

// BridgeTimeout 是"等桥回一条消息"的统一上限。它必须做到两件事：
// 正常完成就返回桥给的值；超时就返回兜底值**并且**触发杀桥。
// 桥那边的插件卡死（实测 CHOWTapeModel 卡在 Create）时，这是唯一能救回 UI 线程的地方，
// 所以这几条分支值得单独钉住。
public class BridgeTimeoutTests
{
    [Fact]
    public void Run_WhenActionCompletes_ReturnsItsValueWithoutKilling()
    {
        bool killed = false;

        int result = BridgeTimeout.Run(() => Task.FromResult(42), TimeSpan.FromSeconds(5), -1, () => killed = true);

        Assert.Equal(42, result);
        Assert.False(killed);
    }

    [Fact]
    public void Run_WhenActionNeverCompletes_ReturnsFallbackAndKillsOnce()
    {
        int kills = 0;
        var never = new TaskCompletionSource<int>();   // 永远不完成 —— 桥卡死的替身

        int result = BridgeTimeout.Run(() => never.Task, TimeSpan.FromMilliseconds(150), -1, () => kills++);

        Assert.Equal(-1, result);
        Assert.Equal(1, kills);
    }

    [Fact]
    public void Run_WhenActionThrows_ReturnsFallbackWithoutKilling()
    {
        bool killed = false;

        int result = BridgeTimeout.Run<int>(
            () => Task.FromException<int>(new InvalidOperationException("boom")),
            TimeSpan.FromSeconds(5), -1, () => killed = true);

        Assert.Equal(-1, result);
        Assert.False(killed);
    }
}
