using System;
using System.IO;
using System.Linq;
using TuneLab.Audio;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.Extensions.Formats.TLP;
using TuneLab.Foundation;
using TuneLab.SDK;
using Xunit;

namespace TuneLab.Tests;

// 宿主侧插入链的封条（阶段二 M6e）。
//
// 这里全部走组件测试（真 Project / 真 DataProperty / 真 IMixerModel 适配器），
// 但**不碰真插件**——插件实例要有 native 库与 .vst3 文件（开发机上没有 64 位的）。
// 所以这一组钉的是宿主侧的保证：
//   ① 数据层：槽位的增删 / 旁通 / undo 粒度；
//   ② 契约层：IMixerChannel.Inserts 是活读（undo 后立刻反映）；
//   ③ 落盘层：槽位与 state 进出 epMixer 载荷，且**原版打不开也不会被它挡住**。
public class InsertModelTests
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
        => Path.Combine(Path.GetTempPath(), "tl-insert-" + Guid.NewGuid().ToString("N") + ".tlpx");

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

    static InsertSlot NewSlot(DataObjectList<InsertSlot> list, string pluginId = "vst3:Test")
    {
        var slot = new InsertSlot("insert-" + Guid.NewGuid().ToString("N"), "VST3", pluginId, "Test", list);
        list.Add(slot);
        return slot;
    }

    // ── 数据层 ──

    [Fact]
    public void Track_StartsWithNoInserts()
    {
        var project = NewProject("t1");

        Assert.Empty(project.Tracks[0].Inserts);
    }

    [Fact]
    public void AddingAnInsert_IsOneUndoStep()
    {
        var project = NewProject("t1");
        var track = project.Tracks[0];

        NewSlot((DataObjectList<InsertSlot>)track.Inserts);
        project.Commit();

        Assert.Single(track.Inserts);

        project.Undo();

        Assert.Empty(track.Inserts);
    }

    [Fact]
    public void RemovingAnInsert_IsOneUndoStep()
    {
        var project = NewProject("t1");
        var track = project.Tracks[0];
        var slot = NewSlot((DataObjectList<InsertSlot>)track.Inserts);
        project.Commit();

        ((DataObjectList<InsertSlot>)track.Inserts).Remove(slot);
        project.Commit();

        Assert.Empty(track.Inserts);

        project.Undo();

        Assert.Single(track.Inserts);
    }

    [Fact]
    public void Bypass_IsADistinctPropertyFromRemoval()
    {
        var project = NewProject("t1");
        var list = (DataObjectList<InsertSlot>)project.Tracks[0].Inserts;
        var slot = NewSlot(list);
        project.Commit();

        // 旁通：槽位还在、顺序不变；删掉才是真的没了。
        slot.IsBypassed.SetInfo(true);
        project.Commit();

        Assert.Single(list);
        Assert.True(list[0].IsBypassed.Value);

        project.Undo();

        Assert.Single(list);
        Assert.False(list[0].IsBypassed.Value);
    }

    [Fact]
    public void InsertOrder_FollowsTheList()
    {
        var project = NewProject("t1");
        var list = (DataObjectList<InsertSlot>)project.Tracks[0].Inserts;

        var a = NewSlot(list, "vst3:A");
        var b = NewSlot(list, "vst3:B");

        // 顺序 = 处理顺序（自下而上，DESIGN.md §3.2）。
        Assert.Equal("vst3:A", list[0].PluginId.Value);
        Assert.Equal("vst3:B", list[1].PluginId.Value);
    }

    // [VST3-BRIDGE] 建实例失败要留下可显示的原因，成功后要清掉 ——
    // 界面据此把那一行标红（Reason 就是 tooltip 里那句话，见 FxPopupWindow）。
    [Fact]
    public void Factory_RecordsAndClearsInsertFailure()
    {
        var project = NewProject("t1");
        var list = (DataObjectList<InsertSlot>)project.Tracks[0].Inserts;

        // JSFX 没有后端：必失败，与开发机上有没有 VST3 native / 桥无关。
        var slot = new InsertSlot("insert-fail-" + Guid.NewGuid().ToString("N"), "JSFX", "jsfx:1", "Test", list);
        list.Add(slot);

        var factory = new InsertInstanceFactory();

        Assert.Null(factory.FailureOf(slot.Id));

        var insert = factory.Create(slot, out var reason);

        Assert.Null(insert);
        Assert.False(string.IsNullOrEmpty(reason));
        Assert.Equal(reason, factory.FailureOf(slot.Id));

        // 槽位被释放，失败标记跟着走。
        factory.Release(slot.Id);
        Assert.Null(factory.FailureOf(slot.Id));
    }

    // ── 契约层 ──


    [Fact]
    public void Inserts_AreReadLive_NotCached()
    {
        var project = NewProject("t1");
        var document = new ProjectDocument();
        document.SetProject(project);

        var model = new MixerModelAdapter(project);
        using (model)
        {
            var channel = model.Channels[0];

            Assert.Empty(channel.Inserts);

            NewSlot((DataObjectList<InsertSlot>)project.Tracks[0].Inserts);

            // 活读：不缓存一份。缓存会让 undo 之后浮窗显示过期内容。
            Assert.Single(channel.Inserts);
        }
    }

    [Fact]
    public void AddInsert_ThroughTheModel_IsOneUndoStep()
    {
        var project = NewProject("t1");

        var model = new MixerModelAdapter(project);
        using (model)
        {
            var channel = model.Channels[0];
            var format = model.InsertFormats.First();

            channel.AddInsert(format);

            Assert.Single(channel.Inserts);
            Assert.Single(project.Tracks[0].Inserts);

            project.Undo();

            Assert.Empty(project.Tracks[0].Inserts);
            Assert.Empty(channel.Inserts);
        }
    }

    // ── 落盘 ──

    [Fact]
    public void Inserts_SurviveSaveAndLoad()
    {
        string path = TempProjectPath();
        try
        {
            var source = NewProject("主唱");
            var list = (DataObjectList<InsertSlot>)source.Tracks[0].Inserts;

            var slot = NewSlot(list, "vst3:Pro-Q 4");
            slot.Name.Set("Pro-Q 4");
            slot.IsBypassed.SetInfo(true);
            slot.State = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

            MixerPersistence.Save(source, path);

            var target = NewProject("主唱");
            MixerPersistence.Load(target, path);

            var restored = (DataObjectList<InsertSlot>)target.Tracks[0].Inserts;

            Assert.Single(restored);
            Assert.Equal("vst3:Pro-Q 4", restored[0].PluginId.Value);
            Assert.Equal("Pro-Q 4", restored[0].Name.Value);
            Assert.True(restored[0].IsBypassed.Value);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, restored[0].State);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // [EP-MIXER-27] 侧链引用与轨道的稳定序号一起往返。
    [Fact]
    public void SidechainSource_SurvivesSaveAndLoad()
    {
        string path = TempProjectPath();
        try
        {
            var source = NewProject("底鼓", "贝斯");
            var list = (DataObjectList<InsertSlot>)source.Tracks[1].Inserts;

            var slot = NewSlot(list, "vst3:Comp");
            string kickId = MixerTrackIds.IdOf(source.Tracks[0]);
            slot.SidechainId.SetInfo(kickId);

            MixerPersistence.Save(source, path);

            var target = NewProject("底鼓", "贝斯");
            MixerPersistence.Load(target, path);

            var restored = (DataObjectList<InsertSlot>)target.Tracks[1].Inserts;

            Assert.Single(restored);
            Assert.Equal(kickId, restored[0].SidechainId.Value);

            // 稳定序号本身也落了盘：装载后同一条轨拿回同一个号，
            // 引用才不会指到"序号撞上"的另一条轨。
            Assert.Equal(kickId, MixerTrackIds.IdOf(target.Tracks[0]));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void InsertState_IsBase64InThePayload_SoJsonStaysText()
    {
        string path = TempProjectPath();
        try
        {
            var source = NewProject("主唱");
            var list = (DataObjectList<InsertSlot>)source.Tracks[0].Inserts;

            var slot = NewSlot(list, "vst3:Test");
            slot.State = new byte[] { 0xFF, 0xFE, 0x00, 0x01 };

            string json = MixerPersistence.Serialize(source);

            // 载荷是 JSON 文本（要塞进 CBOR 串）：二进制必须编码成 base64，
            // 不能出现**裸** NUL 字节把下游的文本管道噎住。
            // （JSON 里出现 "\u0000" 这六个字符是允许的转义，不算裸字符。）
            Assert.DoesNotContain('\0', json);

            // 0xFF 0xFE 0x00 0x01 → "//4AAQ=="（手算好了，不靠运行时）。
            Assert.Contains("//4AAQ==", json);

            var target = NewProject("主唱");
            MixerPersistence.Apply(target, json);

            Assert.Equal(new byte[] { 0xFF, 0xFE, 0x00, 0x01 },
                ((DataObjectList<InsertSlot>)target.Tracks[0].Inserts)[0].State);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ACorruptInsertState_IsDroppedWithoutFailingTheLoad()
    {
        string json = """
        {
          "branch": 1,
          "tracks": [
            {
              "index": 0,
              "name": "主唱",
              "inserts": [
                { "id": "insert-1", "format": "VST3", "pluginId": "vst3:X", "name": "X", "bypass": false, "state": "!!!not base64!!!" }
              ]
            }
          ]
        }
        """;

        var project = NewProject("主唱");

        // 坏 state 只丢那一段：槽位还在（链长不变）、插件用它自己的默认参数。
        // 绝不让它把整个工程的装载挡住 —— 那是工程打不开级别的事故。
        MixerPersistence.Apply(project, json);

        var list = (DataObjectList<InsertSlot>)project.Tracks[0].Inserts;

        Assert.Single(list);
        Assert.Null(list[0].State);
    }

    [Fact]
    public void Aux_CanAlsoCarryInserts()
    {
        var project = NewProject("t1");

        var aux = project.AddAuxWithId("aux-1", "AUX 1");
        NewSlot((DataObjectList<InsertSlot>)aux.Inserts);

        Assert.Single(aux.Inserts);
    }
}
