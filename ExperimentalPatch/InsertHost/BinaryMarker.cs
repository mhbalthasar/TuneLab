using System;
using System.IO;
using System.Text;

namespace TuneLab.ExperimentalPatch.InsertHost;

// 在插件二进制里找一段 ASCII / UTF-16LE 字面量（分块读，不加载、不执行插件代码）。
//
// 【谁在用】两处判断都只能靠"二进制里的常量"，因为真要问插件就得加载它：
//   · 扫描判乐器：VST3 的类别是类信息里的子类别字符串（"Instrument|Synth"）；
//   · 界面判 OpenGL：DPF / NanoVG 那类插件的二进制里带 "NanoVG"。
// 这两件事都发生在**读文件**的层面上，放在宿主进程里做是安全的（不执行插件代码）。
public static class BinaryMarker
{
    public static bool Contains(string file, string needle)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(needle);
        byte[] utf16 = Encoding.Unicode.GetBytes(needle);

        int overlap = Math.Max(ascii.Length, utf16.Length) - 1;
        byte[] buffer = new byte[(1 << 20) + overlap];

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);

        int carry = 0;

        while (true)
        {
            int read = stream.Read(buffer, carry, buffer.Length - carry);
            if (read <= 0)
                return false;

            var chunk = buffer.AsSpan(0, carry + read);

            if (chunk.IndexOf(ascii) >= 0 || chunk.IndexOf(utf16) >= 0)
                return true;

            // 留下尾巴：标记可能正好跨在两块之间。
            carry = Math.Min(overlap, chunk.Length);
            chunk.Slice(chunk.Length - carry).CopyTo(buffer);
        }
    }
}
