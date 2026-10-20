using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.MixerEngine;
using HostLog = TuneLab.Foundation.Log;

namespace TuneLab.Audio;

// TuneLab 接管的参数面板：**不依赖插件 GUI**，把插件暴露的参数列成滑杆。
//
// 【为什么要有它】插件 GUI 可能根本画不出来 —— 缺运行时依赖、没有可用的 OpenGL
// （虚拟机上的 DPF/NanoVG 就是这样，见 DESIGN §5.7.11），或者插件本身就没做界面。
// 那时参数并不是取不到：VST3 的 IEditController / VST2 的 getParameter 一直都在。
// 这个窗口就是"用宿主自己的控件把参数摆出来"，思路与 JSFX 的非 @gfx 渲染一致。
//
// 【刷新】插件 GUI 改参数时我们要跟着变，所以定时读一遍。但**正在拖的那一根不覆盖**，
// 否则手指还没松开值就被拉回去了。VST2 要过桥，刷新频率放低（1.5s），VST3 是本地调用（200ms）。
public sealed class InsertParameterWindow : Window
{
    sealed record Parameter(int Id, string Name, double Min, double Max, double Value, double? Default);

    sealed class ParameterSource
    {
        public required Func<IReadOnlyList<Parameter>> Read { get; init; }
        public required Action<int, double> Write { get; init; }
        public required TimeSpan PollInterval { get; init; }
    }

    readonly ParameterSource mSource;
    readonly StackPanel mRows = new() { Spacing = 4 };
    readonly TextBlock mEmpty = new();
    readonly List<Row> mRowViews = [];

    DispatcherTimer? mPoll;

    sealed class Row
    {
        public required int Id { get; init; }
        public required Slider Slider { get; init; }
        public required TextBlock ValueText { get; init; }
        public bool Dragging { get; set; }

        // 轮询回填时置位：否则 Value 的 setter 会把同一个值再写回插件，
        // 每秒几十次无意义往返（VST2 还要过桥）。
        public bool Applying { get; set; }
    }

    InsertParameterWindow(string title, ParameterSource source)
    {
        mSource = source;

        Title = title;
        Width = 620;
        Height = 520;
        MinWidth = 420;
        MinHeight = 240;
        Background = new SolidColorBrush(Color.FromRgb(26, 26, 30));

        Content = BuildContent();

        Opened += (_, _) => { Reload(); StartPolling(); };
        Closed += (_, _) => mPoll?.Stop();
    }

    public static InsertParameterWindow Create(string pluginName, IMixInsert insert)
    {
        ParameterSource source = insert switch
        {
            Vst3Insert vst3 => new ParameterSource
            {
                Read = () => vst3.Parameters
                    .Select(p => new Parameter(p.Id, p.Name, p.MinValue, p.MaxValue, p.CurrentValue,
                        p.DefaultValue is >= double.MinValue and <= double.MaxValue ? p.DefaultValue : null))
                    .ToList(),
                Write = (id, value) => vst3.SetParameter(id, value),
                PollInterval = TimeSpan.FromMilliseconds(200),
            },
            Vst2Insert vst2 => new ParameterSource
            {
                Read = () => vst2.GetParameters()
                    .Select(p => new Parameter((int)p.Id, p.Name, p.Min, p.Max, p.Value, null))
                    .ToList(),
                Write = (id, value) => vst2.SetParameter(id, value),
                // 过桥：每次刷新都是一次命名管道往返，别太勤。
                PollInterval = TimeSpan.FromMilliseconds(1500),
            },
            _ => new ParameterSource
            {
                Read = () => [],
                Write = (_, _) => { },
                PollInterval = TimeSpan.FromSeconds(2),
            },
        };

        return new InsertParameterWindow(pluginName, source);
    }

    Control BuildContent()
    {
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(34, 34, 40)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 64)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6),
            Child = new TextBlock
            {
                Text = "由 TuneLab 接管：参数面板（不依赖插件界面）",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 216)),
            },
        };

        mEmpty.Text = "这个插件没有暴露任何参数";
        mEmpty.FontSize = 11;
        mEmpty.Margin = new Thickness(16);
        mEmpty.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 156));
        mEmpty.IsVisible = false;

        var scroller = new ScrollViewer
        {
            Content = new StackPanel { Children = { mRows, mEmpty } },
            Padding = new Thickness(10, 8),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(header, 0);
        Grid.SetRow(scroller, 1);
        grid.Children.Add(header);
        grid.Children.Add(scroller);

        return grid;
    }

    void Reload()
    {
        IReadOnlyList<Parameter> parameters;

        try
        {
            parameters = mSource.Read();
        }
        catch (Exception ex)
        {
            HostLog.Warning("insert: failed to read plugin parameters: " + ex.Message);
            parameters = [];
        }

        mRows.Children.Clear();
        mRowViews.Clear();

        mEmpty.IsVisible = parameters.Count == 0;
        mRows.IsVisible = parameters.Count > 0;

        foreach (var parameter in parameters)
            mRows.Children.Add(BuildRow(parameter));
    }

    Control BuildRow(Parameter parameter)
    {
        // 插件可能给出 min == max（坏数据）或者 NaN：退回 0..1，至少能拖。
        double min = double.IsFinite(parameter.Min) ? parameter.Min : 0;
        double max = double.IsFinite(parameter.Max) && parameter.Max > parameter.Min ? parameter.Max : 1;

        var name = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(parameter.Name) ? "#" + parameter.Id : parameter.Name,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(208, 208, 214)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var value = new TextBlock
        {
            Text = Format(parameter.Value),
            FontSize = 11,
            Width = 74,
            TextAlignment = TextAlignment.Right,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 188)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(parameter.Value, min, max),
            VerticalAlignment = VerticalAlignment.Center,
            Height = 20,
        };

        var row = new Row { Id = parameter.Id, Slider = slider, ValueText = value };

        slider.PropertyChanged += (_, change) =>
        {
            if (change.Property != RangeBase.ValueProperty || row.Applying)
                return;

            // 用户拖的时候别再被轮询覆盖（Dragging 在指针按下/松开时切换）。
            if (row.Dragging)
                value.Text = Format(slider.Value);

            mSource.Write(parameter.Id, slider.Value);
            value.Text = Format(slider.Value);
        };

        slider.PointerPressed += (_, _) => row.Dragging = true;
        slider.PointerReleased += (_, _) => row.Dragging = false;

        var cell = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*,74") };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        cell.Children.Add(name);
        cell.Children.Add(slider);
        cell.Children.Add(value);

        if (parameter.Default is { } defaultValue)
        {
            var reset = new Button
            {
                Content = "↺",
                FontSize = 10,
                Width = 22,
                Height = 18,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 168)),
                BorderThickness = default,
            };

            reset.Click += (_, _) =>
            {
                double target = Math.Clamp(defaultValue, min, max);
                slider.Value = target;
            };

            cell.ColumnDefinitions = new ColumnDefinitions("220,*,74,28");
            Grid.SetColumn(reset, 3);
            cell.Children.Add(reset);
        }

        mRowViews.Add(row);
        return cell;
    }

    void StartPolling()
    {
        mPoll = new DispatcherTimer { Interval = mSource.PollInterval };
        mPoll.Tick += (_, _) => Poll();
        mPoll.Start();
    }

    // 只更新数值，不重建控件（重建会把正在拖的滑杆抽走）。
    void Poll()
    {
        if (mRowViews.Count == 0)
            return;

        IReadOnlyList<Parameter> parameters;

        try
        {
            parameters = mSource.Read();
        }
        catch (Exception)
        {
            return;
        }

        if (parameters.Count != mRowViews.Count)
        {
            // 参数个数变了（插件换了？）：整表重建一次。
            Reload();
            return;
        }

        for (int i = 0; i < mRowViews.Count; i++)
        {
            var row = mRowViews[i];
            double current = parameters[i].Value;

            if (!double.IsFinite(current))
                continue;

            row.ValueText.Text = Format(current);

            if (row.Dragging)
                continue;

            row.Applying = true;
            try
            {
                row.Slider.Value = Math.Clamp(current, row.Slider.Minimum, row.Slider.Maximum);
            }
            finally
            {
                row.Applying = false;
            }
        }
    }

    static string Format(double value)
    {
        if (!double.IsFinite(value))
            return "—";

        return Math.Abs(value) >= 1000
            ? value.ToString("0")
            : value.ToString("0.###");
    }
}

