using System;
using System.IO;

namespace TuneLab.Data;

// [EP-MIXER-18] 音频部件在工程文件里的路径格式（单独一个 Patcher）。
//
// 【格式】绝对路径若确实位于工程目录之内，就存成 ".." + 分隔符 + 相对部分（如
// `..\audio\vox.wav`）；否则原样存绝对路径。读回来时再与工程目录拼回去。
//
// 【原来的坑】编码是 `".." + path[base.Length..]`，**默认 base 末尾带分隔符**。
// 但工程放在盘符根目录时（`GetDirectoryName("Y:\x.tlpx")` 返回 `"Y:\"`），
// 余下部分没有前导分隔符，于是存成 `..Insts\Midi\…`（`..` 直接贴 `Insts`）。
// 读回来时按 `path[3..]` 切，把 `I` 也切掉了，定位必然失败 —— 用户看到的就是
// "保存后地址头部出现 `..`，重新打开找不到音频"。
//
// 这里同时修两件事：
//   1. 编码时**显式**补分隔符，并做目录边界判断（`C:\Music` 不该匹配 `C:\Music2\…`）；
//   2. 解码时容忍老文件那种 `..Insts\…`（没有分隔符），把它们也还原对 ——
//      已经存坏的文件不用重存就能打开。
internal static class ExperimentalPatchAudioPartPath
{
    const string Prefix = "..";

    static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>保存：绝对路径 → 工程文件里存的形式。</summary>
    public static string ToStored(string path, string? baseDirectory)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(baseDirectory))
            return path;

        // 盘符根（"Y:\"）去掉尾分隔符会变成 "Y:"，所以这里保留它再拼一个分隔符比较。
        string baseDir = baseDirectory.TrimEnd(Separators);
        string prefix = baseDir.Length == 0 ? baseDirectory : baseDir + Path.DirectorySeparatorChar;

        if (prefix.Length == 0)
            return path;

        // 必须真的在目录里：只比字符串前缀会把 `C:\Music2\x.wav` 误判成在 `C:\Music` 里。
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return path;

        return Prefix + Path.DirectorySeparatorChar + path[prefix.Length..];
    }

    /// <summary>读取：工程文件里存的形式 → 绝对路径（未做存在性检查）。</summary>
    public static string FromStored(string stored, string? baseDirectory)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;

        if (string.IsNullOrEmpty(baseDirectory))
            return stored;   // 调用方会把它当"还没绑定工程目录"，等绑定后重试

        // 老文件是 `..Insts\…`（少一个分隔符），新文件是 `..\Insts\…`。
        // 统一做法：去掉前导的 ".." 再削掉可能存在的分隔符。
        string rest = stored[Prefix.Length..].TrimStart(Separators);

        return Path.Combine(baseDirectory, rest);
    }
}
