using System;
using System.IO;
using System.Linq;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.Extensions.Formats.TLP;
using TuneLab.Foundation;
using TuneLab.SDK;
using Xunit;

namespace TuneLab.Tests;

// 路由落盘（sidecar）的封条。
//
// 为什么是 sidecar 而不是写进 .tlpx：见 DESIGN.md §9 —— 原版保存会按 DTO 重写整个文件，
// 我们写进 .tlpx 的键会被丢掉；sidecar 原版不认识也不会动，所以"原版往返"之后路由还在。
public class MixerPersistenceTests
{
    static Project NewProject(params string[] trackNames)
    {
        var info = new ProjectInfo();
        foreach (var name in trackNames)
            info.Tracks.Add(new NativeTrackInfo { Name = name });

        var document = new ProjectDocument();
        document.SetProject(new Project(info));
        return document.Project!;
    }

    static string TempProjectPath()
        => Path.Combine(Path.GetTempPath(), "tl-mixer-" + Guid.NewGuid().ToString("N") + ".tlpx");

    static void Cleanup(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(MixerPersistence.SidecarPath(path));
        }
        catch
        {
            // 测试清理失败不影响结论。
        }
    }

    static void AddSend(Project project, ITrack track, MixerRoute target, double gainDb)
    {
        var send = new Send(track);
        send.Target.SetInfo(target);
        send.Gain.SetInfo(gainDb);
        ((DataObjectList<Send>)track.Sends).Add(send);
    }

    [Fact]
    public void SaveThenLoad_RestoresAuxAndRouting()
    {
        string path = TempProjectPath();
        try
        {
            var source = NewProject("主唱", "和声");
            var aux = source.AddAux("AUX 1");
            aux.Gain.SetInfo(-6);
            aux.Pan.SetInfo(0.25);

            var lead = source.Tracks[0];
            lead.Output.SetInfo(new MixerRoute(MixerOutputKind.Aux, aux.Id, "AUX 1"));
            AddSend(source, lead, MixerRoute.Master, -3);

            MixerPersistence.Save(source, path);
            Assert.True(File.Exists(MixerPersistence.SidecarPath(path)));

            var target = NewProject("主唱", "和声");
            MixerPersistence.Load(target, path);

            Assert.Single(target.Auxes);
            Assert.Equal(aux.Id, target.Auxes[0].Id);
            Assert.Equal("AUX 1", target.Auxes[0].Name.Value);
            Assert.Equal(-6, target.Auxes[0].Gain.Value);
            Assert.Equal(0.25, target.Auxes[0].Pan.Value);

            Assert.Equal(aux.Id, target.Tracks[0].Output.Value.Id);
            Assert.Single(target.Tracks[0].Sends);
            Assert.Equal(-3, target.Tracks[0].Sends[0].Gain.Value);
            Assert.Equal(MixerOutputKind.Master, target.Tracks[0].Sends[0].Target.Value.Kind);

            // 没动过的轨保持默认。
            Assert.Equal(MixerOutputKind.Master, target.Tracks[1].Output.Value.Kind);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // 改名（轨数不变）→ 路由按序号跟上。
    [Fact]
    public void RenamedTrack_KeepsItsRouting()
    {
        string path = TempProjectPath();
        try
        {
            var source = NewProject("主唱");
            source.Tracks[0].Output.SetInfo(new MixerRoute(MixerOutputKind.Device, "device:default", "设备"));
            MixerPersistence.Save(source, path);

            var target = NewProject("Lead Vocal");
            MixerPersistence.Load(target, path);

            Assert.Equal(MixerOutputKind.Device, target.Tracks[0].Output.Value.Kind);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // 删掉一条轨（轨数变了）→ 对不上的路由丢掉，绝不能安到别人身上。
    [Fact]
    public void DeletedTrack_DropsItsRoutingInsteadOfMovingIt()
    {
        string path = TempProjectPath();
        try
        {
            var source = NewProject("主唱", "和声");
            source.Tracks[0].Output.SetInfo(new MixerRoute(MixerOutputKind.Device, "device:default", "设备"));
            MixerPersistence.Save(source, path);

            // 只剩"和声"（原来在序号 1，现在在 0）。
            var target = NewProject("和声");
            MixerPersistence.Load(target, path);

            // 和声自己的 dto 是 Master；主唱那条不能因为序号 0 撞上就套给和声。
            Assert.Equal(MixerOutputKind.Master, target.Tracks[0].Output.Value.Kind);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void LoadWithoutSidecar_IsANoOp()
    {
        string path = TempProjectPath();
        try
        {
            var target = NewProject("主唱");
            MixerPersistence.Load(target, path);

            Assert.Empty(target.Auxes);
            Assert.Equal(MixerOutputKind.Master, target.Tracks[0].Output.Value.Kind);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void CorruptSidecar_IsIgnored()
    {
        string path = TempProjectPath();
        try
        {
            File.WriteAllText(MixerPersistence.SidecarPath(path), "{ this is not json");

            var target = NewProject("主唱");
            MixerPersistence.Load(target, path);   // 不抛 = 工程还能打开

            Assert.Empty(target.Auxes);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // 文件内 epMixer：写进 .tlpx、读得回来，而且不影响工程本身。
    [Fact]
    public void NativeFile_RoundTripsTheEpMixerPayload()
    {
        var info = new ProjectInfo();
        info.Tracks.Add(new TrackInfo { Name = "t1" });

        var file = new NativeProjectFile { Project = info, EpMixer = "{\"branch\":1,\"auxes\":[]}" };

        using var stream = new MemoryStream();
        new TuneLabProjectCbor().SerializeNative(stream, file);
        var bytes = stream.ToArray();

        using var read = new MemoryStream(bytes);
        var back = new TuneLabProjectCbor().DeserializeNative(read);

        Assert.Equal("{\"branch\":1,\"auxes\":[]}", back.EpMixer);
        Assert.Equal("t1", back.Project.Tracks[0].Name);
    }

    // 装载顺序：文件内 → sidecar（DESIGN.md §9.2）。
    [Fact]
    public void Load_PrefersTheInFilePayloadOverTheSidecar()
    {
        string path = TempProjectPath();
        try
        {
            var sidecarSource = NewProject("主唱");
            sidecarSource.AddAux("AUX-from-sidecar");
            MixerPersistence.Save(sidecarSource, path);

            var inFileSource = NewProject("主唱");
            inFileSource.AddAux("AUX-from-file");
            string inFile = MixerPersistence.Serialize(inFileSource);

            var target = NewProject("主唱");
            MixerPersistence.Load(target, path, inFile);

            Assert.Single(target.Auxes);
            Assert.Equal("AUX-from-file", target.Auxes[0].Name.Value);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // pin 跟着工程走（它是视图状态，但用户希望下次打开还在）。
    [Fact]
    public void Pin_SurvivesSaveAndLoad()
    {
        string path = TempProjectPath();
        try
        {
            var source = NewProject("主唱");
            source.Tracks[0].Pinned.SetInfo(MixerPin.Left);
            MixerPersistence.Save(source, path);

            var target = NewProject("主唱");
            MixerPersistence.Load(target, path);

            Assert.Equal(MixerPin.Left, target.Tracks[0].Pinned.Value);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // 门面：工程被替换后，补丁手里的同一个模型要跟着换，并通知界面重建。
    [Fact]
    public void Facade_FollowsProjectReplacement()
    {
        var document = new ProjectDocument();
        document.SetProject(new Project(new ProjectInfo { Tracks = { new NativeTrackInfo { Name = "A" } } }));

        using var facade = new MixerModelFacade(document);
        Assert.Equal("A", facade.Channels[0].Name);

        int changed = 0;
        facade.ChannelsChanged += () => changed++;

        document.SetProject(new Project(new ProjectInfo { Tracks = { new NativeTrackInfo { Name = "B" } } }));

        Assert.True(changed > 0);
        Assert.Equal("B", facade.Channels[0].Name);
    }

    // [EP-MIXER-26] 删 VCA 时把成员摘下来（同一个 merge = 一步 undo）。
    [Fact]
    public void RemoveVca_DetachesItsMembers()
    {
        var document = new ProjectDocument();
        document.SetProject(new Project(new ProjectInfo { Tracks = { new NativeTrackInfo { Name = "A" } } }));

        using var facade = new MixerModelFacade(document);

        facade.AddVca();
        var vca = facade.Channels.First(channel => channel.Kind == MixerChannelKind.Vca);
        var track = facade.Channels.First(channel => channel.Kind == MixerChannelKind.Track);

        track.VcaId = vca.Id;
        Assert.Equal(vca.Id, track.VcaId);

        facade.RemoveVca(vca);

        Assert.Equal(string.Empty, track.VcaId);
        Assert.DoesNotContain(facade.Channels, channel => channel.Kind == MixerChannelKind.Vca);
    }

    // [EP-MIXER-26] VCA 与成员归属要跟着工程走。
    [Fact]
    public void SaveThenLoad_RestoresVcaAndMembership()
    {
        string path = TempProjectPath();
        try
        {
            var source = NewProject("鼓", "贝斯");
            var vca = source.AddVca("鼓组");
            vca.Gain.SetInfo(-4.5);
            vca.IsMute.SetInfo(true);

            source.Tracks[0].VcaId.SetInfo(vca.Id);
            var aux = source.AddAux("AUX 1");
            aux.VcaId.SetInfo(vca.Id);

            MixerPersistence.Save(source, path);

            var target = NewProject("鼓", "贝斯");
            MixerPersistence.Load(target, path);

            Assert.Single(target.Vcas);
            Assert.Equal(vca.Id, target.Vcas[0].Id);
            Assert.Equal("鼓组", target.Vcas[0].Name.Value);
            Assert.Equal(-4.5, target.Vcas[0].Gain.Value);
            Assert.True(target.Vcas[0].IsMute.Value);

            Assert.Equal(vca.Id, target.Tracks[0].VcaId.Value);
            Assert.Equal(string.Empty, target.Tracks[1].VcaId.Value);
            Assert.Equal(vca.Id, target.Auxes[0].VcaId.Value);
        }
        finally
        {
            Cleanup(path);
        }
    }
}
