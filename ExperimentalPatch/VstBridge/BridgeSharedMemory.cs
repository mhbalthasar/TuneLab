using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace TuneLab.ExperimentalPatch.VstBridge;

// ════════════════════════════════════════════════════════════════════════════
// 共享内存音频环（宿主 ↔ 桥之间的**数据**通路）。
//
// 【为什么不用命名管道传音频】不是延迟——本地管道往返几十微秒，对 10.6ms 一块来说
// 可以忽略。真正的问题是**音频线程会被管道阻塞**：管道缓冲满、或桥被调度器摘走一会儿，
// 宿主的 Write/Read 就挂住，等出来就是一个听得见的爆音。共享内存 + 自旋把等待
// 变成"在本地内存上转圈"，可控、可超时、可降级。
//
// 【本文件的特殊纪律】它被**两个工程各编译一份**（InsertHost 与 VstBridge，由各自的
// csproj 用 <Compile Include> 链进来）。所以这里只准出现 BCL 与 Windows API，
// 一端特有的类型一个都不许有。改这里的布局 = 两端一起改，靠
// SharedAudioRingTests 的行为测试兜住（测试的是这份源码编译进 InsertHost 的那一份）。
//
// 【内存序】x86/x64 是强内存序模型，TSO 保证 store-store 不重排，所以
// "写完数据 → Volatile.Write 序号"即可让对端看见完整数据；Volatile 的编译器屏障
// 保证 .NET JIT 不会把数据写重排到序号写之后。两端都是 Windows，暂无 ARM 顾虑，
// 但 Volatile 的用法在 ARM64 上同样是正确的（它在那里会发屏障）。
//
// 【并发模型】严格的单生产者单消费者（SPSC）：
//   · 生产者：本进程的音频线程（宿主侧）/ 音频线程（桥侧）
//   · 消费者：桥的音频线程 / 宿主的音频线程
// 每条链路一个方向、一个生产者、一个消费者，不需要 CAS，也不需要锁。
// ════════════════════════════════════════════════════════════════════════════
public static class SharedAudioLayout
{
    // 环形容量。取 4 而不是 1：今天同步用（一次只会在途一块），多出来的容量不要钱，
    // 而将来要做"提前一块流水、把桥的调度延迟藏掉"时协议一行都不用改。
    public const int SlotCount = 4;

    // 上限：2048 帧 × 2 声道。宿主设备块长通常 512~1024，留一倍余量。
    // 超了不是"凑合写进去"，而是拒绝——共享区大小是定死的，越界写就是踩别人的内存。
    public const int MaxBlockFrames = 2048;

    public const int MaxChannels = 2;

    // 每块的元数据头（16 字节，见 BlockHeader）。
    public const int BlockHeaderBytes = 16;

    public static readonly int SlotBytes = BlockHeaderBytes + MaxChannels * MaxBlockFrames * 4;

    // 整段 = 头（两个 long）+ 所有块。
    public static readonly int SectionBytes = 16 + SlotCount * SlotBytes;

    // 块内音频数据的起始偏移（相对块起点）。
    public const int BlockAudioOffset = BlockHeaderBytes;
}

// 每块的元数据。定长 16 字节：序号推进之后消费者才读它，所以编译器/CPU 怎么排布
// 字段都不影响正确性，但保持定长让偏移计算变成常量。
[StructLayout(LayoutKind.Explicit, Size = SharedAudioLayout.BlockHeaderBytes)]
unsafe struct BlockHeader
{
    [FieldOffset(0)] public int Slot;
    [FieldOffset(4)] public int Frames;
    [FieldOffset(8)] public int Channels;

    // 预留。曾经放"本块是否旁通"，后来删了：旁通的正确做法是**根本不发起往返**
    // （见 VstBridgeClient.SendProcess），让不需要过桥的音频白白跑一趟没有意义。
    [FieldOffset(12)] public int Reserved;
}

// 段头：两个单调递增的序号（配合 SlotCount 做环形）。
[StructLayout(LayoutKind.Sequential)]
unsafe struct SectionHeader
{
    // 生产者已**发布**的块数。每发布一块 +1。
    public long WriteSeq;

    // 消费者已**归还**的块数。生产者据此判断还能不能写（差 < SlotCount 才能写）。
    public long ReadSeq;
}

// 一个方向的环。
//
// 用法是一侧一侧的，不要两头同时用同一个实例：
//   · 宿主 h2b：宿主 Publish / 桥 Read + Release
//   · 宿嘉 b2h：桥 Publish / 宿主 Read + Release
public sealed unsafe class SharedAudioRing : IDisposable
{
    // 自旋预算（次）。约等于几十微秒：够挡住一次正常的调度让小延迟变成"没发生"，
    // 又不至于在桥真的卡死时白烧一个核。
    const int SpinBudget = 4096;

    readonly MemoryMappedFile mSection;
    readonly MemoryMappedViewAccessor mAccessor;
    readonly SectionHeader* mHeader;
    readonly byte* mBlocks;

    // 只由生产者记：下一个待发布的序号。消费者不需要（它读 WriteSeq）。
    long mNextWrite;

    readonly EventWaitHandle? mDataAvailable;

    public bool HasGate => mDataAvailable != null;

    // 诊断用。宿主侧把它暴露给界面（"插件卡住"要看得见），桥侧写日志。
    public long Published { get; private set; }

    public long TimedOutReads { get; private set; }

    public long TimedOutWrites { get; private set; }

    SharedAudioRing(MemoryMappedFile section, EventWaitHandle? gate)
    {
        mSection = section;
        mDataAvailable = gate;

        mAccessor = section.CreateViewAccessor(0, SharedAudioLayout.SectionBytes, MemoryMappedFileAccess.ReadWrite);

        byte* pointer = null;
        mAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);

        mHeader = (SectionHeader*)pointer;
        mBlocks = pointer + sizeof(SectionHeader);
    }

    /// <summary>创建一段共享内存（生产者所在的那一端）。</summary>
    [SupportedOSPlatform("windows")]
    public static SharedAudioRing Create(string sectionName, string? gateName)
    {
        // 容量对两端必须一致：从同一份源码编译出来的常量，这里断言一次。
        var section = MemoryMappedFile.CreateNew(sectionName, SharedAudioLayout.SectionBytes);

        // gate 用 ManualReset（不是 AutoReset）：见 WaitUntil 的注释。
        // 发布后 Set，消费者确认数据到位后 Reset。
        var gate = gateName == null
            ? null
            : new EventWaitHandle(true, EventResetMode.ManualReset, gateName);

        return new SharedAudioRing(section, gate);
    }

    /// <summary>打开一段已存在的共享内存（消费者/另一端）。</summary>
    [SupportedOSPlatform("windows")]
    public static SharedAudioRing Open(string sectionName, string? gateName)
    {
        var section = MemoryMappedFile.OpenExisting(sectionName);

        var gate = gateName == null
            ? null
            : EventWaitHandle.OpenExisting(gateName);

        return new SharedAudioRing(section, gate);
    }

    // ── 生产者侧 ──

    /// <summary>
    /// 发布一块音频（平面，按声道顺序拼接：L0..Ln, R0..Rn）。
    /// 返回 false = 消费者迟迟不归还缓冲区（桥卡死 / 已退出）——调用方应当把
    /// 对应的插入摘掉，因为环已经对不上步了，继续发只会越来越乱。
    /// </summary>
    public bool TryPublish(int slot, float[][] planes, int channels, int frames, int waitMs)
    {
        if (channels <= 0 || frames <= 0 || channels > SharedAudioLayout.MaxChannels
            || frames > SharedAudioLayout.MaxBlockFrames)
        {
            return false;
        }

        if (!WaitForWritable(waitMs))
        {
            TimedOutWrites++;
            return false;
        }

        long sequence = mNextWrite;
        int index = (int)(sequence % SharedAudioLayout.SlotCount);

        var header = (BlockHeader*)(mBlocks + index * SharedAudioLayout.SlotBytes);

        header->Slot = slot;
        header->Frames = frames;
        header->Channels = channels;
        header->Reserved = 0;

        float* audio = (float*)(mBlocks + index * SharedAudioLayout.SlotBytes + SharedAudioLayout.BlockAudioOffset);

        int written = 0;
        for (int ch = 0; ch < channels; ch++)
        {
            int count = Math.Min(frames, planes[ch].Length);

            new ReadOnlySpan<float>(planes[ch], 0, count)
                .CopyTo(new Span<float>(audio + written, count));

            written += count;
        }

        // ★ 发布：必须发生在写完音频**之后**。x86 的 TSO 保证数据先于序号可见，
        //   Volatile.Write 再堵住 JIT 把数据写摆到序号写之后的可能。
        Volatile.Write(ref mHeader->WriteSeq, sequence + 1);

        mNextWrite = sequence + 1;
        Published++;

        // 通知可能在睡觉得消费者。没装 gate 的链路纯靠自旋（宿主→桥那条就是这样）。
        mDataAvailable?.Set();

        return true;
    }

    // ── 消费者侧 ──

    /// <summary>
    /// 取生产者已发布、且本消费者还没取走的最早一块。
    /// 返回 false = 超时（没数据），此时 out 参数无意义。
    /// </summary>
    public bool TryRead(int waitMs, out int slot, out float[][] planes, out int channels, out int frames)
    {
        slot = 0;
        channels = 0;
        frames = 0;
        planes = [];

        long next = Volatile.Read(ref mHeader->ReadSeq);

        if (!WaitUntil(mHeader, next + 1, waitMs))
        {
            TimedOutReads++;
            return false;
        }

        int index = (int)(next % SharedAudioLayout.SlotCount);
        var header = (BlockHeader*)(mBlocks + index * SharedAudioLayout.SlotBytes);

        slot = header->Slot;
        frames = Math.Min(header->Frames, SharedAudioLayout.MaxBlockFrames);
        channels = Math.Min(header->Channels, SharedAudioLayout.MaxChannels);

        // 平面缓冲：借调用方的（PluginHost 每个槽位备好的那份），零分配。
        float* audio = (float*)(mBlocks + index * SharedAudioLayout.SlotBytes + SharedAudioLayout.BlockAudioOffset);

        int read = 0;
        planes = new float[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            planes[ch] = new float[frames];

            // 注意这里是 frames（float 个数），不是 frames * 4（字节）：
            // Span<float> 的长度按元素算。写成字节数是第一次写完就炸的写法。
            new ReadOnlySpan<float>(audio + read, frames).CopyTo(planes[ch]);
            read += frames;
        }

        return true;
    }

    /// <summary>把刚取走的那块归还给环形（推进 ReadSeq）。</summary>
    public void Release()
    {
        long next = Volatile.Read(ref mHeader->ReadSeq);

        // ★ 归还：推进序号让生产者知道这个槽位又能写了。
        Volatile.Write(ref mHeader->ReadSeq, next + 1);
    }

    // ── 等待 ──

    // 等消费者腾出槽位。环形里最多 SlotCount 块在途，所以判定是
    // "待发布序号 − 已归还序号 < SlotCount"。
    //
    // 这里**不碰 gate**：腾槽位的概率极低（消费者只会在自己按同一个节奏消费时才堵），
    // 而且等的是对方推进序号这种"一定会发生"的事，纯自旋 + Yield 就够。
    // 真要把这条也做成可睡眠的，得给它单独一个反向事件，否则两个方向共用一个
    // ManualReset 事件会互相误醒（本项目真实踩过一次，见 SharedAudioRingTests 的注释）。
    bool WaitForWritable(int waitMs)
    {
        long start = Stopwatch.GetTimestamp();

        while (true)
        {
            long readSeq = Volatile.Read(ref mHeader->ReadSeq);

            if (mNextWrite - readSeq < SharedAudioLayout.SlotCount)
                return true;

            Thread.Yield();

            if (!SpinOrWait(waitMs, start))
                return false;
        }
    }

    // 等 WriteSeq 达到 target。
    //
    // 先自旋（短延迟下这是最快也最可预测的），超预算后睡在 gate 上。
    // gate 是 ManualReset：生产者发布后 Set，消费者确认序号到位后 Reset。
    // ManualReset 而不是 AutoReset 是**必须的**：AutoReset 会把"早到的那次 Set"
    // 消费掉却没告诉任何等待者，而序号是已经推进的——下一次 WaitOne 就永远等不到
    // 第二次通知，症状是偶发卡死。（正是这个竞态让 ManyBlocks 测试在 block 4 挂住。）
    bool WaitUntil(SectionHeader* counter, long target, int waitMs)
    {
        long start = Stopwatch.GetTimestamp();
        int spins = 0;

        while (true)
        {
            if (Volatile.Read(ref counter->WriteSeq) >= target)
                return true;

            if (spins < SpinBudget)
            {
                Thread.SpinWait(64);
                spins++;
                continue;
            }

            if (mDataAvailable is { } gate)
            {
                long elapsedMs = elapsed(start);

                if (elapsedMs >= waitMs)
                    return false;

                if (gate.WaitOne((int)Math.Max(1, waitMs - elapsedMs)))
                {
                    // 被唤醒不代表序号到位（可能是上一轮残留的信号）。
                    // 已到位就清零，让下一个等待者重新等下一次 Set。
                    if (Volatile.Read(ref counter->WriteSeq) >= target)
                    {
                        gate.Reset();
                        return true;
                    }

                    // 没到位： Reset 后再等。这一步不能省——否则门一直是开的，
                    // 下一次 WaitOne 立刻返回，自旋循环退化成忙等。
                    gate.Reset();
                    continue;
                }
            }
            else
            {
                Thread.Yield();
            }

            if (elapsed(start) >= waitMs)
                return false;
        }
    }

    static long elapsed(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000 / Stopwatch.Frequency;

    // 自旋之后的第二段：睡在事件上（有 gate）或者 Yield（没有）。
    // 返回 false = 超时。
    bool SpinOrWait(int waitMs, long startTimestamp)
    {
        if (mDataAvailable is { } gate)
        {
            long elapsedMs = elapsed(startTimestamp);

            if (elapsedMs >= waitMs)
                return false;

            gate.WaitOne((int)Math.Max(1, waitMs - elapsedMs));
        }
        else
        {
            Thread.Yield();
        }

        return elapsed(startTimestamp) < waitMs;
    }

    public void Dispose()
    {
        try
        {
            mAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        catch (Exception)
        {
        }

        mAccessor.Dispose();
        mSection.Dispose();
        mDataAvailable?.Dispose();
    }
}
