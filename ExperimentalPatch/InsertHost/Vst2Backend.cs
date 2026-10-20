using System;
using System.Collections.Generic;
using System.IO;
using TuneLab.ExperimentalPatch.MixerEngine;

namespace TuneLab.ExperimentalPatch.InsertHost;

// VST2 后端：**目前只做清单，还不能加载**。
//
// 【为什么先这样】VST2 的加载要分两条路：
//   · 64 位插件：进程内直载（P/Invoke VST2 的 C ABI，见 VstBridge/PluginHost.cs ——
//     那段已经写好并编译通过，但还没有一个宿主经它建过实例）；
//   · 32 位插件：过 x86 桥（VstBridge 的协议、JobObject、共享环都已落地，同样没人调过）。
// 两条都没端到端跑通之前，让它能"扫到"已经是有用的：用户至少看得见自己装了什么，
// 而不会因为"明明装了却搜不到"以为我们没实现。
//
// 【可用性】一律不可用 + 原因（界面据 IsAvailable 禁用菜单项）。这是 DES
public sealed class Vst2Backend : IInsertBackend
{
    public const string FormatName = "VST2";

    string IInsertBackend.FormatName => FormatName;

    // 常驻 false：真加载还没接（见类注释）。
    public bool IsAvailable => false;

    public string? UnavailableReason => "VST2 hosting is not wired up yet";

    // 按目录清单扫 .dll。**不判断是不是真 VST2**：那要读导出表（VSTPluginMain），
    // 而读导出表要么把 dll load 进来（可能崩）、要么自己 PE 解析（那一整块还没写）。
    // 宁可多列几个，也不能漏 —— 漏了用户会以为我们的扫描不可信。
    public IReadOnlyList<InsertPluginInfo> ListPlugins(IReadOnlyList<string>? directories = null)
    {
        if (directories == null || directories.Count == 0)
            return [];

        var list = new List<InsertPluginInfo>();

        foreach (string directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            foreach (string file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(file);

                list.Add(new InsertPluginInfo
                {
                    Format = FormatName,
                    PluginId = "vst2:" + name,
                    Name = name,
                    Locator = file,
                });
            }
        }

        return list;
    }

    public IMixInsert? Create(InsertPluginInfo plugin, InsertSettings settings, out string? reason)
    {
        reason = "VST2 hosting is not wired up yet";
        return null;
    }
}
