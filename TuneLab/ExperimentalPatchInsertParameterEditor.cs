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
using TuneLab.GUI.Components;
using Slider = TuneLab.GUI.Components.Slider;
using HostLog = TuneLab.Foundation.Log;

namespace TuneLab.Audio;

// 参数面板：**不依赖插件 GUI**，把插件暴露的参数列成滑杆。
//
// 【为什么要有它】插件 GUI 可能根本画不出来 —— 缺运行时依赖、没有可用的 OpenGL
// （虚拟机上的 DPF/NanoVG 就是这样，见 DESIGN §5.7.11），或者插件本身就没做界面。
// 那时参数并不是取不到：VST3 的 IEditController / VST2 的 getParameter 一直都在。
// 这个窗口就是"用宿主自己的控件把参数摆出来"，思路与 JSFX 的非 @gfx 渲染一致。
//
// 【控件】用的是 TuneLab.GUI 里侧栏同款：Slider（带默认值区间高亮、双击回默认）
// 与 DraggableNumberBox（拖动擦写、双击键入）。TuneLab.GUI 对 TuneLab 有
// InternalsVisibleTo，所以宿主直接用，不另画一套。
//
// 【刷新】插件 GUI 改参数时我们要跟着变，所以定时读一遍。正在拖的那一行不覆盖
// （`Dragging`），否则手指还没松开值就被拉回去了。VST2 要过桥，刷新频率放低。
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
    readonly string mHeaderText;
    readonly StackPanel mRows = new() { Spacing = 2 };
    readonly TextBlock mEmpty = new();
    readonly List<Row> mRowViews = [];

    DispatcherTimer? mPoll;

    sealed class Row
    {
        public required int Id { get; init; }
        public required Slider Slider { get; init; }
        public required DraggableNumberBox Box { get; init; }
        public bool Dragging { get; set; }
    }

    InsertParameterWindow(string title, ParameterSource source)
    {
        mSource = source;

        // 抬头只说"哪条轨道的哪个 FX"，别堆功能说明。
        Title = title;
        mHeaderText = title;

        Width = 660;
        Height = 540;
        MinWidth = 460;
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
                Write = (id, value) =>
                {
                    vst3.SetParameter(id, value);

                    // VST3 的参数改动要等一次 process() 才被处理器消费。播放中音频线程
                    // 每块都在跑，自然就生效；停止时没人跑，改动会一直排队、读回来还是旧值
                    // （用户报的"调完几秒后自动变回去"）。所以停止时补一块静音。
                    if (!AudioEngine.IsPlaying)
                        vst3.FlushParameterChanges();
                },
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
                Text = mHeaderText,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 216)),
                TextTrimming = TextTrimming.CharacterEllipsis,
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
        double initial = Math.Clamp(double.IsFinite(parameter.Value) ? parameter.Value : min, min, max);
        double defaultValue = parameter.Default is { } d && double.IsFinite(d) ? Math.Clamp(d, min, max) : initial;

        var name = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(parameter.Name) ? "#" + parameter.Id : parameter.Name,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(208, 208, 214)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 0, 8, 0),
        };

        // 与侧栏同款：默认值区间高亮、双击回默认。
        var slider = new Slider
        {
            Direction = SliderDirection.LeftToRight,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        slider.SetRange(min, max);
        slider.DefaultValue = defaultValue;
        slider.Display(initial);

        // 数值框：拖动擦写、双击键入精确值。
        var box = new DraggableNumberBox
        {
            Width = 78,
            Height = 24,
            MinValue = min,
            MaxValue = max,
            DefaultValue = defaultValue,
            NumberFormat = TuneLab.SDK.NumberFormat.Decimals(3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.Display(initial);

        var row = new Row { Id = parameter.Id, Slider = slider, Box = box };

        slider.ValueWillChange.Subscribe(() => row.Dragging = true);
        slider.ValueCommitted.Subscribe(() => row.Dragging = false);

        slider.ValueChanged.Subscribe(() =>
        {
            double value = slider.Value;
            box.Display(value);
            mSource.Write(parameter.Id, value);
        });

        box.ValueChanged.Subscribe(() =>
        {
            double value = box.Value;
            slider.Display(value);
            mSource.Write(parameter.Id, value);
        });

        var cell = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("210,*,86"),
            Height = 30,
        };

        Grid.SetColumn(name, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(box, 2);
        cell.Children.Add(name);
        cell.Children.Add(slider);
        cell.Children.Add(box);

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

            if (row.Dragging)
                continue;

            // Display 只刷新显示、不触发 ValueChanged —— 否则回填会被当成用户改动写回插件。
            row.Slider.Display(Math.Clamp(current, row.Slider.MinValue, row.Slider.MaxValue));
            row.Box.Display(current);
        }
    }
}

