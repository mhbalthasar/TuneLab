using System;
using System.IO;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 读 PE 头判断一个 dll 是 32 位还是 64 位。
//
// 【为什么必须自己读】VST2 的位数决定了它能进哪个桥进程：
// 32 位插件只能被 x86 桥加载，64 位只能被 x64 桥加载。搞错位数的结果不是
// "加载失败"这么温和 —— NativeLibrary.Load 会抛 ERROR_BAD_EXE_FORMAT，
// 而我们希望在建实例**之前**就知道该起哪个桥，而不是先失败再看异常。
//
// 只读 4 个字段（e_lfanew / PE 签名 / Machine），不加载 dll、不执行任何插件代码。
internal static class PluginBitness
{
    const uint PeSignature = 0x00004550; // 'P','E',0,0

    /// <summary>32 / 64；读不出来（不是 PE、文件坏了）返回 0。</summary>
    public static int Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40)
                return 0;

            stream.Seek(0x3C, SeekOrigin.Begin);
            int peOffset = reader.ReadInt32();

            if (peOffset <= 0 || peOffset + 6L > stream.Length)
                return 0;

            stream.Seek(peOffset, SeekOrigin.Begin);
            if (reader.ReadUInt32() != PeSignature)
                return 0;

            return reader.ReadUInt16() switch
            {
                0x014C => 32,   // IMAGE_FILE_MACHINE_I386
                0x8664 => 64,   // IMAGE_FILE_MACHINE_AMD64
                0xAA64 => 64,   // IMAGE_FILE_MACHINE_ARM64（不是 x64，但同为 64 位进程无法直载）
                _ => 0,
            };
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
