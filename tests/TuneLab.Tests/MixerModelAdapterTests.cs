using System.Linq;
using TuneLab.Audio;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.SDK;
using Xunit;

namespace TuneLab.Tests;

// 混音台模型适配器的封条（M1）。
//
// 这里测的是"宿主数据 ⇄ 补丁契约"的翻译，不测 UI、不测引擎：
//   · 读：Track 的 Name/Color/Gain/Pan/M/S/选中 必须原样出现在通道上；
//   · 写：改通道必须落到 Track 的 DataProperty（于是 undo / 序列化 / 脚本都免费）；
//   · undo 粒度：一次拖动 = 一步 undo（宿主用 merge 作用域），单次写入 = 一步。
public class MixerModelAdapterTests
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

    static NativeTrackInfo Track(string name, double gain = 0, double pan = 0, bool mute = false, bool solo = false, string color = "")
        => new() { Name = name, Gain = gain, Pan = pan, Mute = mute, Solo = solo, Color = color };

    [Fact]
    public void Channels_MapTrackStateInOrder()
    {
        var (_, project, model) = Create(
            Track("主唱", gain: -3, pan: 0.25, mute: true, color: "#737CE5"),
            Track("和声", gain: -6.5, pan: -0.5, solo: true));

        using (model)
        {
            Assert.Equal(2, model.Channels.Count);

            var first = model.Channels[0];
            Assert.Equal(MixerChannelKind.Track, first.Kind);
            Assert.Equal("主唱", first.Name);
            Assert.Equal("#737CE5", first.ColorHex);
            Assert.Equal(-3, first.GainDb);
            Assert.Equal(0.25, first.Pan);
            Assert.True(first.IsMute);
            Assert.False(first.IsSolo);

            var second = model.Channels[1];
            Assert.Equal("和声", second.Name);
            Assert.True(second.IsSolo);
            Assert.False(second.IsMute);

            // 默认输出 = Master（DESIGN.md §4.2）。
            Assert.Equal(MixerOutputKind.Master, first.Output.Kind);
        }
    }

    [Fact]
    public void WriteBack_LandsOnTrackProperties()
    {
        var (_, project, model) = Create(Track("t1"));

        using (model)
        {
            var channel = model.Channels[0];
            var track = project.Tracks[0];

            channel.GainDb = -7.5;
            channel.Pan = 0.4;
            channel.IsMute = true;
            channel.IsSolo = true;

            Assert.Equal(-7.5, track.Gain.Value);
            Assert.Equal(0.4, track.Pan.Value);
            Assert.True(track.IsMute.Value);
            Assert.True(track.IsSolo.Value);
        }
    }

    [Fact]
    public void StateChanged_FiresWhenTrackIsEditedElsewhere()
    {
        var (_, project, model) = Create(Track("t1"));

        using (model)
        {
            int fired = 0;
            model.Channels[0].StateChanged += () => fired++;

            // 模拟编辑器里改同一属性（轨道头滑杆走的就是这条路径）。
            project.Tracks[0].Gain.Set(-12);

            Assert.True(fired > 0);
        }
    }

    [Fact]
    public void SelectedTrack_IsReported()
    {
        var (_, project, model) = Create(Track("t1"), Track("t2"));

        using (model)
        {
            project.Tracks[1].IsSelected = true;

            Assert.False(model.Channels[0].IsSelected);
            Assert.True(model.Channels[1].IsSelected);
        }
    }

    [Fact]
    public void SingleWrite_IsOneUndoStep()
    {
        var (document, project, model) = Create(Track("t1"));

        using (model)
        {
            var channel = model.Channels[0];

            channel.GainDb = -1;
            channel.GainDb = -2;

            document.Undo();
            Assert.Equal(-1, project.Tracks[0].Gain.Value);

            document.Undo();
            Assert.Equal(0, project.Tracks[0].Gain.Value);
        }
    }

    [Fact]
    public void EditSession_IsOneUndoStep()
    {
        var (document, project, model) = Create(Track("t1"));

        using (model)
        {
            var channel = model.Channels[0];

            // 模拟一次拖动：几十次中间值，最后停在 −9。
            channel.BeginEdit();
            for (int i = 1; i <= 30; i++)
                channel.GainDb = -i * 0.3;

            channel.EndEdit();

            Assert.Equal(-9, project.Tracks[0].Gain.Value, 6);

            // 一步 undo 就该回到拖动之前 —— 中间 30 次写入不该各占一步。
            document.Undo();
            Assert.Equal(0, project.Tracks[0].Gain.Value);
        }
    }

    [Fact]
    public void Outputs_ExposeTheFourCategories()
    {
        var (_, _, model) = Create(Track("t1"));

        using (model)
        {
            var kinds = model.Outputs.Select(o => o.Kind).ToList();

            Assert.Contains(MixerOutputKind.Master, kinds);
            Assert.Contains(MixerOutputKind.Device, kinds);
            Assert.Contains(MixerOutputKind.Cable, kinds);

            // Cable 是保留项：菜单里出现，但不可选（DESIGN.md §4.5）。
            Assert.False(model.Outputs.Single(o => o.Kind == MixerOutputKind.Cable).IsAvailable);
        }
    }

    [Fact]
    public void InsertFormats_ReportRealAvailability()
    {
        var (_, _, model) = Create(Track("t1"));

        using (model)
        {
            Assert.NotEmpty(model.InsertFormats);

            // VST3 的可用性 = 原生库在不在（测试机上 native dll 不在 exe 旁，
            // 所以这里是"不可用 + 有原因"）。真正要钉的是：
            // 每个格式都给出**为什么**不可用，而不是光秃秃一个 false ——
            // 界面把那句话直接显示给用户（DESIGN.md §6.3 的纪律）。
            foreach (var format in model.InsertFormats)
            {
                if (!format.IsAvailable)
                    Assert.False(string.IsNullOrWhiteSpace(format.UnavailableReason));
            }

            // 没实现的格式永远是禁用的：界面不画做不到的入口。
            Assert.False(model.InsertFormats.Single(f => f.Name == "JSFX").IsAvailable);
            Assert.False(model.InsertFormats.Single(f => f.Name == "VST2").IsAvailable);
        }
    }

    [Fact]
    public void Name_IsWritableAndUndoable()
    {
        var (document, project, model) = Create(Track("t1"));

        using (model)
        {
            model.Channels[0].Name = "改名了";
            Assert.Equal("改名了", project.Tracks[0].Name.Value);

            document.Undo();
            Assert.Equal("t1", project.Tracks[0].Name.Value);
        }
    }

    [Fact]
    public void Master_ReadsAndWritesEngineGain()
    {
        var (_, _, model) = Create(Track("t1"));
        double original = AudioEngine.MasterGain;

        try
        {
            using (model)
            {
                model.Master.GainDb = -4.5;
                Assert.Equal(-4.5, AudioEngine.MasterGain);

                // Master 没有 pan / M / S：写进去也不该有副作用。
                model.Master.Pan = 0.5;
                model.Master.IsMute = true;
                model.Master.IsSolo = true;

                Assert.Equal(0, model.Master.Pan);
                Assert.False(model.Master.IsMute);
                Assert.False(model.Master.IsSolo);
            }
        }
        finally
        {
            AudioEngine.MasterGain = original;
        }
    }
}
