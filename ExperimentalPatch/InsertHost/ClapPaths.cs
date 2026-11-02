using System;
using System.Collections.Generic;
using System.IO;

namespace TuneLab.ExperimentalPatch.InsertHost;

// CLAP 插件的标准安装位置（三平台）。
//
// 【为什么单独一个类】两份使用者：设置层的默认清单（InsertPluginSettings.DefaultClapPaths）
// 与后端的"完整扫描"兜底（ClapBackend.ListPluginsDeep）。写两遍必然漂移。
//
// 【只列真的在用的标准位置】CLAP 没有像 VST3 那样强制的系统目录，社区惯例是：
//   Windows  %CommonProgramFiles%\CLAP
//   macOS    /Library/Audio/Plug-Ins/CLAP、~/Library/Audio/Plug-Ins/CLAP
//   Linux    /usr/lib/clap、~/.clap      （发行版另有 /usr/local/lib/clap）
// 用户自己的收藏目录通过设置页加，不在这里猜。
public static class ClapPaths
{
    public static IReadOnlyList<string> Default { get; } = Build();

    static IReadOnlyList<string> Build()
    {
        var list = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);

            if (!string.IsNullOrEmpty(common))
                list.Add(Path.Combine(common, "CLAP"));

            return list;
        }

        if (OperatingSystem.IsMacOS())
        {
            list.Add("/Library/Audio/Plug-Ins/CLAP");

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (!string.IsNullOrEmpty(home))
                list.Add(Path.Combine(home, "Library", "Audio", "Plug-Ins", "CLAP"));

            return list;
        }

        list.Add("/usr/lib/clap");
        list.Add("/usr/local/lib/clap");

        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(userHome))
            list.Add(Path.Combine(userHome, ".clap"));

        return list;
    }
}
