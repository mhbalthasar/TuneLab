using System;
using System.IO;
using System.Text;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 只读 PE 的**导出表**，回答"这个 dll 到底是不是 VST2"。
//
// 【为什么需要】VST2 的扫描只能按 *.dll 枚举，而"扩展名叫 .dll 的文件"不等于 VST2：
// 真机上就有把 LV2 的二进制（只导出 lv2_descriptor / lv2ui_descriptor）放进 VST2 目录的，
// 菜单里会带着"可用"的招牌列出来，用户插进去才看到 "the file is not a VST2 plugin"。
// 这里在扫描时就把这件事判掉：不动插件的行为，只让它提前显示为不可用 + 原因。
//
// 只读文件、不加载、不执行插件代码 —— 与 PluginBitness 同一档成本。
internal static class PeExports
{
    /// <summary>
    /// 这些名字里有没有一个被导出。<c>null</c> = 读不出来（不是合法 PE / 文件坏了 / 结构畸形），
    /// 调用方应当"不知道就别下线" —— 宁可多列一个坏插件，也不要把能用的插件藏起来。
    /// </summary>
    public static bool? HasAny(string path, params string[] names)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40)
                return null;

            stream.Seek(0x3C, SeekOrigin.Begin);
            int peOffset = reader.ReadInt32();

            if (peOffset <= 0 || peOffset + 24L > stream.Length)
                return null;

            stream.Seek(peOffset, SeekOrigin.Begin);
            if (reader.ReadUInt32() != 0x00004550) // 'P','E',0,0
                return null;

            reader.ReadUInt16();                                  // Machine
            ushort sectionCount = reader.ReadUInt16();
            reader.ReadUInt32();                                  // TimeDateStamp
            reader.ReadUInt32();                                  // PointerToSymbolTable
            reader.ReadUInt32();                                  // NumberOfSymbols
            ushort optionalSize = reader.ReadUInt16();
            reader.ReadUInt16();                                  // Characteristics

            long optionalStart = peOffset + 24L;
            if (optionalStart + optionalSize > stream.Length || sectionCount > 96)
                return null;

            stream.Seek(optionalStart, SeekOrigin.Begin);
            ushort magic = reader.ReadUInt16();

            // 导出表在数据目录的第 0 项：PE32+ 从可选头 +112 开始，PE32 从 +96。
            int directoryOffset = magic switch
            {
                0x20B => 112,
                0x10B => 96,
                _ => -1,
            };

            if (directoryOffset < 0 || directoryOffset + 8 > optionalSize)
                return null;

            stream.Seek(optionalStart + directoryOffset, SeekOrigin.Begin);
            uint exportRva = reader.ReadUInt32();
            reader.ReadUInt32();                                   // export size

            // 没有导出表 = 不可能是 VST2（VST2 唯一的入口就是导出函数）。
            if (exportRva == 0)
                return false;

            long sectionStart = optionalStart + optionalSize;
            if (sectionStart + sectionCount * 40L > stream.Length)
                return null;

            var sections = new Section[sectionCount];
            stream.Seek(sectionStart, SeekOrigin.Begin);

            for (int i = 0; i < sectionCount; i++)
            {
                stream.Seek(8, SeekOrigin.Current);                // Name
                uint virtualSize = reader.ReadUInt32();
                uint virtualAddress = reader.ReadUInt32();
                uint rawSize = reader.ReadUInt32();
                uint rawPointer = reader.ReadUInt32();
                stream.Seek(16, SeekOrigin.Current);               // relocs / linenums / characteristics

                sections[i] = new Section(virtualAddress, Math.Max(virtualSize, rawSize), rawPointer);
            }

            long directory = ToOffset(sections, exportRva, stream.Length);
            if (directory < 0)
                return null;

            // IMAGE_EXPORT_DIRECTORY：+24 NumberOfNames，+32 AddressOfNames。
            stream.Seek(directory + 24, SeekOrigin.Begin);
            uint nameCount = reader.ReadUInt32();
            stream.Seek(directory + 32, SeekOrigin.Begin);
            uint namesRva = reader.ReadUInt32();

            if (nameCount == 0)
                return false;

            if (nameCount > 65536)
                return null;

            long namesTable = ToOffset(sections, namesRva, stream.Length);
            if (namesTable < 0)
                return null;

            for (uint i = 0; i < nameCount; i++)
            {
                stream.Seek(namesTable + i * 4, SeekOrigin.Begin);
                uint nameRva = reader.ReadUInt32();

                long nameOffset = ToOffset(sections, nameRva, stream.Length);
                if (nameOffset < 0)
                    continue;

                string? exported = ReadAsciiZ(stream, nameOffset);
                if (exported == null)
                    continue;

                foreach (string wanted in names)
                {
                    if (string.Equals(exported, wanted, StringComparison.Ordinal))
                        return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            return null;
        }
    }

    readonly record struct Section(uint VirtualAddress, uint Size, uint RawPointer);

    // RVA → 文件偏移。落不进任何节（或落在节的填充区外）就当读不出来。
    static long ToOffset(Section[] sections, uint rva, long fileLength)
    {
        foreach (var section in sections)
        {
            if (rva < section.VirtualAddress || rva >= section.VirtualAddress + section.Size)
                continue;

            long offset = section.RawPointer + (rva - section.VirtualAddress);
            return offset < fileLength ? offset : -1;
        }

        return -1;
    }

    static string? ReadAsciiZ(Stream stream, long offset)
    {
        if (offset < 0 || offset >= stream.Length)
            return null;

        stream.Seek(offset, SeekOrigin.Begin);

        var text = new StringBuilder(32);
        while (text.Length <= 512)
        {
            int value = stream.ReadByte();
            if (value <= 0)
                return text.ToString();

            text.Append((char)value);
        }

        return null;
    }
}
