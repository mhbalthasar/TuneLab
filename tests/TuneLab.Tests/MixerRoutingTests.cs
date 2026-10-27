using System.Linq;
using TuneLab.Audio;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.Foundation;
using TuneLab.SDK;
using Xunit;

namespace TuneLab.Tests;

// M4 的封条：AUX / output / send 的模型层行为。
//
// 这里测的是"路由是一等数据"这件事：增删进 undo、成环被拒、删 AUX 不丢路由。
// 引擎侧的渲染由 MixerAudioSeamTests 覆盖。
public class MixerRoutingTests
{
    static (ProjectDocument Document, Project Project, MixerModelAdapter Model) Create(params NativeTrackInfo[] tracks)
    {
        var document = new ProjectDocument();
        var info = new ProjectInfo();
        foreach (var track in tracks)
            info.Tracks.Add(track);

        document.SetProject(new Project(info));

        var project = document.Project!;
        return (document, project, new MixerModelAdapter(project));
    }

    static NativeTrackInfo Track(string name) => new() { Name = name };

    static IMixerOutput Output(MixerModelAdapter model, MixerOutputKind kind, string? id = null)
        => model.Outputs.First(o => o.Kind == kind && (id == null || o.Id == id));

    [Fact]
    public void AddAux_AddsChannelAndOutput()
    {
        var (_, project, model) = Create(Track("t1"));

        using (model)
        {
            model.AddAux();

            Assert.Contains(model.Channels, c => c.Kind == MixerChannelKind.Aux);
            Assert.Contains(model.Outputs, o => o.Kind == MixerOutputKind.Aux);
            Assert.Single(project.Auxes);

            project.RemoveAux(project.Auxes[0]);
        }
    }

    [Fact]
    public void RenamingAnAux_FollowsIntoOutputsAndSendTargets()
    {
        var (_, project, model) = Create(Track("t1"));

        using (model)
        {
            model.AddAux();

            var aux = model.Channels.First(c => c.Kind == MixerChannelKind.Aux);
            var track = model.Channels.First(c => c.Kind == MixerChannelKind.Track);

            var send = track.AddSend(Output(model, MixerOutputKind.Aux, aux.Id));

            aux.Name = "BBB";

            // 名字是**活读**的：output 菜单 / 通道条短名 / 已存在的 send 目标行都必须跟着变，
            // 而不是停在构造时拷下来的那个字符串（那是"改名了，别处还写 AUX 1"的根因）。
            Assert.Equal("BBB", Output(model, MixerOutputKind.Aux, aux.Id).Name);
            Assert.Equal("BBB", send.Target.Name);

            project.RemoveAux(project.Auxes[0]);
        }
    }

    [Fact]
    public void CanSendTo_RejectsSelfAndCycles()
    {
        var (_, project, model) = Create(Track("t1"));

        using (model)
        {
            model.AddAux();
            model.AddAux();

            var auxes = model.Channels.Where(c => c.Kind == MixerChannelKind.Aux).ToArray();
            var track = model.Channels.First(c => c.Kind == MixerChannelKind.Track);

            Assert.True(track.CanSendTo(Output(model, MixerOutputKind.Aux, auxes[0].Id)));

            // 输出到自己：菜单里不该出现（点了也会被引擎拒绝）。
            Assert.False(auxes[0].CanSendTo(Output(model, MixerOutputKind.Aux, auxes[0].Id)));

            // 成环：AUX1 → AUX2 之后，AUX2 不能再 → AUX1。
            auxes[0].Output = Output(model, MixerOutputKind.Aux, auxes[1].Id);
            Assert.False(auxes[1].CanSendTo(Output(model, MixerOutputKind.Aux, auxes[0].Id)));

            // Master 恒 false（它没有 send）。
            Assert.False(model.Master.CanSendTo(Output(model, MixerOutputKind.Master)));

            foreach (var bus in project.Auxes.ToArray())
                project.RemoveAux(bus);
        }
    }

    // [EP-MIXER-27] 侧链源：自己 / Master 不能当源；已经成环的源也不列出来。
    [Fact]
    public void CanSidechainFrom_RejectsSelfMasterAndCycles()
    {
        var (_, project, model) = Create(Track("t1"), Track("t2"));

        using (model)
        {
            var tracks = model.Channels.Where(c => c.Kind == MixerChannelKind.Track).ToArray();

            // t2 的插入可以吃 t1 的侧链（t1 → t2）。
            Assert.True(tracks[1].CanSidechainFrom(tracks[0]));

            // 自己不能当自己的源。
            Assert.False(tracks[1].CanSidechainFrom(tracks[1]));

            // Master 是终点，不能当源。
            Assert.False(tracks[1].CanSidechainFrom(model.Master));

            // 让 t1 的插入吃 t2 的侧链（t2 → t1）。现在再加 t1 → t2 就是环。
            var list = (DataObjectList<InsertSlot>)project.Tracks[0].Inserts;
            var slot = new InsertSlot("insert-sidechain", "VST3", "vst3:test", "Test", list);
            list.Add(slot);
            slot.SidechainId.SetInfo(tracks[1].Id);

            Assert.False(tracks[1].CanSidechainFrom(tracks[0]));
        }
    }

    // [EP-MIXER-27] 删 AUX 时把指向它的侧链引用一起清掉（同一个 merge 作用域）。
    [Fact]
    public void RemovingAux_ClearsSidechainRefsToIt()
    {
        var (_, project, model) = Create(Track("t1"));

        using (model)
        {
            model.AddAux();
            var aux = model.Channels.First(c => c.Kind == MixerChannelKind.Aux);

            var list = (DataObjectList<InsertSlot>)project.Tracks[0].Inserts;
            var slot = new InsertSlot("insert-sidechain", "VST3", "vst3:test", "Test", list);
            list.Add(slot);
            slot.SidechainId.SetInfo(aux.Id);

            model.RemoveAux(aux);

            Assert.Equal(string.Empty, slot.SidechainId.Value);
        }
    }

    [Fact]
    public void TrackOutput_CanGoToAnAux()
    {
        var (_, project, model) = Create(Track("t1"));

        using (model)
        {
            model.AddAux();
            var aux = model.Channels.First(c => c.Kind == MixerChannelKind.Aux);
            var track = model.Channels.First(c => c.Kind == MixerChannelKind.Track);

            track.Output = Output(model, MixerOutputKind.Aux, aux.Id);

            Assert.Equal(aux.Id, track.Output.Id);
            Assert.Equal(aux.Id, project.Tracks[0].Output.Value.Id);

            project.RemoveAux(project.Auxes[0]);
        }
    }

    [Fact]
    public void RemovingAux_RepointsOutputAndSendsToMaster()
    {
        var (_, project, model) = Create(Track("t1"));

        using (model)
        {
            model.AddAux();
            var aux = model.Channels.First(c => c.Kind == MixerChannelKind.Aux);
            var track = model.Channels.First(c => c.Kind == MixerChannelKind.Track);

            track.Output = Output(model, MixerOutputKind.Aux, aux.Id);
            track.AddSend(Output(model, MixerOutputKind.Aux, aux.Id));

            model.RemoveAux(aux);

            // 静默丢路由比报错更糟：必须改指 Master（DESIGN.md §4.1）。
            var reloaded = model.Channels.First(c => c.Kind == MixerChannelKind.Track);
            Assert.Equal(MixerOutputKind.Master, reloaded.Output.Kind);
            Assert.All(reloaded.Sends, send => Assert.Equal(MixerOutputKind.Master, send.Target.Kind));
            Assert.Empty(project.Auxes);
        }
    }

    [Fact]
    public void Output_CycleIsRefused()
    {
        var (_, project, model) = Create(Track("t1"));

        using (model)
        {
            model.AddAux();
            model.AddAux();

            var aux1 = model.Channels.First(c => c.Kind == MixerChannelKind.Aux);
            var aux2 = model.Channels.Last(c => c.Kind == MixerChannelKind.Aux);

            // aux1 → aux2 合法。
            aux1.Output = Output(model, MixerOutputKind.Aux, aux2.Id);
            Assert.Equal(aux2.Id, aux1.Output.Id);

            // aux2 → aux1 会成环，必须被拒绝（保持原来的 Master）。
            aux2.Output = Output(model, MixerOutputKind.Aux, aux1.Id);
            Assert.Equal(MixerOutputKind.Master, aux2.Output.Kind);

            foreach (var aux in project.Auxes.ToArray())
                project.RemoveAux(aux);
        }
    }

    [Fact]
    public void Send_AddIsOneUndoStep()
    {
        var (document, project, model) = Create(Track("t1"));

        using (model)
        {
            var track = model.Channels[0];
            track.AddSend(Output(model, MixerOutputKind.Master));

            Assert.Single(project.Tracks[0].Sends);

            document.Undo();
            Assert.Empty(project.Tracks[0].Sends);

            // 界面读的是活列表，undo 之后不该还显示那条 send。
            Assert.Empty(model.Channels[0].Sends);
        }
    }

    [Fact]
    public void Send_CannotTargetItself()
    {
        var (_, project, model) = Create(Track("t1"));

        using (model)
        {
            model.AddAux();
            var aux = model.Channels.First(c => c.Kind == MixerChannelKind.Aux);

            // AUX 不能 send 到自己。
            aux.AddSend(Output(model, MixerOutputKind.Aux, aux.Id));
            Assert.Empty(project.Auxes[0].Sends);

            project.RemoveAux(project.Auxes[0]);
        }
    }

    [Fact]
    public void RemovingAux_IsOneUndoStep()
    {
        var (document, project, model) = Create(Track("t1"));

        using (model)
        {
            model.AddAux();
            var aux = model.Channels.First(c => c.Kind == MixerChannelKind.Aux);
            var track = model.Channels.First(c => c.Kind == MixerChannelKind.Track);
            track.Output = Output(model, MixerOutputKind.Aux, aux.Id);

            model.RemoveAux(aux);

            Assert.Empty(project.Auxes);
            Assert.Equal(MixerOutputKind.Master,
                model.Channels.First(c => c.Kind == MixerChannelKind.Track).Output.Kind);

            // 一步 undo 要把"删 AUX + 改指"一起还原 —— 所以界面上不需要确认框。
            document.Undo();

            Assert.Single(project.Auxes);
            Assert.Equal(aux.Id, project.Tracks[0].Output.Value.Id);
        }
    }

    [Fact]
    public void AuxGain_IsUndoable()
    {
        var (document, project, model) = Create(Track("t1"));

        using (model)
        {
            model.AddAux();
            var aux = model.Channels.First(c => c.Kind == MixerChannelKind.Aux);

            aux.GainDb = -6;
            Assert.Equal(-6, project.Auxes[0].Gain.Value);

            document.Undo();
            Assert.Equal(0, project.Auxes[0].Gain.Value);

            project.RemoveAux(project.Auxes[0]);
        }
    }
}
