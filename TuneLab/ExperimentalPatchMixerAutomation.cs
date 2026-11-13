using System;
using System.Collections.Generic;
using System.Linq;
using TuneLab.Audio;
using TuneLab.Data;
using TuneLab.Data.Timing;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.Foundation;
using HostLog = TuneLab.Foundation.Log;

namespace TuneLab;

// [EP-MIXER-AUTO] 插入参数自动化的宿主侧接线。
//
// 【数据】条目（基值 / 量程）在 InsertSlotAutomation（挂在 InsertSlot 上，随槽位 undo / 落盘）；
//         每个 part 的偏移曲线住在该 part 的自动化表里（键 = "{slotId}:{paramId}"，part 相对 tick）。
//         同一参数可被多个 part 控制（AUX / Master 链尤其如此），曲线**求和**后叠加到基值。
// 【求值】音频线程每块开头：位置 → tick（TempoTimeMapper）→ 各 part 冻结曲线快照求偏移之和
//         → clamp(基值 + Σ偏移, 参数 Min, 参数 Max) → 推给正在跑的实例。
// 【纪律】音频线程路径零分配：绑定持有一份不可变的 (partPos, Point[]) 快照，UI 改曲线时整份换引用；
//         稳态（平线）时每块只做几次 double 比较。
internal static class MixerAutomation
{
    // ── 位置 → tick ──

    public static IMixTimeMapper? CreateTimeMapper(Project? project)
    {
        if (project?.TempoManager is not TempoManager tempoManager)
            return null;

        return new TempoTimeMapper(tempoManager.CreateSnapshot(), AudioEngine.SampleRate.Value);
    }

    sealed class TempoTimeMapper(TempoSnapshot snapshot, double sampleRate) : IMixTimeMapper
    {
        public double PositionToTick(double position) => snapshot.ToTick(position / sampleRate);
    }

    // ── 一条绑定的宿主侧实现 ──

    internal sealed class Binding
    {
        public Binding(InsertSlotAutomation entry, string keyId, Project? project,
            double minValue, double maxValue, Func<double, bool> push)
        {
            mEntry = entry;
            mKeyId = keyId;
            mProject = project;
            mMin = Math.Min(minValue, maxValue);
            mMax = Math.Max(minValue, maxValue);
            mPush = push;

            RebuildSnapshot();
            // 全量脸（AsEverytime）：面板拖基值时外层包着 merge 作用域，结果态要等松手才发；
            // 而 TryWriteAutomatedParameter 在拖动中每步都要用**新基值**求值推一次。
            entry.BaseValue.Modified.AsEverytime().Subscribe(_ => RebuildSnapshot(), mSubscriptions);

            if (project == null)
                return;

            // 结构性变化（part 增删 / 轨道增删）→ 重挂"每个 part 的自动化表"订阅并重取快照。
            // 曲线内容变化走 part.Automations.Modified（Map 的 Modified 会聚合子项修改，
            // 与渲染器重绘同一口径）；曲线首次创建也走它。
            project.Tracks.MembershipModified.Subscribe(RebuildStructure, mSubscriptions);
            project.Tracks.WhenAny(track => track.Parts.ItemAdded).Subscribe(_ => RebuildStructure(), mSubscriptions);
            project.Tracks.WhenAny(track => track.Parts.ItemRemoved).Subscribe(_ => RebuildStructure(), mSubscriptions);

            RebuildStructure();
        }

        readonly InsertSlotAutomation mEntry;
        readonly string mKeyId;
        readonly Project? mProject;
        readonly double mMin;
        readonly double mMax;
        readonly Func<double, bool> mPush;
        readonly DisposableManager mSubscriptions = new();
        readonly DisposableManager mPartSubscriptions = new();

        // 工厂的自动化登记要用（参数面板按 (槽位, 参数) 找回条目改基值）。
        internal InsertSlotAutomation Entry => mEntry;

        // 冻结快照。volatile 引用换：音频线程要么看旧的、要么看新的，绝不看半份。
        // 只存 Point（纯数据），不存 AnchorPoint（带着选择事件、UI 线程对象）。
        // Start/End = part 的时域（绝对 tick）：曲线只在这个区间内贡献 —— 否则端点值会一直
        // 延伸到全曲，把别的 part 的区间也叠上（AUX / Master 链尤其明显）。
        // 【为什么 base 不用 volatile】volatile 不允许 double；base 的一次读撕裂不可能
        //（对齐的 64 位写），偶尔读到一次旧基值 = 旧值持续到下一块，无害。
        readonly record struct LaneSnapshot(double Pos, double Start, double End, Point[] Points);
        volatile LaneSnapshot[] mLanes = [];
        double mBase;

        void RebuildStructure()
        {
            mPartSubscriptions.DisposeAll();

            if (mProject != null)
            {
                foreach (var track in mProject.Tracks)
                {
                    foreach (var part in track.Parts)
                    {
                        if (part is not IMidiPart midiPart)
                            continue;

                        // 全量脸（AsEverytime）：画点 / 拖锚点的编辑手势整段包在 merge 作用域里，
                        // 结果态要等松手才发 —— 只订结果态会让音频在拖动中不跟手。
                        midiPart.Automations.Modified.AsEverytime().Subscribe(_ => RebuildSnapshot(), mPartSubscriptions);
                        part.Pos.Modified.Subscribe(RebuildSnapshot, mPartSubscriptions);
                        part.StartOffset.Modified.Subscribe(RebuildSnapshot, mPartSubscriptions);
                        part.EndOffset.Modified.Subscribe(RebuildSnapshot, mPartSubscriptions);
                    }
                }
            }

            RebuildSnapshot();
        }

        void RebuildSnapshot()
        {
            mBase = mEntry.BaseValue.Value;

            if (mProject == null)
            {
                mLanes = [];
                return;
            }

            var lanes = new List<LaneSnapshot>();

            foreach (var track in mProject.Tracks)
            {
                foreach (var part in track.Parts)
                {
                    if (part is not IMidiPart midiPart)
                        continue;

                    if (!midiPart.Automations.TryGetValue(mKeyId, out var curve) || curve.Points.Count == 0)
                        continue;

                    // [EP-MIXER-AUTO] 只收**这条 part 的音流通道上真有这个 FX** 的曲线：
                    // 拆分 / 选区截取把曲线带到别的轨之后，目标轨没有这条插入 → 休眠数据，
                    // 不能隔轨绑到源轨的插件上。
                    if (!InsertAutomationCatalog.IsKeyOwnedByPart(mProject, midiPart, mKeyId))
                        continue;

                    lanes.Add(new LaneSnapshot(part.Pos.Value, part.StartPos(), part.EndPos(),
                        [.. curve.Points.Select(p => new Point(p.Pos, p.Value))]));
                }
            }

            mLanes = [.. lanes];
        }

        // tick → 生效值。音频线程每块调用；无分配。
        public double Evaluate(double tick)
        {
            var lanes = mLanes;
            double offset = 0;

            for (int i = 0; i < lanes.Length; i++)
            {
                var lane = lanes[i];
                if (tick < lane.Start || tick > lane.End)
                    continue;

                offset += InsertAutomationMath.Evaluate(lane.Points, tick - lane.Pos);
            }

            return Math.Clamp(mBase + offset, mMin, mMax);
        }

        public bool Push(double value) => mPush(value);

        public void Dispose()
        {
            mPartSubscriptions.DisposeAll();
            mSubscriptions.DisposeAll();
        }
    }

    // ── 编译期：把一个槽位的自动化条目变成图里的绑定 ──

    // 从活实例读参数清单（VST3 / CLAP / JSFX 走 IParameterInsert，VST2 有自己的查询口）。
    // 查不到（没活实例 / 参数清单变了）回空表 —— 没有参数清单就没有三级菜单，也不建绑定。
    internal static List<InsertParameter> GetLiveParameters(IMixInsert? insert)
    {
        try
        {
            return insert switch
            {
                IParameterInsert parameterInsert => [.. parameterInsert.Parameters],
                Vst2BridgeInsert vst2 => vst2.GetParameters().Select(p => new InsertParameter
                {
                    Id = (int)p.Id,
                    Name = p.Name,
                    MinValue = p.Min,
                    MaxValue = p.Max,
                    CurrentValue = p.Value,
                    IsDiscrete = p.IsDiscrete,
                }).ToList(),
                _ => [],
            };
        }
        catch (Exception)
        {
            return [];
        }
    }

    // 一个槽位的全部自动化条目 → 图绑定。没有条目 / 实例拿不到 / 参数不在清单里 → 跳过该条。
    internal static List<MixAutomationBinding> BuildBindings(InsertSlot slot, IMixInsert? insert, Project? project,
        out List<Binding> hostBindings)
    {
        hostBindings = [];

        if (insert == null || slot.Automations.Count == 0)
            return [];

        var parameters = GetLiveParameters(insert);

        var bindings = new List<MixAutomationBinding>();

        foreach (var kvp in slot.Automations)
        {
            var entry = kvp.Value;

            if (!long.TryParse(entry.ParamId, out long parameterId))
                continue;

            var parameter = parameters.FirstOrDefault(p => p.Id == parameterId);
            if (parameter == null)
                continue;

            if (parameter.MinValue >= parameter.MaxValue)
                continue;   // 死参数（min == max）没有可自动化的余地

            string keyId = InsertAutomationCatalog.MakeKeyId(slot.Id, parameterId);

            var hostBinding = new Binding(entry, keyId, project, parameter.MinValue, parameter.MaxValue,
                value => PushParameter(insert, (int)parameterId, value));
            hostBindings.Add(hostBinding);

            bindings.Add(new MixAutomationBinding(hostBinding.Evaluate, hostBinding.Push));
        }

        return bindings;
    }

    static bool PushParameter(IMixInsert insert, int parameterId, double value)
    {
        try
        {
            switch (insert)
            {
                case IParameterInsert parameterInsert:
                    parameterInsert.SetParameter(parameterId, value);
                    return true;
                case Vst2BridgeInsert vst2:
                    vst2.SetParameter(parameterId, value);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            // 推送失败（实例刚好被释放 / 桥忙）不算致命：下一块重试，或者随下一次编图重建绑定。
            HostLog.Debug($"mixer: an automated parameter could not be pushed ({ex.Message})");
            return false;
        }
    }
}

// 单点单调 Hermite 求值（与 HermiteInterpolation.Calculate 同一套公式与斜率口径，
// 只是标量版、零分配 —— legacy 的 TuneLab.Base 是冻结面，加不了方法）。
// 曲线不足两个点 / 越出锚点区间：按"区间外取端点值"的同一口径（Calculate 也是这么退的）。
internal static class InsertAutomationMath
{
    public static double Evaluate(ReadOnlySpan<Point> points, double x)
    {
        switch (points.Length)
        {
            case 0:
                return 0;
            case 1:
                return points[0].Y;
        }

        if (x <= points[0].X)
            return points[0].Y;

        int last = points.Length - 1;

        if (x >= points[last].X)
            return points[last].Y;

        // 二分找 x 落进的段：points[i].X <= x < points[i+1].X。
        int lo = 0;
        int hi = last;

        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;

            if (points[mid].X <= x)
                lo = mid;
            else
                hi = mid;
        }

        var a = points[lo];
        var b = points[hi];
        double slopeA = MonotonicSlope(points, lo);
        double slopeB = MonotonicSlope(points, hi);

        double d1 = x - a.X;
        double d2 = x - b.X;
        double t1 = d1 / (b.X - a.X);
        double t2 = d2 / (a.X - b.X);

        return
            (1.0 + 2.0 * t1) * t2 * t2 * a.Y +
            (1.0 + 2.0 * t2) * t1 * t1 * b.Y +
            d1 * t2 * t2 * slopeA +
            d2 * t1 * t1 * slopeB;
    }

    // Fritsch–Carlson 单调斜率（与 MonotonicHermiteSlopeCalculator 同一口径）。
    static double MonotonicSlope(ReadOnlySpan<Point> points, int index)
    {
        if (index == 0 || index == points.Length - 1)
            return 0;

        var point = points[index];
        double lastK = Slope(point, points[index - 1]);
        double nextK = Slope(point, points[index + 1]);
        double kk = lastK * nextK;

        return kk <= 0 ? 0 : 2 / (1 / lastK + 1 / nextK);
    }

    static double Slope(Point a, Point b) => (a.Y - b.Y) / (a.X - b.X);
}
