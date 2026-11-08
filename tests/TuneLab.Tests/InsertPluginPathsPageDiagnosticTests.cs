using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.Patches.Mixer;
using TuneLab.ExperimentalPatch.Patches.Mixer.Controls;
using Xunit;

namespace TuneLab.Tests;

// 回归测试：插件扫描设置页的每个格式区块（VST3/VST2/CLAP/JSFX）都必须有
// 「+ 添加路径」按钮。曾有反馈 JSFX 区块缺按钮——按钮是 BuildSection 统一生成的，
// 该测试保证任何后续重构都不会把某一个格式的入口悄悄丢掉。
public class InsertPluginPathsPageDiagnosticTests
{
    class StubHost : IHostServices
    {
        public string AppVersion => "test";
        public string DataFolder { get; } =
            Path.Combine(Path.GetTempPath(), "tunelab-paths-page-diag-" + Guid.NewGuid().ToString("N"));
        public string UiLanguage => "zh-CN";
        public void Log(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message, Exception? exception = null) { }
    }

    [AvaloniaFact]
    public void EverySection_HasAddPathButton()
    {
        var page = new InsertPluginPathsPage(new StubHost());

        // 视觉树要在挂到根之后才物化（ContentPresenter 是挂载时才展开的）。
        var window = new Window { Width = 900, Height = 700, Content = page };
        window.Show();

        var buttons = new List<string>();
        Collect(page, buttons, 0);

        var adds = buttons.FindAll(t => t.Contains("添加路径") || t.Contains("Add path"));
        Assert.Equal(4, adds.Count);

        window.Close();
    }

    static void Collect(Visual visual, List<string> into, int depth)
    {
        if (visual is ChromeButton button)
            into.Add(button.Text ?? "");

        foreach (var child in visual.GetVisualChildren())
            Collect(child, into, depth + 1);
    }
}
