using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TuneLab.Audio;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.Foundation;
using TuneLab.SDK;
using TuneLab.UI;
using Xunit;

namespace TuneLab.Tests;

// [EP-MIXER-AUTO] 插入参数自动化：曲线数学、加性语义（基值 + Σ各 part 偏移 + 钳位）、
// 曲线按 part 分住（part 相对 tick、随 part 移动）、落盘 / 旧数据迁移、图绑定的聚合。
public class InsertAutomationTests
{
    // ── 标量 Hermite：与数组版同口径 ──

    [Fact]
    public void Evaluate_EmptyCurve_IsNeutralOffset()
    {
        Assert.Equal(0, InsertAutomationMath.Evaluate([], 123));
    }

    [Fact]
    public void Evaluate_SinglePoint_IsFlat()
    {
        Assert.Equal(0.25, InsertAutomationMath.Evaluate([new Point(10, 0.25)], 0));
        Assert.Equal(0.25, InsertAutomationMath.Evaluate([new Point(10, 0.25)], 1000));
    }

    [Fact]
    public void Evaluate_OutsideAnchors_TakesEndpointValues()
    {
        var points = new Point[] { new(0, 0), new(100, 1) };

        Assert.Equal(0, InsertAutomationMath.Evaluate(points, -50));
        Assert.Equal(1, InsertAutomationMath.Evaluate(points, 150));
    }

    [Fact]
    public void Evaluate_OnAnchors_IsExact()
    {
        var points = new Point[] { new(0, 0.1), new(50, 0.7), new(200, -0.3) };

        Assert.Equal(0.1, InsertAutomationMath.Evaluate(points, 0), 12);
        Assert.Equal(0.7, InsertAutomationMath.Evaluate(points, 50), 12);
        Assert.Equal(-0.3, InsertAutomationMath.Evaluate(points, 200), 12);
    }

    [Fact]
    public void Evaluate_BetweenAnchors_StaysWithinTheSegment()
    {
        // 单调 Hermite 的"单调"承诺：段内不越出两端值。
        var points = new Point[] { new(0, 0), new(100, 1) };

        for (double x = 0; x <= 100; x += 5)
        {
            double value = InsertAutomationMath.Evaluate(points, x);
            Assert.InRange(value, 0, 1);
        }
    }

    // ── 加性语义：基值 + Σ各 part 偏移，钳位到参数量程 ──

    [Fact]
    public void Binding_ClampsBasePlusOffsetToTheParameterRange()
    {
        var (project, part) = NewProjectWithPart(pos: 0);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        AddEntry(slot, baseValue: 0.8, min: 0, max: 1);
        var curve = AddCurve(part, slot, new Point(0, -0.5));

        var host = BuildBinding(project, slot, min: 0, max: 1);

        // 基值 0.8 + 偏移 -0.5 = 0.3。
        Assert.Equal(0.3, host.Evaluate(0), 12);

        // 偏移把和推出上限 → 钳位。
        curve.AddLine([new Point(0, 5)], 0);
        Assert.Equal(1, host.Evaluate(0), 12);

        // 偏移把和拉破下限 → 钳位。
        curve.AddLine([new Point(0, -5)], 0);
        Assert.Equal(0, host.Evaluate(0), 12);
    }

    [Fact]
    public void Binding_BaseEditFlowsIntoEvaluation()
    {
        var (project, part) = NewProjectWithPart(pos: 0);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        var entry = AddEntry(slot, baseValue: 0.5, min: 0, max: 2);
        AddCurve(part, slot);

        var host = BuildBinding(project, slot, min: 0, max: 2);

        // 空曲线 = 偏移 0：评估值就是基值。
        Assert.Equal(0.5, host.Evaluate(10), 12);

        // 面板改基值 → 曲线不变，评估跟着走。
        entry.BaseValue.Set(1.25);
        Assert.Equal(1.25, host.Evaluate(10), 12);
    }

    [Fact]
    public void Binding_SumsContributionsOfMultipleParts()
    {
        var (project, parts) = NewProjectWithParts(0, 960);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        AddEntry(slot, baseValue: 0.5, min: 0, max: 1);

        // 两个 part 各画一条偏移曲线（part 相对 tick）：同一参数被多个 part 控制 = 求和。
        AddCurve(parts[0], slot, new Point(0, 0.2));
        AddCurve(parts[1], slot, new Point(0, 0.3));

        var host = BuildBinding(project, slot, min: 0, max: 1);

        // 绝对 tick 100：part 0 时域 [0,1920] 内 → +0.2；part 1 时域 [960,2880]，不覆盖。
        Assert.Equal(0.7, host.Evaluate(100), 12);

        // 绝对 tick 1000：两个 part 都覆盖 → 0.5 + 0.2 + 0.3 = 1.0。
        Assert.Equal(1.0, host.Evaluate(1000), 12);

        // 绝对 tick 3000：都不在时域内 → 只剩基值。
        Assert.Equal(0.5, host.Evaluate(3000), 12);
    }

    [Fact]
    public void Binding_IgnoresContributionsOutsideThePartExtent()
    {
        var (project, part) = NewProjectWithPart(pos: 0);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        AddEntry(slot, baseValue: 0.5, min: 0, max: 1);
        AddCurve(part, slot, new Point(0, 0.5));

        var host = BuildBinding(project, slot, min: 0, max: 1);

        // part 时域 [0,1920]：内部生效，外部回到基值（否则端点值会一直延伸到全曲）。
        Assert.Equal(1.0, host.Evaluate(1920), 12);
        Assert.Equal(0.5, host.Evaluate(1921), 12);
    }

    [Fact]
    public void Binding_CurveMovesWithThePart()
    {
        var (project, part) = NewProjectWithPart(pos: 0);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        AddEntry(slot, baseValue: 0.5, min: 0, max: 1);
        AddCurve(part, slot, new Point(100, 0.25));

        var host = BuildBinding(project, slot, min: 0, max: 1);

        // part 相对 100 = 绝对 100。
        Assert.Equal(0.75, host.Evaluate(100), 12);

        // part 整体右移 480：曲线跟着走（绝对 580），原位置回到基值。
        part.Pos.Set(480);
        Assert.Equal(0.5, host.Evaluate(100), 12);
        Assert.Equal(0.75, host.Evaluate(580), 12);
    }

    // ── 背景：同轴真正值（Σ 后钳位、相对基值的偏移）──

    [Fact]
    public void TrueOffset_SumsAllPartsAndClampsThenSubtractsBase()
    {
        var (project, parts) = NewProjectWithParts(0, 960);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        var entry = AddEntry(slot, baseValue: 0.5, min: 0, max: 1);
        AddCurve(parts[0], slot, new Point(0, 0.2));
        AddCurve(parts[1], slot, new Point(0, 0.3));

        string keyId = InsertAutomationCatalog.MakeKeyId(slot.Id, 3);

        // tick 1000：0.5 + 0.2 + 0.3 = 1.0 → 偏移 0.5（正好到上限）。
        Assert.Equal(0.5, InsertAutomationCatalog.EvaluateTrueOffset(project, keyId, entry, [1000])[0], 12);

        // 基值改到 0.9 → 和 1.4 被钳到 1 → 偏移 0.1。
        entry.BaseValue.Set(0.9);
        Assert.Equal(0.1, InsertAutomationCatalog.EvaluateTrueOffset(project, keyId, entry, [1000])[0], 12);

        // 两个 part 都不覆盖的 tick：真正值 = 基值 → 偏移 0。
        Assert.Equal(0, InsertAutomationCatalog.EvaluateTrueOffset(project, keyId, entry, [5000])[0], 12);
    }

    // ── lane 值轴：偏移空间 ±range，默认 0 ──

    [Theory]
    [InlineData(0, 100, -100, 100)]
    [InlineData(-50, 100, -150, 150)]
    [InlineData(-12, 12, -24, 24)]
    public void LaneConfig_IsSymmetricOffsetSpace(double min, double max, double expectedMin, double expectedMax)
    {
        var project = NewProject("t1");
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        var entry = AddEntry(slot, baseValue: -3.5, min: min, max: max);

        var config = InsertAutomationCatalog.BuildLaneConfig(entry, InsertAutomationCatalog.MakeKeyId(slot.Id, 3));

        Assert.Equal(expectedMin, config.MinValue, 12);
        Assert.Equal(expectedMax, config.MaxValue, 12);
        Assert.Equal(0, config.DefaultValue, 12);
    }

    // ── 曲线住在 part 自己的自动化表里：part 相对 tick、偏移量、编辑原语直接落在真数据上 ──

    [Fact]
    public void Curve_LivesInThePartMapInPartRelativeTicks()
    {
        var (project, part) = NewProjectWithPart(pos: 960);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        AddEntry(slot, baseValue: 0.4, min: 0, max: 1);

        string keyId = InsertAutomationCatalog.MakeKeyId(slot.Id, 3);
        var key = AutomationKey.Insert(keyId);

        // 条目建了但还没画：没有曲线，偏移恒 0（不干预）。
        Assert.Null(part.GetEffectiveAutomation(key));
        Assert.Equal(0, part.GetFinalAutomationValues([480], key)[0], 12);

        // 画点：part 相对 tick 直接存，不做任何投影。
        var curve = part.AddEffectiveAutomation(key);
        Assert.NotNull(curve);
        curve!.InsertPoint(new AnchorPoint(100, 0.1));

        var stored = part.Automations[keyId].Points.Single();
        Assert.Equal(100, stored.Pos, 12);
        Assert.Equal(0.1, stored.Value, 12);

        // lane 终值 = 偏移量本身（不加基值、不钳位；真正值见 EvaluateTrueOffset）。
        Assert.Equal(0.1, part.GetFinalAutomationValues([100], key)[0], 12);

        // 第二次调用取回同一对象（不重建）。
        Assert.Same(curve, part.GetEffectiveAutomation(key));
        Assert.Same(curve, part.AddEffectiveAutomation(key));
    }

    [Fact]
    public void Curve_EditsUsePartRelativeTicksAndZeroIsNeutral()
    {
        var (project, part) = NewProjectWithPart(pos: 960);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        AddEntry(slot, baseValue: 0.4, min: 0, max: 1);

        string keyId = InsertAutomationCatalog.MakeKeyId(slot.Id, 3);
        var curve = part.AddAutomation(keyId)!;

        // AddLine 直接是 part 相对 tick。
        curve.AddLine([new AnchorPoint(200, 0.2), new AnchorPoint(300, 0.3)], 0);
        Assert.Equal(2, curve.Points.Count);
        Assert.Equal(200, curve.Points[0].Pos, 12);

        // Clear 与既有轨同语义：在区域两端补默认锚点（偏移 0 = 回到不干预）。
        curve.Clear(150, 350, 0);
        Assert.Contains(curve.Points, p => p.Pos == 150 && p.Value == 0);
        Assert.Contains(curve.Points, p => p.Pos == 350 && p.Value == 0);

        // 区间删除按 part 相对坐标。
        curve.InsertPoint(new AnchorPoint(50, 0.1));
        curve.InsertPoint(new AnchorPoint(60, 0.2));
        curve.DeletePoints(curve.Points.ToList());
        Assert.Empty(curve.Points);
    }

    [Fact]
    public void Curve_AnchorSelectionLivesOnTheStoredAnchors()
    {
        var (project, part) = NewProjectWithPart(pos: 0);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        AddEntry(slot, baseValue: 0.5, min: 0, max: 1);

        string keyId = InsertAutomationCatalog.MakeKeyId(slot.Id, 3);
        var curve = AddCurve(part, slot, new Point(0, 0.1), new Point(480, -0.1));

        // 选中态住在真锚点上（跨手势与既有轨一致）。
        curve.Points[1].IsSelected = true;
        Assert.True(part.Automations[keyId].Points[1].IsSelected);
        Assert.False(part.Automations[keyId].Points[0].IsSelected);
    }

    [Fact]
    public void Routing_InsertKeyNeedsTheEntryAndTheCurveIsPerPart()
    {
        var (project, part) = NewProjectWithPart(pos: 0);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        string keyId = InsertAutomationCatalog.MakeKeyId(slot.Id, 3);
        var key = AutomationKey.Insert(keyId);

        // 没建条目：不有效、也没有 lane 轴。
        Assert.False(part.IsEffectiveAutomation(key));
        Assert.Null(part.GetEffectiveAutomation(key));
        Assert.Null(part.AddEffectiveAutomation(key));

        AddEntry(slot, baseValue: 0.5, min: -1, max: 2);

        Assert.True(part.IsEffectiveAutomation(key));
        var config = part.GetEffectiveAutomationConfig(key);
        Assert.Equal(-3, config.MinValue, 12);   // ±range = ±(2 − (−1))
        Assert.Equal(3, config.MaxValue, 12);
        Assert.Equal(0, config.DefaultValue, 12);

        // 条目存在、曲线不存在：Get 不创建（悬浮 / 收集锚点路径不能顺手建数据）。
        Assert.Null(part.GetEffectiveAutomation(key));

        var curve = part.AddEffectiveAutomation(key);
        Assert.NotNull(curve);
        curve!.SetInfo(new AutomationInfo { DefaultValue = 0, Points = [new Point(0, 0.5)] });

        // 终值 = 偏移（无 vibrato 可叠）。
        Assert.Equal(0.5, part.GetFinalAutomationValues([0], key)[0], 12);
    }

    [Fact]
    public void Curves_AreDormantWithoutTheEntry()
    {
        var (project, part) = NewProjectWithPart(pos: 0);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        var entry = AddEntry(slot, baseValue: 0.5, min: 0, max: 1);
        string keyId = InsertAutomationCatalog.MakeKeyId(slot.Id, 3);
        AddCurve(part, slot, new Point(0, 0.5));

        // 删条目 = 曲线变休眠数据：不生效、也不再是有效 lane。
        slot.Automations.Remove("3");

        Assert.False(part.IsEffectiveAutomation(AutomationKey.Insert(keyId)));
        Assert.Equal(0, part.GetFinalAutomationValues([0], AutomationKey.Insert(keyId))[0], 12);

        // 重新添加同一参数：原曲线恢复生效（一次 undo 也能整条恢复）。
        slot.Automations.Add("3", entry);
        Assert.Equal(0.5, part.GetFinalAutomationValues([0], AutomationKey.Insert(keyId))[0], 12);
    }

    // ── 落盘：条目（基值 / 量程）走槽位，曲线走 part ──

    [Fact]
    public void Automations_SurviveSaveAndLoad()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tl-insertauto-" + Guid.NewGuid().ToString("N") + ".tlpx");

        try
        {
            var source = NewProject("主唱");
            var slot = AddSlot(source.Tracks[0], "VST3", "vst3:pro-q", "Pro-Q 4");
            AddEntry(slot, baseValue: -3.5, min: -12, max: 12);

            MixerPersistence.Save(source, path);

            var target = NewProject("主唱");
            MixerPersistence.Load(target, path);

            var restored = target.Tracks[0].Inserts.Single().Automations.Values.Single();
            Assert.Equal(-3.5, restored.BaseValue.Value, 12);
            Assert.Equal(-12, restored.MinValue.Value, 12);
            Assert.Equal(12, restored.MaxValue.Value, 12);
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
            try { System.IO.File.Delete(MixerPersistence.SidecarPath(path)); } catch { }
        }
    }

    [Fact]
    public void InsertCurves_SurvivePartInfoRoundtrip()
    {
        var (project, part) = NewProjectWithPart(pos: 960);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        AddEntry(slot, baseValue: 0.4, min: 0, max: 1);
        AddCurve(part, slot, new Point(100, 0.25));

        string keyId = InsertAutomationCatalog.MakeKeyId(slot.Id, 3);

        // part 的 DTO 里就有这条曲线（任意键，part 相对 tick）。
        var info = part.GetInfo();
        Assert.True(info.Automations.ContainsKey(keyId));
        Assert.Equal(100, info.Automations[keyId].Points.Single().X, 12);

        // 读回：曲线仍是 part 相对空间，随 part 走。
        var (project2, part2) = NewProjectWithPart(pos: 0);
        part2.SetInfo(info);

        Assert.True(part2.Automations.TryGetValue(keyId, out var restored));
        Assert.Equal(100, restored!.Points.Single().Pos, 12);
        Assert.Equal(0.25, restored.Points.Single().Value, 12);
    }

    [Fact]
    public void LegacyAbsoluteCurve_MigratesIntoTheOwnerTrackPart()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tl-insertauto-" + Guid.NewGuid().ToString("N") + ".tlpx");

        try
        {
            var source = NewProject("t1");
            var slot = AddSlot(source.Tracks[0], "VST3", "vst3:test", "Test");
            AddEntry(slot, baseValue: 0.5, min: 0, max: 1);

            MixerPersistence.Save(source, path);

            // 手工写回二期首版的遗留曲线（槽位 DTO、工程绝对 tick）。
            string sidecar = MixerPersistence.SidecarPath(path);
            var json = JsonNode.Parse(File.ReadAllText(sidecar))!;
            var automation = json["Tracks"]![0]!["Inserts"]![0]!["Automations"]![0]!;
            automation["Points"] = new JsonArray(new JsonObject { ["Pos"] = 1060, ["Value"] = 0.25 });
            File.WriteAllText(sidecar, json.ToJsonString());

            // 目标工程里 part 在 960：绝对 1060 → part 相对 100。
            var (target, part) = NewProjectWithPart(pos: 960);
            MixerPersistence.Load(target, path);

            string keyId = InsertAutomationCatalog.MakeKeyId(slot.Id, 3);
            Assert.True(part.Automations.TryGetValue(keyId, out var migrated));
            Assert.Equal(100, migrated!.Points.Single().Pos, 12);
            Assert.Equal(0.25, migrated.Points.Single().Value, 12);
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
            try { System.IO.File.Delete(MixerPersistence.SidecarPath(path)); } catch { }
        }
    }

    // ── 图绑定聚合 ──

    [Fact]
    public void GraphBuilder_AggregatesAutomationsAcrossNodes()
    {
        var builder = new TuneLab.ExperimentalPatch.MixerEngine.MixGraphBuilder();

        var master = builder.AddNode("master", TuneLab.ExperimentalPatch.MixerEngine.MixNodeKind.Master);
        var aux = builder.AddNode("aux-1", TuneLab.ExperimentalPatch.MixerEngine.MixNodeKind.Aux);
        var device = builder.AddNode("device", TuneLab.ExperimentalPatch.MixerEngine.MixNodeKind.Device);
        builder.Connect(aux, device);

        var pushed = new List<double>();
        aux.AddAutomations(
        [
            new(
                tick => Math.Clamp(tick / 1000.0, 0, 1),
                value => { pushed.Add(value); return true; }),
        ]);

        var snapshot = builder.Build();
        var binding = Assert.Single(snapshot.Automations);

        // 值变化才推：首块必推，同值不再推。
        binding.Apply(0);
        binding.Apply(500);      // 0.5
        binding.Apply(600);      // 0.6
        binding.Apply(600);

        Assert.Equal([0, 0.5, 0.6], pushed);
    }

    [Fact]
    public void Binding_PushFailure_KeepsRetrying()
    {
        var builder = new TuneLab.ExperimentalPatch.MixerEngine.MixGraphBuilder();
        var master = builder.AddNode("master", TuneLab.ExperimentalPatch.MixerEngine.MixNodeKind.Master);

        bool shouldFail = true;
        var pushed = new List<double>();

        master.AddAutomations(
        [
            new(
                _ => 0.42,
                value =>
                {
                    if (shouldFail)
                        return false;

                    pushed.Add(value);
                    return true;
                }),
        ]);

        var binding = builder.Build().Automations.Single();

        binding.Apply(0);   // 推送失败：不记"已推"，下块重试
        binding.Apply(100);

        shouldFail = false;
        binding.Apply(200); // 这次推出去

        Assert.Equal([0.42], pushed);
    }

    // ── 音流通道枚举（三级菜单的第一级）──

    [Fact]
    public void FlowChannels_FollowOutputAndSendsAndRequireFx()
    {
        var project = NewProject("主唱", "和声");
        var aux = project.AddAux("AUX 1");

        var lead = project.Tracks[0];
        lead.Output.SetInfo(new MixerRoute(MixerOutputKind.Aux, aux.Id, "AUX 1"));
        AddSend(lead, MixerRoute.Master);

        // 沿途都没有 FX：一个都不列（音频经过但没有可自动化的东西）。
        Assert.Empty(InsertAutomationsController.CollectFlowChannels(lead, project));

        AddSlot(lead, "VST3", "vst3:a", "A");
        var onlyTrack = InsertAutomationsController.CollectFlowChannels(lead, project);
        Assert.Single(onlyTrack);
        Assert.Equal("Track:主唱", onlyTrack[0].MenuName);

        // 音频经 output 流到 AUX：AUX 带了 FX 就出现。
        var auxSlot = new InsertSlot("insert-aux", "VST3", "vst3:b", "B", aux);
        ((DataObjectList<InsertSlot>)aux.Inserts).Add(auxSlot);
        var trackAndAux = InsertAutomationsController.CollectFlowChannels(lead, project);
        Assert.Equal(2, trackAndAux.Count);
        Assert.Equal("Aux:AUX 1", trackAndAux[1].MenuName);

        // 音频经 send 流到 Master：Master 带了 FX 也出现。
        var masterSlot = new InsertSlot("insert-master", "VST3", "vst3:c", "C", project);
        project.MasterInserts.Add(masterSlot);
        var all = InsertAutomationsController.CollectFlowChannels(lead, project);
        Assert.Equal(3, all.Count);
        Assert.Equal("Master", all[2].MenuName);
    }

    // ── 目录：key 解析 / 全工程按槽位找回 ──

    [Fact]
    public void Catalog_KeyIdRoundtrips()
    {
        Assert.True(InsertAutomationCatalog.TryParseKeyId("insert-abc123:42", out var slotId, out var paramId));
        Assert.Equal("insert-abc123", slotId);
        Assert.Equal(42, paramId);

        Assert.False(InsertAutomationCatalog.TryParseKeyId(":42", out _, out _));
        Assert.False(InsertAutomationCatalog.TryParseKeyId("insert-abc:", out _, out _));
        Assert.False(InsertAutomationCatalog.TryParseKeyId("insert-abc:x", out _, out _));
    }

    [Fact]
    public void Catalog_ResolvesEntriesAcrossTrackAuxAndMaster()
    {
        var project = NewProject("主唱");
        var aux = project.AddAux("AUX 1");

        var trackSlot = AddSlot(project.Tracks[0], "VST3", "vst3:a", "A");
        var auxSlot = new InsertSlot("insert-aux", "VST3", "vst3:b", "B", aux);
        ((DataObjectList<InsertSlot>)aux.Inserts).Add(auxSlot);
        var masterSlot = new InsertSlot("insert-master", "VST3", "vst3:c", "C", project);
        project.MasterInserts.Add(masterSlot);

        foreach (var slot in new[] { trackSlot, auxSlot, masterSlot })
        {
            var entry = AddEntry(slot, baseValue: 0.5, min: 0, max: 1);

            Assert.True(InsertAutomationCatalog.TryResolveEntry(project,
                InsertAutomationCatalog.MakeKeyId(slot.Id, 3), out _, out var resolved));
            Assert.Same(entry, resolved);
        }

        // 不存在的槽位 / 条目都认不出。
        Assert.False(InsertAutomationCatalog.TryResolveEntry(project,
            InsertAutomationCatalog.MakeKeyId("insert-nope", 3), out _, out _));
        Assert.False(InsertAutomationCatalog.TryResolveEntry(project,
            InsertAutomationCatalog.MakeKeyId(trackSlot.Id, 99), out _, out _));
    }

    // ── 测试脚手架 ──

    static InsertSlotAutomation AddEntry(InsertSlot slot, double baseValue, double min, double max)
    {
        var entry = new InsertSlotAutomation(slot, "3");
        entry.SetInfo(new InsertSlotAutomationInfo
        {
            ParamId = "3",
            BaseValue = baseValue,
            MinValue = min,
            MaxValue = max,
        });
        slot.Automations.Add("3", entry);
        return entry;
    }

    // 建这个 part 自己的偏移曲线（part 相对 tick）；点为空 = 不贡献（绑定跳过空曲线）。
    static IAutomation AddCurve(MidiPart part, InsertSlot slot, params Point[] points)
    {
        string keyId = InsertAutomationCatalog.MakeKeyId(slot.Id, 3);
        var curve = part.AddAutomation(keyId);
        Assert.NotNull(curve);
        curve!.SetInfo(new AutomationInfo { DefaultValue = 0, Points = [.. points] });
        return curve;
    }

    static MixerAutomation.Binding BuildBinding(Project project, InsertSlot slot, double min, double max)
    {
        var entry = slot.Automations.Values.Single();
        string keyId = InsertAutomationCatalog.MakeKeyId(slot.Id, 3);
        return new MixerAutomation.Binding(entry, keyId, project, min, max, _ => true);
    }

    static (Project, MidiPart) NewProjectWithPart(long pos)
    {
        var (project, parts) = NewProjectWithParts(pos);
        return (project, parts[0]);
    }

    static (Project, List<MidiPart>) NewProjectWithParts(params long[] positions)
    {
        TestVoices.EnsureBuiltIn();   // 建 MidiPart 会构造 SoundSource，要向 VoicesManager 求声明 config

        var track = new NativeTrackInfo { Name = "t1" };
        foreach (var pos in positions)
            track.Parts.Add(new MidiPartInfo { Name = "p" + pos, Pos = pos, StartOffset = 0, EndOffset = 1920 });

        var document = new ProjectDocument();
        document.SetProject(new Project(new ProjectInfo { Tracks = { track } }));
        return (document.Project!, [.. document.Project!.Tracks.First().Parts.Cast<MidiPart>()]);
    }

    static void AddSend(ITrack track, MixerRoute target)
    {
        var send = new Send(track);
        send.Target.SetInfo(target);
        ((DataObjectList<Send>)track.Sends).Add(send);
    }

    static Project NewProject(params string[] trackNames)
    {
        var info = new ProjectInfo();
        foreach (var name in trackNames)
            info.Tracks.Add(new NativeTrackInfo { Name = name });

        var document = new ProjectDocument();
        document.SetProject(new Project(info));
        return document.Project!;
    }

    static InsertSlot AddSlot(ITrack track, string format, string pluginId, string name)
    {
        var slot = new InsertSlot("insert-" + Guid.NewGuid().ToString("N"), format, pluginId, name,
            (DataObject)track);
        ((DataObjectList<InsertSlot>)track.Inserts).Add(slot);
        return slot;
    }
}
