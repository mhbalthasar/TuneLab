using System;
using System.Collections.Generic;
using System.IO;
using TuneLab.ExperimentalPatch.MixerEngine;

namespace TuneLab.ExperimentalPatch.InsertHost;

// VST2 后端：列插件清单 + 通过桥进程建实例。
//
// 【加载一律过桥】见 Vst2Insert 的类注释：进程内直载 64 位 VST2 实测会被
// 单个插件的 effSetChunk 崩掉整个宿主，所以不分位数，全部交给桥。
// 桥按插件位数选 x86 / x64 两份可执行文件（PluginBitness 读 PE 头决定）。
//
// 【扫描不加载插件】清单按 .dll 列，名字用文件名，PluginId = "vst2:" + 文件名。
// 真名/厂商要加载插件才问得到，而扫一遍目录就为了显示名字去 load 几十个 dll
// 是自找崩溃（而且很慢）。代价是重名插件会撞 id —— 这个取舍写在 DESIGN.md 里。
public sealed class Vst2Backend : IInsertBackend
{
    public const string FormatName = "VST2";

    string IInsertBackend.FormatName => FormatName;

    // 桥是 Windows 专属的（共享内存段 + JobObject）。别的平台上这个后端整个不可用。
    public bool IsAvailable => OperatingSystem.IsWindows();

    public string? UnavailableReason
        => OperatingSystem.IsWindows() ? null : "the VST2 bridge is Windows-only";

    public IReadOnlyList<InsertPluginInfo> ListPlugins(IReadOnlyList<string>? directories = null, Action<int, int, string>? onDirectory = null)
    {
        if (directories == null || directories.Count == 0)
            return [];

        var list = new List<InsertPluginInfo>();

        // 同一个 dll 可能被两个目录同时包含（用户既有公共目录又有私有目录）：
        // 按完整路径去重，别在菜单里出现两行一模一样的。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < directories.Count; i++)
        {
            string directory = directories[i];

            // 进度先报后扫（见 IInsertBackend.ListPlugins 的说明）。
            onDirectory?.Invoke(i, directories.Count, directory);

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            foreach (string file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
            {
                if (!seen.Add(file))
                    continue;

                int bitness = PluginBitness.Read(file);

                // 不是合法 PE（或既不是 32 也不是 64 位）→ 不列。列了也装不进来。
                if (bitness == 0)
                    continue;

                string name = Path.GetFileNameWithoutExtension(file);

                list.Add(new InsertPluginInfo
                {
                    Format = FormatName,
                    PluginId = "vst2:" + name,
                    Name = name,
                    Category = bitness + "-bit",
                    Locator = file,
                });
            }
        }

        return list;
    }

    public IMixInsert? Create(InsertPluginInfo plugin, InsertSettings settings, out string? reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            reason = UnavailableReason;
            return null;
        }

        return Vst2Insert.Create(plugin, settings, out reason);
    }
}
