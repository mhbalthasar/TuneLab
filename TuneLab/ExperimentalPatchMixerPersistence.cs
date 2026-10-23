using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TuneLab.Audio;
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

    // .tlpx 里的 epMixer 用紧凑 JSON（要塞进一个 CBOR 文本串）。
    public static string Serialize(Project project) => JsonSerializer.Serialize(BuildPayload(project), CompactJson);

    public static void Save(Project project, string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
            return;

        try
        {
            File.WriteAllText(SidecarPath(projectPath), JsonSerializer.Serialize(BuildPayload(project), JsonOptions));
        }
        catch (Exception ex)
        {
            HostLog.Error("mixer: failed to write routing sidecar: " + ex);
        }
    }

    static Payload BuildPayload(Project project)
    {
        var payload = new Payload { Branch = Branch };

        // 插件的实时状态先采一遍（state chunk 存在插件实例里，不在数据里 ——
        // 见 InsertInstanceFactory 的注释：实例不能跟着 undo 走）。
        // 采不到的槽位保留它上次落盘的那份（可能是老版本的状态），不静默清空。
        CaptureInsertStates(project);

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
                SoloSafe = aux.IsSoloSafe.Value,
                Output = ToDto(aux.Output.Value),
                Sends = ToDtos(aux.Sends),
                Pin = aux.Pinned.Value.ToString(),
                Inserts = ToInsertDtos(aux.Inserts),

                // [EP-MIXER-20] 输入级
                TrimDb = aux.TrimDb.Value,
                PhaseInvert = aux.PhaseInvert.Value,
                HpfHz = aux.HpfHz.Value,
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
                Pin = track.Pinned.Value.ToString(),
                Inserts = ToInsertDtos(track.Inserts),

                // [EP-MIXER-20] 输入级
                SoloSafe = track.IsSoloSafe.Value,
                TrimDb = track.TrimDb.Value,
                PhaseInvert = track.PhaseInvert.Value,
                HpfHz = track.HpfHz.Value,
            });
        }

        // [EP-MIXER-19] Master 总线上的插入（总轨效果）。
        payload.MasterInserts = ToInsertDtos(project.MasterInserts);

        return payload;
    }

    // 装载顺序：**文件内 epMixer → sidecar → 空**（DESIGN.md §9.2）。
    // inFileJson 是 .tlpx 顶层键 epMixer 的内容；原版读取器会跳过它，所以我们自己取出来用。
    public static void Load(Project project, string projectPath, string? inFileJson = null)
    {
        if (!string.IsNullOrEmpty(inFileJson))
        {
            Apply(project, inFileJson);
            return;
        }

        if (string.IsNullOrEmpty(projectPath))
            return;

        string path = SidecarPath(projectPath);
        if (!File.Exists(path))
            return;

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            HostLog.Error("mixer: failed to read routing sidecar: " + ex);
            return;
        }

        Apply(project, json);
    }

    // 把一份 JSON 载荷套到工程上。整段一个 merge 作用域：装载出来的路由算"一步"，不会散成几十条 undo。
    public static void Apply(Project project, string json)
    {
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            HostLog.Error("mixer: routing payload is not valid JSON: " + ex);
            return;
        }

        if (payload == null || payload.Branch > Branch)
        {
            HostLog.Warning("mixer: routing payload has a newer branch; ignored");
            return;
        }

        project.BeginMergeNotify();
        try
        {
            ApplyPayload(project, payload);
        }
        catch (Exception ex)
        {
            HostLog.Error("mixer: failed to apply routing payload: " + ex);
        }
        finally
        {
            project.EndMergeNotify();
            project.Commit();
        }

        // 【必须重建图】装载发生在 `mDocument.SetProject` **之后**：那一刻 AudioGraph 已经
        // 用"没有插入"的工程编过一次图，之后 ApplyPayload 才把插入槽位塞进来。
        // 而插入不是"加轨/改路由"那类会自己触发重建的事件（成员变化挂在混音台模型上，
        // 而模型是懒创建的，此时可能还没订阅）。不补这一下，槽位要等下一次结构变化
        // 才进链 —— 用户看到的就是"插件的「界面」键全是灰的，加一个新插件之后才全变正常"。
        AudioEngine.RebuildMixerGraph();
    }

    static void ApplyPayload(Project project, Payload payload)
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
            aux.IsSoloSafe.SetInfo(dto.SoloSafe);
            aux.Output.SetInfo(FromDto(dto.Output));
            aux.Pinned.SetInfo(ParsePin(dto.Pin));

            // [EP-MIXER-20] 输入级（老工程没有这几个字段 → 默认值 = 关闭）
            aux.TrimDb.SetInfo(dto.TrimDb);
            aux.PhaseInvert.SetInfo(dto.PhaseInvert);
            aux.HpfHz.SetInfo(dto.HpfHz);
            ApplyInserts(aux.Inserts, dto.Inserts);
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
            track.Pinned.SetInfo(ParsePin(dto.Pin));

            // [EP-MIXER-20] 输入级（老工程没有这几个字段 → 默认值 = 关闭）
            track.IsSoloSafe.SetInfo(dto.SoloSafe);
            track.TrimDb.SetInfo(dto.TrimDb);
            track.PhaseInvert.SetInfo(dto.PhaseInvert);
            track.HpfHz.SetInfo(dto.HpfHz);
            ApplyInserts((DataObjectList<InsertSlot>)track.Inserts, dto.Inserts);
            ApplySends((DataObjectList<Send>)track.Sends, dto.Sends);
            matched++;
        }

        if (dropped > 0)
            HostLog.Warning($"mixer: {dropped} track(s) in the routing sidecar had no match; their routing was dropped");

        // Master 的插入链：与轨道 / AUX 同一套装载。
        ApplyInserts(project.MasterInserts, payload.MasterInserts);

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

            // [EP-MIXER-21] 老工程的 send 只有 Target + Gain，新字段取默认值 ——
            // post-fader / 声向居中 / 不静音，与旧行为逐项一致。
            send.IsPreFader.SetInfo(dto.IsPreFader);
            send.Pan.SetInfo(dto.Pan);
            send.IsMute.SetInfo(dto.IsMute);
            list.Add(send);
        }
    }

    // 插入槽位的装载。与 send 的区别：plugins 是**按 id 原样重建**的（不像轨道路由
    // 需要重新对上）—— id 是我们自己生成的 GUID，不依赖宿主的任何顺序或命名。
    static void ApplyInserts(DataObjectList<InsertSlot> list, List<InsertDto>? dtos)
    {
        if (dtos == null)
            return;

        foreach (var dto in dtos)
        {
            if (string.IsNullOrEmpty(dto.Id))
                continue;

            var slot = new InsertSlot(
                dto.Id,
                dto.Format ?? string.Empty,
                dto.PluginId ?? string.Empty,
                dto.Name ?? dto.PluginId ?? string.Empty,
                list);

            slot.IsBypassed.SetInfo(dto.Bypass);

            // [EP-MIXER-23] 湿/干与增益微调。老工程没有这两个字段 →
            // DTO 的缺省值（全湿 + 0 dB）= 包装器透明，与旧行为一致。
            slot.Mix.SetInfo(dto.Mix);
            slot.GainDb.SetInfo(dto.GainDb);

            if (!string.IsNullOrEmpty(dto.State))
            {
                try
                {
                    slot.State = Convert.FromBase64String(dto.State);
                }
                catch (FormatException)
                {
                    // 坏 base64 = 手改坏了。丢掉这一段、保留空槽位：
                    // 宁可让插件用自己的默认参数，也不让整个工程打不开。
                    HostLog.Warning("mixer: an insert's saved state is not valid base64; ignored");
                }
            }

            list.Add(slot);
        }
    }

    static RouteDto ToDto(MixerRoute route) => new()
    {
        Kind = route.Kind.ToString(),
        Id = route.Id ?? string.Empty,
        Name = route.Name ?? string.Empty,
    };

    static List<SendDto> ToDtos(IReadOnlyDataObjectList<Send> sends)    {
        var list = new List<SendDto>(sends.Count);
        foreach (var send in sends)
        {
            list.Add(new SendDto
            {
                Kind = send.Target.Value.Kind.ToString(),
                Id = send.Target.Value.Id ?? string.Empty,
                Name = send.Target.Value.Name ?? string.Empty,
                Gain = send.Gain.Value,

                // [EP-MIXER-21] send 的推子前/后、声向、静音
                IsPreFader = send.IsPreFader.Value,
                Pan = send.Pan.Value,
                IsMute = send.IsMute.Value,
            });
        }

        return list;
    }

    // 保存前把每个插件的 state chunk 采回数据槽位。采不到（插件没起 / 没报状态）
    // 就保留槽位上原有的一份 —— 清空会让"关掉编辑器再存盘"丢掉用户的插件设置。
    static void CaptureInsertStates(Project project)
    {
        var factory = InsertInstanceFactory.Shared;

        foreach (var track in project.Tracks)
        {
            foreach (var slot in track.Inserts)
                Capture(factory, slot);
        }

        foreach (var aux in project.Auxes)
        {
            foreach (var slot in aux.Inserts)
                Capture(factory, slot);
        }
    }

    static void Capture(InsertInstanceFactory factory, InsertSlot slot)
    {
        var state = factory.CaptureState(slot);

        if (state != null && state.Length > 0)
            slot.State = state;
    }

    // 插入槽位 → DTO。state 是 base64（DESIGN.md §9.4 / §5.6：
    static List<InsertDto> ToInsertDtos(IReadOnlyDataObjectList<InsertSlot> slots)
    {
        var list = new List<InsertDto>(slots.Count);

        foreach (var slot in slots)
        {
            list.Add(new InsertDto
            {
                Id = slot.Id,
                Format = slot.Format,
                PluginId = slot.PluginId.Value,
                Name = slot.Name.Value,
                Bypass = slot.IsBypassed.Value,
                State = slot.State == null || slot.State.Length == 0
                    ? null
                    : Convert.ToBase64String(slot.State),

                // [EP-MIXER-23] 湿/干与增益微调
                Mix = slot.Mix.Value,
                GainDb = slot.GainDb.Value,
            });
        }

        return list;
    }

    static MixerPin ParsePin(string? value)
        => Enum.TryParse<MixerPin>(value, out var pin) ? pin : MixerPin.None;

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

    // 塞进 .tlpx 的那份不缩进：工程文件里多几千字节空白没有意义。
    static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    sealed class Payload
    {
        public int Branch { get; set; }
        public List<AuxDto> Auxes { get; set; } = [];
        public List<TrackDto> Tracks { get; set; } = [];

        // [EP-MIXER-19] Master 的插入链。老文件没有这个字段 → 空列表，行为与以前一致。
        public List<InsertDto> MasterInserts { get; set; } = [];
    }

    sealed class AuxDto
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public double Gain { get; set; }
        public double Pan { get; set; }
        public bool Mute { get; set; }
        public bool Solo { get; set; }

        // [EP-MIXER-25] Solo 安全（老工程没有这个字段 → false = 普通通道）
        public bool SoloSafe { get; set; }

        public RouteDto? Output { get; set; }
        public List<SendDto>? Sends { get; set; }
        public string? Pin { get; set; }
        public List<InsertDto>? Inserts { get; set; }

        // [EP-MIXER-20] 输入级
        public double TrimDb { get; set; }
        public bool PhaseInvert { get; set; }
        public double HpfHz { get; set; }
    }

    sealed class TrackDto
    {
        public int Index { get; set; }
        public string? Name { get; set; }

        // [EP-MIXER-25] Solo 安全（Track 的 solo 本身由宿主原生格式管，这里只存新字段）
        public bool SoloSafe { get; set; }

        public RouteDto? Output { get; set; }
        public List<SendDto>? Sends { get; set; }
        public string? Pin { get; set; }
        public List<InsertDto>? Inserts { get; set; }

        // [EP-MIXER-20] 输入级
        public double TrimDb { get; set; }
        public bool PhaseInvert { get; set; }
        public double HpfHz { get; set; }
    }

    // 一个插入槽位。state 是 base64（DESIGN.md §9.4 / §5.6：
    // VST 的 chunk 是二进制，而载荷是 JSON 文本）。
    sealed class InsertDto
    {
        public string Id { get; set; } = string.Empty;
        public string? Format { get; set; }
        public string? PluginId { get; set; }
        public string? Name { get; set; }
        public bool Bypass { get; set; }
        public string? State { get; set; }

        // [EP-MIXER-23] 湿/干与增益微调。缺省值 = 全湿 + 0 dB（= 包装器透明），
        // 所以老工程读进来与旧行为一致。
        public double Mix { get; set; } = 1;
        public double GainDb { get; set; }
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

        // [EP-MIXER-21] send 的推子前/后、声向、静音
        public bool IsPreFader { get; set; }
        public double Pan { get; set; }
        public bool IsMute { get; set; }
    }
}











