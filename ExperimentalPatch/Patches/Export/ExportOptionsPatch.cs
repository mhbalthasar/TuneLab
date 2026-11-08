using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.UI;
using CheckBox = TuneLab.GUI.Components.CheckBox;

namespace TuneLab.ExperimentalPatch.Patches.Export;

// 导出侧栏的“包含轨道效果器”开关。
//
// 【为什么在补丁层】导出侧栏是宿主 UI，但这个开关是补丁功能（分轨导出走轨道插入链）。
// 宿主只留一个贡献锚点（ExportOptionsContribution），侧栏标题/控件都由这里给；
// 没有补丁时宿主侧栏与上游逐项一致。
[Patch(Order = 0)]
public sealed class ExportOptionsPatch : IPatch
{
    public string Id => "export.track-effects";

    public void Attach(IAnchorTable anchors)
    {
        mHost = anchors.Host;
        anchors.AddExportOptions(new ExportOptionsContribution(
            L("轨道效果器", "Track effects"),
            CreateContent,
            Refresh));
    }

    public void Detach()
    {
        mHost = null;
        mCheckBox = null;
    }

    object CreateContent()
    {
        var checkBox = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        mCheckBox = checkBox;

        checkBox.Switched.Subscribe(() =>
        {
            if (mSyncing || mHost == null)
                return;

            mHost.ExportIncludeEffects = checkBox.IsChecked;
        });

        var label = new TextBlock
        {
            Text = L("包含轨道效果器", "Include track effects"),
            FontSize = 11,
            Foreground = Palette.Brush(Palette.LightWhite),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new Grid
        {
            Margin = new Thickness(12, 0, 12, 8),
            Height = 24,
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };

        Grid.SetColumn(label, 0);
        Grid.SetColumn(checkBox, 1);
        row.Children.Add(label);
        row.Children.Add(checkBox);

        Refresh();
        return row;
    }

    // 宿主切换工程时调用：重新读当前工程的导出开关。
    void Refresh()
    {
        if (mCheckBox == null || mHost == null)
            return;

        mSyncing = true;
        try
        {
            mCheckBox.IsChecked = mHost.ExportIncludeEffects;
        }
        finally
        {
            mSyncing = false;
        }
    }

    string L(string zh, string en)
        => mHost != null && mHost.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;

    IHostServices? mHost;
    CheckBox? mCheckBox;
    bool mSyncing;
}
