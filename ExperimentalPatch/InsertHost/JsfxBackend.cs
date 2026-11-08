using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TuneLab.ExperimentalPatch.MixerEngine;

namespace TuneLab.ExperimentalPatch.InsertHost;

// JSFX 后端：宿主只做**文本扫描**（desc / 路径），不执行 EEL2。
//
// 与 VST3 / CLAP 的分工相反：JSFX 没有二进制元数据要问，扫描就是读文件头。
// 真正加载 / 编译 / 跑音频都在桥进程里交给 ysfx（JsfxPluginHost）。
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
        bool hasGfx = false;

        using (var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            for (int lineNumber = 0; lineNumber < 512 && reader.ReadLine() is { } line; lineNumber++)
            {
                string trimmed = line.Trim();

                if (trimmed.StartsWith("@", StringComparison.Ordinal))
                {
                    if (trimmed.StartsWith("@gfx", StringComparison.OrdinalIgnoreCase))
                        hasGfx = true;

                    continue;
                }

                if (trimmed.StartsWith("desc:", StringComparison.OrdinalIgnoreCase))
                    desc = trimmed[5..].Trim();
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
            Vendor = "JSFX",
            Category = hasGfx ? "JSFX (gfx)" : "JSFX",
            Locator = Path.GetFullPath(file),
            IsInstrument = false,
            IsAvailable = true,
        };
    }
}
