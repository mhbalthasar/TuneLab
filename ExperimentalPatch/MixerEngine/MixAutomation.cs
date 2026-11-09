namespace TuneLab.ExperimentalPatch.MixerEngine;

// 采样位置 → 乐谱 tick 的换算（音频线程每个渲染块开头调一次）。
//
// 【线程】实现方必须持有一份**不可变**的换算快照；tempo 变化时宿主换掉整个对象引用，
// 音频线程只做一次引用读 —— 永远看到自洽的一份，绝不自己重建（分配 / 竞态都不许有）。
public interface IMixTimeMapper
{
    double PositionToTick(double position);
}

// 一条"插入参数自动化"绑定：音频线程每块开头把曲线值（叠加基值、钳位后）推给实例。
//
// 【为什么放 MixerEngine】插值时机只有音频回调知道（块首的采样位置），而包装器
// （IMixInsert.Process）拿不到位置 —— 见 Mixer.RenderChunk 的调用点。
//
// 【evaluate】tick → 本块应施加的参数值。宿主侧闭包：基值 + 曲线偏移 + 钳位都在那边算好
// （MixerEngine 不认插件、不认曲线数据结构）。实现必须是**无分配**的：它跑在音频线程上。
//
// 【push】把值写进实例。返回 false = 这次没写进去（桥忙 / 音频路停）——绑定保留旧值，
// 下一块重推；推成功与否决定"上次推过的值"要不要更新。
//
// 【稳态零开销】曲线值没变就不推：自动化的常见形态是大段平线，每块只有第一次推一发。
public sealed class MixAutomationBinding(Func<double, double> evaluate, Func<double, bool> push)
{
    double mLastPushed = double.NaN;

    public void Apply(double tick)
    {
        double value = evaluate(tick);

        // NaN 哨兵：首块必推（把当前曲线值同步进实例，即使曲线全平）。
        if (value == mLastPushed)
            return;

        if (!push(value))
            return;

        mLastPushed = value;
    }
}
