using System;
using System.Collections.Generic;
using System.Linq;
using TuneLab.Audio;
using TuneLab.Configs;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace TuneLab.Data;

// 插入参数自动化的"目录"：key ↔ 数据的解析、lane 的 AutomationConfig 构建、显示名。
//
// 【key.Id 的形状】"{slotId}:{paramId}"。slotId（"insert-<hex>"）全工程唯一且不带冒号，
// 通道归属（轨道 / AUX / Master）由槽位自带，所以 key 不需要编码通道 —— 全工程按槽位 id
// 一遍找回即可。EffectIndex 恒 -1（与 Voice/Lane 同款）。
//
// 【为什么量程存在条目上】lane 的值轴、锚点钳位都不能依赖"实例此刻活着"——重开工程、
// 图还没编译时 lane 也得有正确的轴（建条目时从活实例抄，见 InsertSlotAutomation）。
internal static class InsertAutomationCatalog
{
    public static string MakeKeyId(string slotId, long paramId) => $"{slotId}:{paramId}";

    public static bool TryParseKeyId(string keyId, out string slotId, out long paramId)
    {
        slotId = string.Empty;
        paramId = 0;

        int separator = keyId.LastIndexOf(':');
        if (separator <= 0 || separator == keyId.Length - 1)
            return false;

        slotId = keyId[..separator];

        return long.TryParse(keyId[(separator + 1)..], out paramId);
    }

    // 全工程按槽位 id 找回条目（各轨 / AUX / Master 的插入链都扫；条目数量级是个位数）。
    public static bool TryResolveEntry(Project project, string keyId, out InsertSlot slot, out InsertSlotAutomation entry)
    {
        slot = null!;
        entry = null!;

        if (!TryParseKeyId(keyId, out var slotId, out var paramId))
            return false;

        // 各轨
        foreach (var track in project.Tracks)
        {
            if (TryGetEntry(track.Inserts, slotId, paramId, out slot, out entry))
                return true;
        }

        // AUX
        foreach (var aux in project.Auxes)
        {
            if (TryGetEntry(aux.Inserts, slotId, paramId, out slot, out entry))
                return true;
        }

        // Master
        return TryGetEntry(project.MasterInserts, slotId, paramId, out slot, out entry);
    }

    public static bool TryResolveEntry(IMidiPart part, string keyId, out InsertSlot slot, out InsertSlotAutomation entry)
    {
        slot = null!;
        entry = null!;

        if (part.Track.Project is not Project project)
            return false;

        return TryResolveEntry(project, keyId, out slot, out entry);
    }

    static bool TryGetEntry(IReadOnlyDataObjectList<InsertSlot> slots, string slotId, long paramId,
        out InsertSlot slot, out InsertSlotAutomation entry)
    {
        foreach (var candidate in slots)
        {
            if (candidate.Id != slotId)
                continue;

            slot = candidate;

            return candidate.Automations.TryGetValue(paramId.ToString(), out entry!);
        }

        slot = null!;
        entry = null!;
        return false;
    }

    // ── lane 的 AutomationConfig（底部参数区与 tab 按钮共用同一份口径）──

    // 值轴 = **偏移量**空间 ±(Max−Min)：曲线只存偏移，真正值 = clamp(基值 + Σ偏移)。
    // 默认基线 = 0（"不干预"）：[0,100] → [-100,100]，[-50,100] → [-150,150]。
    // 颜色按 key 确定取（插入轨没有"声明序"可依，用 keyId 的稳定哈希在调色板里取，同一轨每次同色）。
    public static AutomationConfig BuildLaneConfig(InsertSlotAutomation entry, string keyId)
    {
        double min = Math.Min(entry.MinValue.Value, entry.MaxValue.Value);
        double max = Math.Max(entry.MinValue.Value, entry.MaxValue.Value);

        double range = max - min;
        if (range <= 0)
            range = 1;   // 死参数兜底：至少给个能画的轴

        return AutomationConfig.Create(-range, range)
            .WithDefault(0)
            .WithColor(TrackColors.PickBySeed(keyId));
    }

    // 真正值（背景用）：clamp(基值 + Σ 所有 part 的偏移, Min, Max)。
    // 传工程**绝对 tick**（渲染器的参数区是 part 相对 tick，调用方加 part.Pos 换算）。
    // 每个 part 的曲线只在**该 part 的时域内**贡献（与音频绑定同一口径，见 MixerAutomation.Binding）。
    // 背景把这条真正值映射到 lane 的**满高**（参数最小 = 底部、最大 = 顶部），与曲线的偏移轴分开：
    // 曲线 = 本 part 的偏移（未钳位），背景顶 = 所有 part 求和后的实际生效值（已钳位）。
    public static double[] EvaluateTrueValues(Project project, string keyId, InsertSlotAutomation entry,
        IReadOnlyList<double> absoluteTicks)
    {
        var values = new double[absoluteTicks.Count];

        foreach (var track in project.Tracks)
        {
            foreach (var part in track.Parts)
            {
                if (part is not IMidiPart midiPart || !midiPart.Automations.TryGetValue(keyId, out var curve))
                    continue;

                double pos = part.Pos.Value;
                double start = part.StartPos();
                double end = part.EndPos();

                var relative = new double[absoluteTicks.Count];
                for (int i = 0; i < relative.Length; i++)
                    relative[i] = absoluteTicks[i] - pos;

                var curveValues = curve.GetValues(relative);
                for (int i = 0; i < values.Length; i++)
                {
                    double tick = absoluteTicks[i];
                    if (tick >= start && tick <= end)
                        values[i] += curveValues[i];
                }
            }
        }

        double baseValue = entry.BaseValue.Value;
        double min = Math.Min(entry.MinValue.Value, entry.MaxValue.Value);
        double max = Math.Max(entry.MinValue.Value, entry.MaxValue.Value);

        for (int i = 0; i < values.Length; i++)
            values[i] = Math.Clamp(baseValue + values[i], min, max);

        return values;
    }

    // lane 显示名："FX 名 · 参数名"（tab 按钮与面板标题共用）。参数显示名按活实例现查
    //（名字不落盘，只存 id）；实例不在了退到 "#id"。
    public static string DisplayName(InsertSlot slot, InsertSlotAutomation entry)
    {
        string parameterName = "#" + entry.ParamId;

        if (long.TryParse(entry.ParamId, out long id))
        {
            var live = InsertInstanceFactory.Shared.TryGetLive(slot.Id, out var instance) && instance != null
                ? MixerAutomation.GetLiveParameters(instance).FirstOrDefault(p => p.Id == id)
                : null;

            if (live != null && !string.IsNullOrWhiteSpace(live.Name))
                parameterName = live.Name;
        }

        return slot.Name.Value + " · " + parameterName;
    }
}
