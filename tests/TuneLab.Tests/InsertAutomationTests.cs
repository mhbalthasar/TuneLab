using System;
using System.Collections.Generic;
using System.Linq;
using TuneLab.Audio;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.Foundation;
using TuneLab.SDK;
using TuneLab.UI;
using Xunit;

namespace TuneLab.Tests;

// [EP-MIXER-AUTO] 插入参数自动化：曲线数学、加性语义（基值 + 偏移 + 钳位）、
// 槽位数据的落盘往返、图绑定的聚合。
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

    // ── 加性语义：基值 + 偏移，钳位到参数量程 ──

    [Fact]
    public void Binding_ClampsBasePlusOffsetToTheParameterRange()
    {
        var slot = NewSlotWithAutomation(baseValue: 0.8, points: [new Point(0, -0.5)]);

        var host = BuildBinding(slot, min: 0, max: 1);
        var automation = slot.Automations.Values.Single();

        // 基值 0.8 + 偏移 -0.5 = 0.3。
        Assert.Equal(0.3, host.Evaluate(0), 12);

        // 偏移把和推出上限 → 钳位。
        automation.Curve.AddLine([new Point(0, 5)], 0);
        Assert.Equal(1, host.Evaluate(0), 12);

        // 偏移把和拉破下限 → 钳位。
        automation.Curve.AddLine([new Point(0, -5)], 0);
        Assert.Equal(0, host.Evaluate(0), 12);
    }

    [Fact]
    public void Binding_BaseEditFlowsIntoEvaluation()
    {
        var slot = NewSlotWithAutomation(baseValue: 0.5, points: []);
        var automation = slot.Automations.Values.Single();

        var host = BuildBinding(slot, min: 0, max: 2);

        // 空曲线 = 偏移 0：评估值就是基值。
        Assert.Equal(0.5, host.Evaluate(10), 12);

        // 面板改基值 → 曲线不变，评估跟着走。
        automation.BaseValue.Set(1.25);
        Assert.Equal(1.25, host.Evaluate(10), 12);
    }

    // ── 落盘往返 ──

    [Fact]
    public void Automations_SurviveSaveAndLoad()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tl-insertauto-" + Guid.NewGuid().ToString("N") + ".tlpx");

        try
        {
            var source = NewProject("主唱");
            var slot = AddSlot(source.Tracks[0], "VST3", "vst3:pro-q", "Pro-Q 4");

            var entry = new InsertSlotAutomation(slot, "7");
            entry.SetInfo(new InsertSlotAutomationInfo
            {
                ParamId = "7",
                BaseValue = -3.5,
                Points = [new Point(0, 0), new Point(480, 0.25)],
            });
            slot.Automations.Add("7", entry);

            MixerPersistence.Save(source, path);

            var target = NewProject("主唱");
            MixerPersistence.Load(target, path);

            var restored = target.Tracks[0].Inserts.Single().Automations;
            Assert.True(restored.TryGetValue("7", out var restoredEntry));

            Assert.Equal(-3.5, restoredEntry.BaseValue.Value, 12);

            var info = restoredEntry.GetInfo();
            Assert.Equal(2, info.Points.Count);
            Assert.Equal(0, info.Points[0].X, 12);
            Assert.Equal(0.25, info.Points[1].Y, 12);
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

    // ── 二期：钢琴窗 lane（Catalog 解析 / InsertAutomationView 适配）──

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
            var entry = NewEntry(slot, baseValue: 0.5, min: 0, max: 1);
            slot.Automations.Add(entry.ParamId, entry);

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

    [Fact]
    public void LaneConfig_UsesStoredParameterRangeAndBase()
    {
        var project = NewProject("t1");
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        var entry = NewEntry(slot, baseValue: -3.5, min: -12, max: 12);
        slot.Automations.Add("3", entry);

        var keyId = InsertAutomationCatalog.MakeKeyId(slot.Id, 3);
        var config = InsertAutomationCatalog.BuildLaneConfig(entry, keyId);

        Assert.Equal(-12, config.MinValue, 12);
        Assert.Equal(12, config.MaxValue, 12);
        Assert.Equal(-3.5, config.DefaultValue, 12);
    }

    [Fact]
    public void View_EvaluatesEffectiveValuesInPartRelativeTicks()
    {
        var (project, part) = NewProjectWithPart(pos: 960);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        // 锚点存工程绝对 tick：Pos 960 + 480，偏移 +0.5；基值 0.4 → 实参 0.9。
        var entry = NewEntry(slot, baseValue: 0.4, min: 0, max: 1,
            points: [new Point(960 + 480, 0.5)]);
        slot.Automations.Add("3", entry);

        var view = new InsertAutomationView(part, entry);

        // part 相对 tick 480 = 绝对 1440 = 正落在锚点上。
        Assert.Equal(0.9, view.GetValues([480])[0], 12);
        // 远处取端点值（仍 0.9）。
        Assert.Equal(0.9, view.GetValues([48000])[0], 12);

        // 钳位：把锚点偏移拉出量程，实参钳在上限。
        entry.Curve.SetInfo(new AutomationInfo { DefaultValue = 0, Points = [new Point(960 + 480, 5)] });
        Assert.Equal(1, view.GetValues([480])[0], 12);
    }

    [Fact]
    public void View_EditsTranslateBackIntoAbsoluteStorage()
    {
        var (project, part) = NewProjectWithPart(pos: 960);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        var entry = NewEntry(slot, baseValue: 0.4, min: 0, max: 1, points: []);
        slot.Automations.Add("3", entry);

        var view = new InsertAutomationView(part, entry);

        // 画点：part 相对 100 → 存储绝对 1060。
        view.InsertPoint(new AnchorPoint(100, 0.1));
        var stored = entry.Curve.Points.Single();
        Assert.Equal(1060, stored.Pos, 12);
        Assert.Equal(0.1, stored.Value, 12);

        // 呈现点读回来是 part 相对。
        Assert.Equal(100, view.Points.Single().Pos, 12);

        // AddLine 同样平移。
        view.AddLine([new AnchorPoint(200, 0.2), new AnchorPoint(300, 0.3)], 0);
        Assert.Equal(3, entry.Curve.Points.Count);
        Assert.Equal(960 + 200, entry.Curve.Points[1].Pos, 12);

        // Clear 与既有轨同语义：在区域两端补默认锚点（偏移 0 = 回到基值）。
        view.Clear(150, 350, 0);
        Assert.Equal(2, entry.Curve.Points.Count(p => p.Value == 0));
        Assert.Contains(entry.Curve.Points, p => p.Pos == 960 + 150 && p.Value == 0);
        Assert.Contains(entry.Curve.Points, p => p.Pos == 960 + 350 && p.Value == 0);

        // 区间删除按呈现坐标翻译。
        view.InsertPoint(new AnchorPoint(50, 0.1));
        view.InsertPoint(new AnchorPoint(60, 0.2));
        view.DeletePoints(view.Points.ToList());
        Assert.Empty(entry.Curve.Points);
    }

    [Fact]
    public void View_AnchorSelectionForwardsToStorage()
    {
        var (project, part) = NewProjectWithPart(pos: 0);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        var entry = NewEntry(slot, baseValue: 0.5, min: 0, max: 1,
            points: [new Point(0, 0.1), new Point(480, -0.1)]);
        slot.Automations.Add("3", entry);

        var view = new InsertAutomationView(part, entry);

        // 选中态住在存储锚点上（跨手势与既有轨一致）；呈现点是投影、每次重建也不断。
        view.Points[1].IsSelected = true;
        Assert.True(entry.Curve.Points[1].IsSelected);
        Assert.True(view.Points[1].IsSelected);
        Assert.False(view.Points[0].IsSelected);
    }

    [Fact]
    public void Routing_InsertKeyIsEffectiveWithEntryOnly()
    {
        var (project, part) = NewProjectWithPart(pos: 0);
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");
        var keyId = InsertAutomationCatalog.MakeKeyId(slot.Id, 3);
        var key = AutomationKey.Insert(keyId);

        // 没建条目：不有效。
        Assert.False(part.IsEffectiveAutomation(key));
        Assert.Null(part.GetEffectiveAutomation(key));

        var entry = NewEntry(slot, baseValue: 0.5, min: -1, max: 2);
        slot.Automations.Add("3", entry);

        Assert.True(part.IsEffectiveAutomation(key));
        var config = part.GetEffectiveAutomationConfig(key);
        Assert.Equal(-1, config.MinValue, 12);
        Assert.Equal(2, config.MaxValue, 12);

        var view = part.GetEffectiveAutomation(key);
        Assert.NotNull(view);
        Assert.Same(part, view!.Part);

        // lane 终值 = 实参（无 vibrato 可叠）。
        entry.Curve.SetInfo(new AutomationInfo { DefaultValue = 0, Points = [new Point(0, 0.5)] });
        Assert.Equal(1, part.GetFinalAutomationValues([0], key)[0], 12);
    }

    [Fact]
    public void Automations_RangeSurvivesSaveAndLoad()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tl-insertauto-" + Guid.NewGuid().ToString("N") + ".tlpx");

        try
        {
            var source = NewProject("主唱");
            var slot = AddSlot(source.Tracks[0], "VST3", "vst3:pro-q", "Pro-Q 4");

            var entry = new InsertSlotAutomation(slot, "7");
            entry.SetInfo(new InsertSlotAutomationInfo
            {
                ParamId = "7",
                BaseValue = -3.5,
                MinValue = -12,
                MaxValue = 12,
                Points = [],
            });
            slot.Automations.Add("7", entry);

            MixerPersistence.Save(source, path);

            var target = NewProject("主唱");
            MixerPersistence.Load(target, path);

            var restored = target.Tracks[0].Inserts.Single().Automations.Values.Single();
            Assert.Equal(-12, restored.MinValue.Value, 12);
            Assert.Equal(12, restored.MaxValue.Value, 12);
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
            try { System.IO.File.Delete(MixerPersistence.SidecarPath(path)); } catch { }
        }
    }

    static InsertSlotAutomation NewEntry(InsertSlot slot, double baseValue, double min, double max, Point[]? points = null)
    {
        var entry = new InsertSlotAutomation(slot, "3");
        entry.SetInfo(new InsertSlotAutomationInfo
        {
            ParamId = "3",
            BaseValue = baseValue,
            MinValue = min,
            MaxValue = max,
            Points = [.. points ?? []],
        });
        return entry;
    }

    static (Project, MidiPart) NewProjectWithPart(long pos)
    {
        TestVoices.EnsureBuiltIn();   // 建 MidiPart 会构造 SoundSource，要向 VoicesManager 求声明 config

        var part = new MidiPartInfo { Name = "p", Pos = pos, StartOffset = 0, EndOffset = 1920 };
        var track = new NativeTrackInfo { Name = "t1" };
        track.Parts.Add(part);
        var document = new ProjectDocument();
        document.SetProject(new Project(new ProjectInfo { Tracks = { track } }));
        return (document.Project!, (MidiPart)document.Project!.Tracks.First().Parts.First());
    }

    static void AddSend(ITrack track, MixerRoute target)
    {
        var send = new Send(track);
        send.Target.SetInfo(target);
        ((DataObjectList<Send>)track.Sends).Add(send);
    }

    // ── 测试脚手架 ──

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

    static InsertSlot NewSlotWithAutomation(double baseValue, Point[] points)
    {
        var project = NewProject("t1");
        var slot = AddSlot(project.Tracks[0], "VST3", "vst3:test", "Test");

        var entry = new InsertSlotAutomation(slot, "3");
        entry.SetInfo(new InsertSlotAutomationInfo { ParamId = "3", BaseValue = baseValue, Points = [.. points] });
        slot.Automations.Add("3", entry);

        // 绑定闭包持有曲线对象；槽位与工程对象要活到断言结束。
        mKeepAlive.Add(slot);

        return slot;
    }

    static readonly List<InsertSlot> mKeepAlive = [];

    static MixerAutomation.Binding BuildBinding(InsertSlot slot, double min, double max)
    {
        var entry = slot.Automations.Values.Single();
        return new MixerAutomation.Binding(entry, min, max, _ => true);
    }
}
