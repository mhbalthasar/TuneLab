using System;
using System.Formats.Cbor;
using System.IO;
using System.Linq;
using System.Text;
using TuneLab.Extensions.Formats.TLP;
using TuneLab.SDK;
using Xunit;

namespace TuneLab.Tests;

// 工程格式的【兼容性封条】。补丁层往 .tlpx 里加东西时，这三条必须一直成立：
//
//   ① 原版 TuneLab 能打开补丁保存的工程 —— 靠"version 保持 1 + 未知键被跳过"。
//   ② 原版保存时会**丢掉**它不认识的键 —— 这是事实（DTO 重写），也是"为什么还需要 sidecar"。
//   ③ 我们不能靠 version 号来区分自己 —— 一升版本，原版就直接拒绝打开。
//
// ② 这条测试是**故意钉住上游的丢键行为**：哪天它变成"保留未知键"，这里会红，那时就该删掉
// sidecar 那一整套（更简单的方案出现了）。
public class ProjectFormatCompatibilityTests
{
    static ProjectInfo SampleProject()
    {
        var info = new ProjectInfo();
        info.Tracks.Add(new TrackInfo { Name = "主唱", Gain = -3, Pan = 0.2 });
        info.Tracks.Add(new TrackInfo { Name = "和声", Gain = -6, Pan = -0.3, Mute = true });
        return info;
    }

    static byte[] WriteProject(ProjectInfo info)
    {
        using var stream = new MemoryStream();
        new TuneLabProjectCbor().Serialize(stream, info);
        return stream.ToArray();
    }

    static ProjectInfo ReadProject(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return new TuneLabProjectCbor().Deserialize(stream);
    }

    // ① 原版读取器遇到不认识的顶层键会 SkipValue —— 这正是"加自己的键"能成立的前提。
    [Fact]
    public void UnknownTopLevelKey_IsSkippedByTheReader()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(null);

        writer.WriteTextString("version");
        writer.WriteInt32(1);      // 必须保持 1

        writer.WriteTextString("someFutureKey");   // 一个我们不认识的顶层键（原版/本版都该跳过）
        writer.WriteStartMap(null);
        writer.WriteTextString("branch");
        writer.WriteInt32(1);
        writer.WriteEndMap();

        writer.WriteEndMap();

        // 不抛 = 原版能打开。
        var project = ReadProject(writer.Encode());
        Assert.Empty(project.Tracks);
    }

    // ③ 升 version 就等于把原版挡在门外（它只认 1）—— 所以补丁层的 schema 版本走 branch 号。
    [Fact]
    public void HigherVersion_IsRejected()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(null);
        writer.WriteTextString("version");
        writer.WriteInt32(2);
        writer.WriteEndMap();

        var error = Assert.Throws<Exception>(() => ReadProject(writer.Encode()));
        Assert.Contains("Unsupported Version", error.Message);
    }

    // 我们写出去的工程，version 字段必须还是 1。
    [Fact]
    public void OurWriter_StillWritesVersionOne()
    {
        var bytes = WriteProject(SampleProject());
        var reader = new CborReader(bytes);

        reader.ReadStartMap();
        int? version = null;
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var key = reader.ReadTextString();
            if (key == "version")
                version = reader.ReadInt32();
            else
                reader.SkipValue();
        }

        Assert.Equal(1, version);
    }

    // ② 原版"读进来再写出去"会丢掉未知键 —— sidecar 存在的唯一理由。
    //    这条红掉时：说明上游开始保留未知键了，sidecar 可以退休（见 DESIGN.md §9）。
    [Fact]
    public void UpstreamRoundTrip_DropsUnknownKeys()
    {
        // 先写一份带 epMixer 的工程（模拟补丁层保存的结果）。
        var withExtra = AddTopLevelKey(WriteProject(SampleProject()), "epMixer", writer =>
        {
            writer.WriteStartMap(null);
            writer.WriteTextString("branch");
            writer.WriteInt32(1);
            writer.WriteEndMap();
        });

        Assert.Contains("epMixer", Encoding.UTF8.GetString(withExtra), StringComparison.Ordinal);

        // 原版：读进来（未知键被跳过）→ 再写出去（从 DTO 重建）→ 我们的键没了。
        var rewritten = WriteProject(ReadProject(withExtra));

        Assert.DoesNotContain("epMixer", Encoding.UTF8.GetString(rewritten), StringComparison.Ordinal);

        // 轨道内容本身是保住的 —— 丢的只有我们附加的那一份。
        Assert.Equal(new[] { "主唱", "和声" }, ReadProject(rewritten).Tracks.Select(t => t.Name).ToArray());
    }

    // 把已编码的顶层 map 拆开，追加一个键后再拼回去（只用于构造测试输入）。
    static byte[] AddTopLevelKey(byte[] encoded, string key, Action<CborWriter> writeValue)
    {
        var reader = new CborReader(encoded);
        var writer = new CborWriter();

        reader.ReadStartMap();
        writer.WriteStartMap(null);

        while (reader.PeekState() != CborReaderState.EndMap)
        {
            writer.WriteTextString(reader.ReadTextString());
            CopyValue(reader, writer);
        }

        writer.WriteTextString(key);
        writeValue(writer);

        writer.WriteEndMap();
        return writer.Encode();
    }

    static void CopyValue(CborReader reader, CborWriter writer)
    {
        switch (reader.PeekState())
        {
            case CborReaderState.StartMap:
                reader.ReadStartMap();
                writer.WriteStartMap(null);
                while (reader.PeekState() != CborReaderState.EndMap)
                {
                    writer.WriteTextString(reader.ReadTextString());
                    CopyValue(reader, writer);
                }
                reader.ReadEndMap();
                writer.WriteEndMap();
                break;

            case CborReaderState.StartArray:
                reader.ReadStartArray();
                writer.WriteStartArray(null);
                while (reader.PeekState() != CborReaderState.EndArray)
                    CopyValue(reader, writer);
                reader.ReadEndArray();
                writer.WriteEndArray();
                break;

            case CborReaderState.TextString:
                writer.WriteTextString(reader.ReadTextString());
                break;

            case CborReaderState.UnsignedInteger:
            case CborReaderState.NegativeInteger:
                writer.WriteInt32(reader.ReadInt32());
                break;

            case CborReaderState.DoublePrecisionFloat:
            case CborReaderState.SinglePrecisionFloat:
            case CborReaderState.HalfPrecisionFloat:
                writer.WriteDouble(reader.ReadDouble());
                break;

            case CborReaderState.Boolean:
                writer.WriteBoolean(reader.ReadBoolean());
                break;

            case CborReaderState.Null:
                reader.ReadNull();
                writer.WriteNull();
                break;

            case CborReaderState.ByteString:
                writer.WriteByteString(reader.ReadByteString());
                break;

            default:
                throw new NotSupportedException("unhandled cbor state: " + reader.PeekState());
        }
    }
}
