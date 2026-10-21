using System;
using System.Linq;
using TuneLab;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.SDK;
using Xunit;

namespace TuneLab.Tests;

// 「移动到 X」的语义：**插入到 X 现在的位置**，不是交换。
//
// 两条相邻时"插入"和"交换"结果相同，三条以上才分得出来 —— 所以这里必须用三条。
public class MixerChannelOrderTests
{
    [Fact]
    public void MovingForwardInsertsAndShiftsTheTargetBack()
    {
        var model = Build("A", "B", "C", "D");

        // 把 D 移到 B 的位置：B、C 整体后移，D 占住 B 原来的位置。
        model.MoveChannelTo(ByName(model, "D"), ByName(model, "B"));

        Assert.Equal("A,D,B,C", string.Join(",", TrackNames(model)));
    }

    [Fact]
    public void MovingBackwardInsertsAndShiftsTheTargetForward()
    {
        var model = Build("A", "B", "C", "D");

        // 把 A 移到 C 的位置：C、D 整体前移，A 占住 C 原来的位置。
        model.MoveChannelTo(ByName(model, "A"), ByName(model, "C"));

        Assert.Equal("B,C,A,D", string.Join(",", TrackNames(model)));
    }

    static IMixerModel Build(params string[] names)
    {
        var project = new Project();

        foreach (string name in names)
            project.AddTrack(new TrackInfo { Name = name });

        return new MixerModelAdapter(project);
    }

    static IMixerChannel ByName(IMixerModel model, string name)
        => model.Channels.First(channel => channel.Kind == MixerChannelKind.Track && channel.Name == name);

    static string[] TrackNames(IMixerModel model)
        => model.Channels
            .Where(channel => channel.Kind == MixerChannelKind.Track)
            .Select(channel => channel.Name)
            .ToArray();
}

