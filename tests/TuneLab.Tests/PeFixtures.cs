using System;

namespace TuneLab.Tests;

// 造最小 PE32+ 样本：一节数据、可选头里带/不带导出表。给"读 PE 头"的那几组测试用。
//
// 为什么不拿真 dll 当样本：测试机（尤其 CI）上没有稳定的 VST2 dll，而"名字里带 VSTPluginMain
// 的导出表"正是被测的东西 —— 自己拼一份最可控，也不用加载任何插件代码。
internal static class PeFixtures
{
    /// <summary>0x8664 = AMD64，0x014C = I386。</summary>
    /// <param name="exportName">null = 有导出表但一个名字都不导；给名字则导出它。</param>
    public static byte[] Build(int machine = 0x8664, string? exportName = null)
    {
        var bytes = new byte[0x400];

        // DOS 头：e_lfanew → 0x40
        BitConverter.GetBytes(0x40).CopyTo(bytes, 0x3C);

        const int pe = 0x40;
        bytes[pe] = (byte)'P';
        bytes[pe + 1] = (byte)'E';

        BitConverter.GetBytes((ushort)machine).CopyTo(bytes, pe + 4);   // Machine
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, pe + 6);         // NumberOfSections
        BitConverter.GetBytes((ushort)0xF0).CopyTo(bytes, pe + 20);     // SizeOfOptionalHeader

        const int opt = pe + 24;                                        // 0x58
        BitConverter.GetBytes((ushort)0x20B).CopyTo(bytes, opt);        // PE32+

        if (exportName != null)
        {
            // 数据目录第 0 项 = 导出表，PE32+ 从可选头 +112 开始。
            BitConverter.GetBytes(0x1000).CopyTo(bytes, opt + 112);     // VirtualAddress
            BitConverter.GetBytes(0x28).CopyTo(bytes, opt + 116);       // Size
        }

        // 节表：Name ".rdata"，VirtualSize/VirtualAddress/RawSize/RawPointer
        const int section = opt + 0xF0;
        ".rdata\0\0"u8.CopyTo(bytes.AsSpan(section));
        BitConverter.GetBytes(0x200).CopyTo(bytes, section + 8);        // VirtualSize
        BitConverter.GetBytes(0x1000).CopyTo(bytes, section + 12);      // VirtualAddress
        BitConverter.GetBytes(0x200).CopyTo(bytes, section + 16);       // SizeOfRawData
        BitConverter.GetBytes(0x200).CopyTo(bytes, section + 20);       // PointerToRawData

        if (exportName == null)
            return bytes;

        // 节数据从文件 0x200 开始，对应 RVA 0x1000。
        const int data = 0x200;

        // IMAGE_EXPORT_DIRECTORY
        BitConverter.GetBytes(0x1080).CopyTo(bytes, data + 12);         // Name RVA
        BitConverter.GetBytes(1).CopyTo(bytes, data + 16);              // Base
        BitConverter.GetBytes(1).CopyTo(bytes, data + 20);              // NumberOfFunctions
        BitConverter.GetBytes(1).CopyTo(bytes, data + 24);              // NumberOfNames
        BitConverter.GetBytes(0x1090).CopyTo(bytes, data + 28);         // AddressOfFunctions
        BitConverter.GetBytes(0x10A0).CopyTo(bytes, data + 32);         // AddressOfNames
        BitConverter.GetBytes(0x10B0).CopyTo(bytes, data + 36);         // AddressOfNameOrdinals

        "pefixture.dll\0"u8.CopyTo(bytes.AsSpan(0x280));

        BitConverter.GetBytes(0x1100).CopyTo(bytes, 0x290);             // 函数地址（随便指节内）
        BitConverter.GetBytes(0x10C0).CopyTo(bytes, 0x2A0);             // 名字 RVA
        BitConverter.GetBytes(0).CopyTo(bytes, 0x2B0);                  // 序号

        byte[] name = System.Text.Encoding.ASCII.GetBytes(exportName);
        name.CopyTo(bytes, 0x2C0);
        bytes[0x2C0 + name.Length] = 0;

        return bytes;
    }

    public static string Write(string directory, string fileName, byte[] bytes)
    {
        string path = System.IO.Path.Combine(directory, fileName);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }
}
