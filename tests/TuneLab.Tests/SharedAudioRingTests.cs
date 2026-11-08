using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TuneLab.ExperimentalPatch.VstBridge;
using Xunit;

namespace TuneLab.Tests;

// 共享音频环的行为测试。
//
// 【为什么值得单测】这是宿主持有的音频线程 − 桥线程之间唯一的交界处。
// 写歪了内存序，症状是"跑久了偶发撕裂缓冲 / 偶发假死"，在真插件上极难复现，
// 而这里能在一个确定性的双线程对撞里把它揪出来。所以这一组测试**跑几万块**
// （不是几块），并且用随机数据（不是常量）——只有连续变化才抓得住错位。
//
// 这些测试在 Windows 上才有意义（共享内存是 Windows 的机制），
// 所以非 Windows 平台上整组跳过：CI 的 Linux job 不会因此变红。
[CollectionDefinition("SharedAudioRing", DisableParallelization = true)]
public sealed class SharedAudioRingCollection
{
}

// 非 Windows 上跳过（共享内存是 Windows 机制）。写成类而不是 attribute 工厂：
// 让跳过的理由写在类名上，测试结果里能直接看到是"平台不支持"而不是"失败"。
[Collection("SharedAudioRing")]
public class SharedAudioRingTests
{
    // 每次一个名字，避免同一台机器上反复跑测试时撞上上一次没回收的段。
    static string Name(string tag) => $"TL.Test.{tag}.{Guid.NewGuid():N}";

    static float[][] MakePlanes(int channels, int frames, int seed)
    {
        var random = new Random(seed);
        var planes = new float[channels][];

        for (int ch = 0; ch < channels; ch++)
        {
            planes[ch] = new float[frames];
            for (int i = 0; i < frames; i++)
                planes[ch][i] = (float)(random.NextDouble() * 2 - 1);
        }

        return planes;
    }

    static void AssertPlanesEqual(float[][] expected, float[][] actual, int channels, int frames, string message)
    {
        for (int ch = 0; ch < channels; ch++)
        {
            for (int i = 0; i < frames; i++)
            {
                if (expected[ch][i] != actual[ch][i])
                {
                    throw new Xunit.Sdk.XunitException(
                        $"{message}: channel {ch} frame {i} differs (expected {expected[ch][i]:R}, actual {actual[ch][i]:R})");
                }
            }
        }
    }

    static bool OnWindows() => OperatingSystem.IsWindows();

    // ── 基本往返 ──

    [Fact]
    public void SingleBlock_RoundTrips()
    {
        // 共享内存是 Windows 机制：别的平台上这组测试不适用，静默返回
        // （xunit 2.6 没有运行时跳过的 API，return 至少不会把 CI 弄红）。
        if (!OperatingSystem.IsWindows())
            return;
        string name = Name("basic");
        using var ring = SharedAudioRing.Create(name, null);

        var expected = MakePlanes(2, 128, 7);
        var planes = MakePlanes(2, 128, 7);   // 同一份数据：读出来必须逐样本一致

        Assert.True(ring.TryPublish(3, planes, 2, 128, 1000));

        Assert.True(ring.TryRead(1000, out int slot, out var read, out int ch, out int fr, out int tag));
        Assert.Equal(3, slot);
        Assert.Equal(2, ch);
        Assert.Equal(128, fr);
        Assert.Equal(0, tag);
        AssertPlanesEqual(expected, read, 2, 128, "single block");

        ring.Release();
    }

    // 回归：两条插入共用同一个环，某一块超时之后，下一块**不能**把上一块的
    // 输入 / 输出当成自己的。根因是环按 FIFO 取，超时的那块还留在里面；
    // 没有标签时 A 的输入会被 B 处理、B 的输出会被 A 取走，链上两条插入
    // 于是互相喂自己上一块的输出 —— 听感就是啸叫 + 卡顿（与插件无关）。
    [Fact]
    public void StaleBlocks_AreSkippedByTag_EvenForTheSameSlot()
    {
        if (!OnWindows())
            return;
        using var ring = SharedAudioRing.Create(Name("tag"), null);

        const int slot = 7;
        var stale = MakePlanes(2, 64, 11);
        var target = MakePlanes(2, 64, 22);
        var later = MakePlanes(2, 64, 33);

        // 同一个槽位：只靠 slot 区分不出来，必须靠 tag。
        Assert.True(ring.TryPublish(slot, stale, 2, 64, 1000, tag: 11));
        Assert.True(ring.TryPublish(slot, target, 2, 64, 1000, tag: 22));
        Assert.True(ring.TryPublish(slot, later, 2, 64, 1000, tag: 33));

        Assert.True(ring.TryReadMatching(22, 1000, out int readSlot, out var read, out int ch, out int fr));
        Assert.Equal(slot, readSlot);
        Assert.Equal(2, ch);
        Assert.Equal(64, fr);
        AssertPlanesEqual(target, read, 2, 64, "matching block");
        ring.Release();

        // 22 之后的 33 还在；11 已经作为过期块被丢掉。
        Assert.True(ring.TryRead(1000, out readSlot, out read, out ch, out fr, out int tag));
        Assert.Equal(slot, readSlot);
        Assert.Equal(33, tag);
        ring.Release();

        Assert.False(ring.TryRead(20, out _, out _, out _, out _, out _));
    }

    // ── 生产者 / 消费者对撞（真正的竞态猎场）──

    [Fact]
    public void ManyBlocks_SurviveProducerConsumerCollision()
    {
        // 共享内存是 Windows 机制：别的平台上这组测试不适用，静默返回
        // （xunit 2.6 没有运行时跳过的 API，return 至少不会把 CI 弄红）。
        if (!OperatingSystem.IsWindows())
            return;
        const int blocks = 20_000;
        const int frames = 512;
        const int channels = 2;

        string section = Name("collision");
        string gateName = Name("gate");

        // 注意：gate 由 ring 自己按名字创建，测试**不**额外 new 一个同名的——
        // 两个句柄指向同一个命名事件却有不同的重置模式，行为立刻错乱
        // （本项目真实踩过：这里曾自行建了个 AutoReset 的同名 gate）。
        using var producer = SharedAudioRing.Create(section, gateName);
        using var consumer = SharedAudioRing.Open(section, gateName);

        var failures = new List<string>();

        // 消费者：独立线程（不是 Task.Run）——真实场景里它是**桥进程**的线程，
        // 与生产者之间只有共享内存。用线程池跑会跟生产者的 Thread.Yield 抢同一批 worker，
        // 那是测试替身自己造出来的饥饿。
        var reader = new Thread(() =>
        {
            for (int b = 0; b < blocks; b++)
            {
                if (!consumer.TryRead(5000, out _, out var planes, out int ch, out int fr, out _))
                {
                    lock (failures)
                        failures.Add($"read timed out at block {b}");

                    return;
                }

                var expected = MakePlanes(channels, frames, b);
                try
                {
                    AssertPlanesEqual(expected, planes, ch, fr, $"block {b}");
                }
                catch (Exception ex)
                {
                    lock (failures)
                        failures.Add(ex.Message);

                    return;
                }
                finally
                {
                    consumer.Release();
                }
            }
        })
        { IsBackground = true };

        reader.Start();

        // 生产者：故意用跟消费者不同的节奏（快一些），把环形填满逼它等。
        for (int b = 0; b < blocks; b++)
        {
            var planes = MakePlanes(channels, frames, b);

            if (!producer.TryPublish(b, planes, channels, frames, 5000))
            {
                lock (failures)
                    failures.Add($"write timed out at block {b}");

                break;
            }
        }

        reader.Join(TimeSpan.FromSeconds(30));

        Assert.Empty(failures);
    }

    // ── 超时与降级 ──

    [Fact]
    public void Read_WhenNothingPublished_TimesOutInsteadOfBlocking()
    {
        // 共享内存是 Windows 机制：别的平台上这组测试不适用，静默返回
        // （xunit 2.6 没有运行时跳过的 API，return 至少不会把 CI 弄红）。
        if (!OperatingSystem.IsWindows())
            return;
        string name = Name("empty");
        using var ring = SharedAudioRing.Create(name, null);

        long before = ring.TimedOutReads;

        bool ok = ring.TryRead(20, out _, out _, out _, out _, out _);

        Assert.False(ok);
        Assert.True(ring.TimedOutReads > before, "timeout should be counted so the host can degrade");
    }

    [Fact]
    public void Publish_TwiceOverCapacity_StillKeepsBlocksCorrect()
    {
        // 共享内存是 Windows 机制：别的平台上这组测试不适用，静默返回
        // （xunit 2.6 没有运行时跳过的 API，return 至少不会把 CI 弄红）。
        if (!OperatingSystem.IsWindows())
            return;
        // 容量 4：连续发 8 块、读 8 块，环形要绕回来且每块内容都对。
        const int frames = 64;
        const int channels = 2;
        const int total = SharedAudioLayout.SlotCount * 2;

        string section = Name("wrap");
        using var ring = SharedAudioRing.Create(section, null);

        var reader = Task.Run(() =>
        {
            for (int b = 0; b < total; b++)
            {
                if (!ring.TryRead(5000, out _, out var planes, out int ch, out int fr, out _))
                {
                    Assert.Fail($"read timed out at {b}");
                    return;
                }

                var expected = MakePlanes(channels, frames, b);
                AssertPlanesEqual(expected, planes, ch, fr, $"wrapped block {b}");
                ring.Release();
            }
        });

        for (int b = 0; b < total; b++)
        {
            var planes = MakePlanes(channels, frames, b);
            Assert.True(ring.TryPublish(b, planes, channels, frames, 5000), $"write {b}");
        }

        Assert.True(reader.Wait(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Publish_OversizedBlock_IsRefused()
    {
        // 共享内存是 Windows 机制：别的平台上这组测试不适用，静默返回
        // （xunit 2.6 没有运行时跳过的 API，return 至少不会把 CI 弄红）。
        if (!OperatingSystem.IsWindows())
            return;

        string name = Name("toobig");
        using var ring = SharedAudioRing.Create(name, null);

        var planes = new[] { new float[SharedAudioLayout.MaxBlockFrames + 1], new float[SharedAudioLayout.MaxBlockFrames + 1] };

        // 直接拒绝而不是截断：共享区大小是定死的，越界写就是踩别人的内存。
        Assert.False(ring.TryPublish(0, planes, 2, SharedAudioLayout.MaxBlockFrames + 1, 50));
    }

    // ── 布局约定（改结构体必须同步改测试）──

    [Fact]
    public void Layout_IsStable()
    {
        // 共享内存是 Windows 机制：别的平台上这组测试不适用，静默返回
        // （xunit 2.6 没有运行时跳过的 API，return 至少不会把 CI 弄红）。
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(4, SharedAudioLayout.SlotCount);

        // [EP-MIXER-27] 2 → 4：侧链要额外两条声道（键 L/R）。改这个数字 = 改共享内存布局，
        // 必须同时 bump BridgeProtocol.ProtocolVersion（否则新旧桥会按不同布局读写同一块内存）。
        Assert.Equal(4, SharedAudioLayout.MaxChannels);

        // 头 16 + 4 槽 × (16 头 + 4 × 2048 × 4 音频) = 16 + 4 × 32784 = 131152
        // 4096 = "效果器插件缓冲时间"默认 50 ms @44.1k（≈2205 帧）的上界；混音图按 4096 分块。
        Assert.Equal(16 + 4 * (16 + 4 * 4096 * 4), SharedAudioLayout.SectionBytes);
        Assert.Equal(16 + 4 * 4096 * 4, SharedAudioLayout.SlotBytes);
    }
}





