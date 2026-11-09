using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TuneLab.ExperimentalPatch.MixerEngine;

namespace TuneLab.ExperimentalPatch.InsertHost;

// JSFX 后端：宿主只做**文本扫描**（desc / author / 路径），不执行 EEL2。
//
// 与 VST3 / CLAP 的分工相反：JSFX 没有二进制元数据要问，扫描就是读文件头。
// 真正加载 / 编译 / 跑音频都在桥进程里交给 ysfx（JsfxPluginHost）。
//
// 【厂商/作者】ysfx 自己也会读（ysfx_get_author），但那是"加载后"的口径；
// 扫描阶段沿用它就等于每个文件开一次桥，所以这里按同一套约定读文本，
// 两条路径都认：`author:` 行，以及不规范但很常见的 `//author:` 注释
// （与 ysfx 的 ysfx_parse.cpp 两遍解析一致：带标签的走一遍，注释的走一遍）。
public sealed class JsfxBackend : IInsertBackend
{
    public const string FormatName = "JSFX";

    string IInsertBackend.FormatName => FormatName;

    public bool IsAvailable => JsfxBridgeInsert.IsolationAvailable;

    public string? UnavailableReason => JsfxBridgeInsert.IsolationUnavailableReason;

    public IReadOnlyList<InsertPluginInfo> ListPlugins(IReadOnlyList<string>? directories = null,
        Action<int, int, string>? onDirectory = null, bool probeInstruments = true, Action<string>? onDetail = null)
    {
        var roots = directories ?? [];
        var list = new List<InsertPluginInfo>();

        for (int index = 0; index < roots.Count; index++)
        {
            string root = roots[index];
            onDirectory?.Invoke(index, roots.Count, root);

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                });
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string file in files)
            {
                if (!string.Equals(Path.GetExtension(file), ".jsfx", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var plugin = ReadPlugin(file, root);
                    if (plugin == null)
                        continue;

                    list.Add(plugin);
                    onDetail?.Invoke(plugin.Name);
                }
                catch (Exception)
                {
                    // 读不了的文件不挡住同目录的其它 JSFX。
                }
            }
        }

        return list;
    }

    public IMixInsert? Create(InsertPluginInfo plugin, InsertSettings settings, out string? reason)
        => JsfxBridgeInsert.Create(plugin, settings, out reason, out _);

    static InsertPluginInfo? ReadPlugin(string file, string root)
    {
        string? desc = null;
        string? author = null;
        bool hasGfx = false;

        // 头部元数据只在第一个 `@section` 之前有效（与 ysfx 的头部段落解析口径一致）；
        // 过了这一段还继续扫，是为了找出 @gfx（有没有编辑器窗口）。
        bool inHeader = true;

        using (var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            for (int lineNumber = 0; lineNumber < 512 && reader.ReadLine() is { } line; lineNumber++)
            {
                string trimmed = line.Trim();

                if (trimmed.StartsWith("@", StringComparison.Ordinal))
                {
                    inHeader = false;

                    if (trimmed.StartsWith("@gfx", StringComparison.OrdinalIgnoreCase))
                        hasGfx = true;

                    continue;
                }

                if (!inHeader)
                    continue;

                if (desc == null && trimmed.StartsWith("desc:", StringComparison.OrdinalIgnoreCase))
                    desc = trimmed[5..].Trim();
                else if (author == null && trimmed.StartsWith("author:", StringComparison.OrdinalIgnoreCase))
                    author = trimmed[7..].Trim();
                else if (author == null && trimmed.StartsWith("//author:", StringComparison.OrdinalIgnoreCase))
                    author = trimmed[9..].Trim();
            }
        }

        string relative;

        try
        {
            relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        }
        catch (Exception)
        {
            relative = Path.GetFileName(file);
        }

        if (relative.StartsWith("../", StringComparison.Ordinal))
            relative = Path.GetFileName(file);

        string name = string.IsNullOrWhiteSpace(desc) ? Path.GetFileNameWithoutExtension(file) : desc!;

        return new InsertPluginInfo
        {
            Format = FormatName,
            PluginId = "jsfx:" + relative,
            Name = name,
            // 没有 author 就留空 —— 添加菜单会显示"(未知厂商)"。以前这里填死 "JSFX"，
            // 把"格式"当成了"作者"，所以所有 JSFX 都挤在同一栏里。
            Vendor = author ?? string.Empty,
            Category = hasGfx ? "JSFX (gfx)" : "JSFX",
            Locator = Path.GetFullPath(file),
            IsInstrument = false,
            IsAvailable = true,
        };
    }
}
