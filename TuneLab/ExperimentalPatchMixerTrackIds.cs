using System.Collections.Generic;

namespace TuneLab.Audio;

// [EP-MIXER-27] 轨道的**稳定序号**：侧链引用靠它，而不是列表下标。
//
// 【为什么不能继续用下标】模型侧的通道 id 一直是 "track:" + 列表位置（MixerTrackChannel.Id）。
// 侧链是第一个**指向轨道**的引用（output / send 只指向 AUX），于是下标的问题第一次暴露：
//   · 删掉第 0 条轨，后面每一条的 id 都会往前挪一格 —— 侧链引用会静默指到**另一条**轨上，
//     比悬空引用更糟（悬空至少会被跳过）；
//   · 删了再建，新轨会复用旧下标 —— 旧引用"复活"。
// 文件顶部原本就有一条 TODO(持久化) 记着这件事，这里把它兑现。
//
// 【方案】按 Track 对象分配一个单调递增的序号，序号跟着对象走：
//   · 会话内：重排 / 删除 / undo 都不改变已有轨的序号（undo 恢复的是同一个对象）；
//   · 落盘：序号写进 epMixer sidecar 的 TrackDto.MixerId，装载时按匹配到的轨恢复；
//   · 新轨：拿一个新号，**绝不复用**（复用的窗口正是"删了又建"）。
//
// 【键为什么是 object】编译器拿到的是 IAudioTrack（音频侧的窄接口），模型拿到的是 ITrack；
// 同一个 Track 对象实现两者。按对象身份记账就不用逼编译器去认 ITrack。
//
// 【线程】只在 UI 线程 / 编译图时调用（都不是音频线程）。加锁是为了防御
// "宿主在别的线程上碰了模型"这种上游行为，代价可以忽略。
internal static class MixerTrackIds
{
    // ReferenceEqualityComparer：Track 没有重写 Equals，但这里要的是**身份**而不是值相等 ——
    // 万一将来有人给 Track 加了值语义，序号也不能串。
    static readonly Dictionary<object, int> sOrdinals = new(ReferenceEqualityComparer.Instance);
    static int sNext = 1;

    public static int OrdinalOf(object track)
    {
        lock (sOrdinals)
        {
            if (sOrdinals.TryGetValue(track, out int ordinal))
                return ordinal;

            ordinal = sNext++;
            sOrdinals[track] = ordinal;
            return ordinal;
        }
    }

    // 模型侧通道 id（"track:7"）。与引擎节点 id（"track-0"）是**两套**东西：
    // 节点 id 是编译期下标，电平查询依赖它，绝不能改；这里只用于引用与落盘。
    public static string IdOf(object track) => "track:" + OrdinalOf(track);

    // 装载时恢复保存的序号。0 = 老工程没有这个字段 → 当作没分配过（懒分配一个新号）。
    public static void Assign(object track, int ordinal)
    {
        if (ordinal <= 0)
            return;

        lock (sOrdinals)
        {
            sOrdinals[track] = ordinal;

            if (ordinal >= sNext)
                sNext = ordinal + 1;
        }
    }
}
