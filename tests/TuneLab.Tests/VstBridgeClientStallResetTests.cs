using System;
using System.Threading;
using TuneLab.ExperimentalPatch.InsertHost;
using Xunit;

namespace TuneLab.Tests;

// 桥重启之后，音频路的停滞标记必须跟着清零。
//
// 【为什么需要这一组】AudioStallWatchTests 钉的是"连续失败到阈值就判死"，
// BridgeIsolationTests 钉的是进程本身；但"判死之后换一个新桥还能不能出声"没人钉。
// 真机上撞到的形状：真 VST3 崩溃 → 音频块连续失败 50 次把标记打上 → 管道自己断开
// （IsConnected 变假，全程没有任何代码调 KillBridge）→ 下一次加载起了一个新桥，
// 停滞标记却还在，于是新桥上每一块都被当成"桥赶不上"直接直通：界面显示已隔离，
// 声音里却没有这个插入。要重启宿主才好。
public class VstBridgeClientStallResetTests
{
    [Fact]
    public void StartingAFreshBridgeDoesNotInheritThePreviousStall()
    {
        var client = new VstBridgeClient(64);

        try
        {
            for (int i = 0; i < AudioStallWatch.FailureLimit; i++)
                client.ReportAudioFailureForTest();

            Assert.True(client.AudioStalled,
                "the audio path should be marked stalled after the failure limit");

            // 这里不关心桥能不能起来（测试输出目录旁边通常没有桥 exe，
            // 这时 EnsureStartedBlocking 返回 false）。要钉的是：清理发生在启动之前。
            client.EnsureStartedBlocking();

            Assert.False(client.AudioStalled,
                "a fresh start must not inherit the previous bridge's stall flag");
        }
        finally
        {
            client.KillBridge();
        }
    }

    // 复位不能顺手把"这一轮确实失败过"的事实抹掉：没有重启时标记必须留着，
    // 否则红行会在下一块立刻变回正常（这正是要避免的"假活"）。
    [Fact]
    public void TheStallSurvivesAsLongAsNoNewBridgeStarts()
    {
        var client = new VstBridgeClient(64);

        try
        {
            for (int i = 0; i < AudioStallWatch.FailureLimit; i++)
                client.ReportAudioFailureForTest();

            Assert.True(client.AudioStalled);

            Thread.Sleep(50);

            Assert.True(client.AudioStalled, "nothing restarted, so the stall must still hold");
        }
        finally
        {
            client.KillBridge();
        }
    }
}
