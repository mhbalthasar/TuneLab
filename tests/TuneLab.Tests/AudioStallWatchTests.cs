using TuneLab.ExperimentalPatch.InsertHost;
using Xunit;

namespace TuneLab.Tests;

// AudioStallWatch 是"运行期音频挂起"的判定：连续若干块拿不到桥的结果就停用音频路。
//
// 【为什么值得单独测】它只做两件事 —— 数够就判死、换桥就复位 —— 而这两件事决定了
// 插件卡住时音频会不会被每块 20ms 的等待拖成爆音，以及一次超时杀桥之后会不会
// 永远回落成直通。真的起进程验证由 opt-in 的真插件回归做；这里把语义钉死
// （与 BridgeTimeoutTests 同一套纪律）。
public class AudioStallWatchTests
{
    [Fact]
    public void BelowTheLimit_StaysLive()
    {
        var watch = new AudioStallWatch();

        for (int i = 0; i < AudioStallWatch.FailureLimit - 1; i++)
            Assert.False(watch.ReportFailure());

        Assert.False(watch.Stalled);
    }

    [Fact]
    public void AtTheLimit_StallsExactlyOnce()
    {
        var watch = new AudioStallWatch();

        for (int i = 0; i < AudioStallWatch.FailureLimit - 1; i++)
            watch.ReportFailure();

        Assert.True(watch.ReportFailure());    // 跨过阈值这一次返回 true
        Assert.True(watch.Stalled);

        // 已停滞：再喂失败不再"重新判死"（调用方只该记一次日志）。
        Assert.False(watch.ReportFailure());
        Assert.True(watch.Stalled);
    }

    [Fact]
    public void ASuccessfulBlock_ClearsTheFailureStreak()
    {
        var watch = new AudioStallWatch();

        for (int i = 0; i < AudioStallWatch.FailureLimit - 1; i++)
            watch.ReportFailure();

        watch.ReportSuccess();                 // 中间成功一块：连败计数清零
        Assert.False(watch.Stalled);

        for (int i = 0; i < AudioStallWatch.FailureLimit - 1; i++)
            Assert.False(watch.ReportFailure());

        Assert.False(watch.Stalled);           // 又被清零，仍然不判死
    }

    [Fact]
    public void Reset_ClearsBothTheStreakAndTheStall()
    {
        var watch = new AudioStallWatch();

        for (int i = 0; i < AudioStallWatch.FailureLimit; i++)
            watch.ReportFailure();

        Assert.True(watch.Stalled);

        watch.Reset();                          // 桥重启 / 被关闭
        Assert.False(watch.Stalled);

        // 复位后必须真的从头数：再失败一块不该立刻又判死。
        Assert.False(watch.ReportFailure());
        Assert.False(watch.Stalled);
    }

    [Fact]
    public void Stalled_IsLatchedUntilReset()
    {
        // Stalled 是给 UI 线程读的状态位：没有继续喂失败时它不能自己回落。
        var watch = new AudioStallWatch();

        for (int i = 0; i < AudioStallWatch.FailureLimit; i++)
            watch.ReportFailure();

        Assert.True(watch.Stalled);
        Assert.True(watch.Stalled);
    }
}
