using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.Foundation;
using HostLog = TuneLab.Foundation.Log;

namespace TuneLab;

// [EP-MIXER-11] 路由（AUX / output / send）的落盘。
//
// 【为什么是 sidecar 而不是写进 .tlpx】见 DESIGN.md §9：
// 原版保存时会按 DTO 重写整个文件，我们写进 .tlpx 的任何键都会被丢掉；sidecar 原版不认识、
// 也不会动它，所以"原版打开 → 编辑 → 保存 → 回到补丁编辑器"之后路由还在。
// 代价：**只拷 .tlpx 不会带走路由**（§9.2 的 in-file 那一半还没做，见 §9.0）。
//
// 【形状】`<工程文件>.epmixer.json`。用 JSON 而不是 CBOR：它是我们自己的文件，
// 可读、可手改、坏了能看出来；而且不需要与宿主的格式版本有任何关系。
//
// 【纪律】读写都在 UI 线程；失败只记日志，绝不让"路由没读出来"变成打不开工程。
internal static class MixerPersistence
{
    const int Branch = 1;

    public static string SidecarPath(string projectPath) => projectPath + ".epmixer.json";

    public static void Save(Project project, string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
            return;

        try
        {
            var payload = new Payload { Branch = Branch };

            foreach (var aux in project.Auxes)
            {
                payload.Auxes.Add(new AuxDto
                {
                    Id = aux.Id,
                    Name = aux.Name.Value,
                    Gain = aux.Gain.Value,
                    Pan = aux.Pan.Value,
                    Mute = aux.IsMute.Value,
                    Solo = aux.IsSolo.Value,
                    Output = ToDto(aux.Output.Value),
                    Sends = ToDtos(aux.Sends),
                });
            }

            for (int i = 0; i < project.Tracks.Count; i++)
            {
                var track = project.Tracks[i];
                payload.Tracks.Add(new TrackDto
                {
                    Index = i,
                    Name = track.Name.Value,
                    Output = ToDto(track.Output.Value),
                    Sends = ToDtos(track.Sends),
                });
            }

            File.WriteAllText(SidecarPath(projectPath),
                JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception ex)
        {
            HostLog.Error("mixer: failed to write routing sidecar: " + ex);
        }
    }

    public static void Load(Project project, string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
            return;

        string path = SidecarPath(projectPath);
        if (!File.Exists(path))
            return;

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            HostLog.Error("mixer: failed to read routing sidecar: " + ex);
            return;
        }

        if (payload == null || payload.Branch > Branch)
        {
            HostLog.Warning("mixer: routing sidecar has a newer branch; ignored");
            return;
        }

        // 整段套一个 merge 作用域：装载出来的路由算"一步"，不会散成几十条 undo。
        project.BeginMergeNotify();
        try
        {
            Apply(project, payload);
        }
        catch (Exception ex)
        {
            HostLog.Error("mixer: failed to apply routing sidecar: " + ex);
        }
        finally
        {
            project.EndMergeNotify();
            project.Commit();
        }
    }

    static void Apply(Project project, Payload payload)
    {
        // AUX：按保存的 id 重建（路由引用的是 id，重建时不能换号）。
        var auxes = new Dictionary<string, AuxBus>();

        foreach (var dto in payload.Auxes)
        {
            if (string.IsNullOrEmpty(dto.Id))
                continue;

            var aux = project.AddAuxWithId(dto.Id, dto.Name ?? dto.Id);
            aux.Gain.SetInfo(dto.Gain);
            aux.Pan.SetInfo(dto.Pan);
            aux.IsMute.SetInfo(dto.Mute);
            aux.IsSolo.SetInfo(dto.Solo);
            aux.Output.SetInfo(FromDto(dto.Output));
            ApplySends(aux.Sends, dto.Sends);

            auxes[dto.Id] = aux;
        }

        // 轨道：按 名字 + 序号 对齐（原版往返不保留任何 id，DESIGN.md §9.3）。
        var byIndex = new Dictionary<int, ITrack>();
        for (int i = 0; i < project.Tracks.Count; i++)
            byIndex[i] = project.Tracks[i];

        int matched = 0;
        int dropped = 0;

        foreach (var dto in payload.Tracks)
        {
            var track = Match(project, byIndex, dto, payload.Tracks.Count);
            if (track == null)
            {
                dropped++;
                continue;
            }

            track.Output.SetInfo(FromDto(dto.Output));
            ApplySends((DataObjectList<Send>)track.Sends, dto.Sends);
            matched++;
        }

        if (dropped > 0)
            HostLog.Warning($"mixer: {dropped} track(s) in the routing sidecar had no match; their routing was dropped");

        if (matched > 0 || auxes.Count > 0)
            HostLog.Info($"mixer: restored routing ({auxes.Count} aux, {matched} track(s))");
    }

    static ITrack? Match(Project project, Dictionary<int, ITrack> byIndex, TrackDto dto, int savedTrackCount)
    {
        // ① 序号与名字都对上 —— 最常见（原版只改内容、不改结构）。
        if (byIndex.TryGetValue(dto.Index, out var exact) && exact.Name.Value == dto.Name)
            return exact;

        // ② 名字唯一对上 —— 用户插了/删了别的轨，序号漂移了。
        var byName = project.Tracks.FirstOrDefault(track => track.Name.Value == dto.Name);
        if (byName != null)
            return byName;

        // ③ 轨数没变才允许"按序号硬对"（用户只是改名或换序）。
        //    轨数变了还按序号对，会把删掉那条的路由安到别人身上——宁可丢。
        if (project.Tracks.Count == savedTrackCount && byIndex.TryGetValue(dto.Index, out var sameIndex))
            return sameIndex;

        return null;
    }

    static void ApplySends(DataObjectList<Send> list, List<SendDto>? dtos)
    {
        if (dtos == null)
            return;

        foreach (var dto in dtos)
        {
            var send = new Send(list);
            send.Target.SetInfo(FromDto(dto));
            send.Gain.SetInfo(dto.Gain);
            list.Add(send);
        }
    }

    static RouteDto ToDto(MixerRoute route) => new()
    {
        Kind = route.Kind.ToString(),
        Id = route.Id ?? string.Empty,
        Name = route.Name ?? string.Empty,
    };

    static List<SendDto> ToDtos(IReadOnlyDataObjectList<Send> sends)
    {
        var list = new List<SendDto>(sends.Count);
        foreach (var send in sends)
        {
            list.Add(new SendDto
            {
                Kind = send.Target.Value.Kind.ToString(),
                Id = send.Target.Value.Id ?? string.Empty,
                Name = send.Target.Value.Name ?? string.Empty,
                Gain = send.Gain.Value,
            });
        }

        return list;
    }

    static MixerRoute FromDto(RouteDto? dto)
    {
        if (dto == null)
            return MixerRoute.Master;

        if (!Enum.TryParse<MixerOutputKind>(dto.Kind, out var kind))
            return MixerRoute.Master;

        // 空 id = 老文件 / 手改坏了：回落到 Master，不把非法路由塞进模型。
        if (string.IsNullOrEmpty(dto.Id))
            return MixerRoute.Master;

        return new MixerRoute(kind, dto.Id, dto.Name ?? dto.Id);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    sealed class Payload
    {
        public int Branch { get; set; }
        public List<AuxDto> Auxes { get; set; } = [];
        public List<TrackDto> Tracks { get; set; } = [];
    }

    sealed class AuxDto
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public double Gain { get; set; }
        public double Pan { get; set; }
        public bool Mute { get; set; }
        public bool Solo { get; set; }
        public RouteDto? Output { get; set; }
        public List<SendDto>? Sends { get; set; }
    }

    sealed class TrackDto
    {
        public int Index { get; set; }
        public string? Name { get; set; }
        public RouteDto? Output { get; set; }
        public List<SendDto>? Sends { get; set; }
    }

    class RouteDto
    {
        public string? Kind { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    sealed class SendDto : RouteDto
    {
        public double Gain { get; set; }
    }
}
