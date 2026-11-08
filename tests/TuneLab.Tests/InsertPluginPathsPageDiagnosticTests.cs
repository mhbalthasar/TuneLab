using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.Patches.Mixer;
using TuneLab.GUI.Components;
using Xunit;

namespace TuneLab.Tests;

// 回归测试：插件扫描设置页的每个格式区块（VST3/VST2/CLAP/JSFX）都必须有
// 「+ 添加路径」按钮（页面上以 Tag="add-path" 标记）。曾有反馈 JSFX 区块缺按钮——
// 按钮是 BuildSection 统一生成的，该测试保证后续重构不会把某个格式的入口悄悄丢掉。
// 同时守卫页面宽度：内容不得比设置窗（800 逻辑像素宽，内容区约 580）更宽，否则右缘被裁。
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
        var window = new Window { Width = 800, Height = 720, Content = page };
        window.Show();

        var addButtons = page.GetVisualDescendants()
            .OfType<TuneLab.GUI.Components.Button>()
            .Count(b => (b.Tag as string) == "add-path");
        Assert.Equal(4, addButtons);

        // 宽度诊断：页面必须被窗口约束住（历史上内容曾比窗口宽，右缘被裁掉）。
        Assert.True(page.Bounds.Width <= 800, $"page width {page.Bounds.Width} exceeds window width 800");
        var scroll = page.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll != null)
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1,
                $"scroll content {scroll.Extent.Width} wider than viewport {scroll.Viewport.Width}");

        window.Close();
    }
}
