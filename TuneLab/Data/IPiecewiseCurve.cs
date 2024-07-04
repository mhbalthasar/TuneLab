﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TuneLab.Base.Data;
using TuneLab.Base.Event;
using TuneLab.Base.Structures;
using TuneLab.Base.Utils;

namespace TuneLab.Data;

internal interface IPiecewiseCurve : IDataObject<IEnumerable<IReadOnlyCollection<Point>>>
{
    IActionEvent<double, double> RangeModified { get; }
    IReadOnlyList<IAnchorGroup> AnchorGroups { get; }
    double[] GetValues(IReadOnlyList<double> ticks);
    void AddLine(IReadOnlyList<AnchorPoint> points, double extend);
    void Clear(double start, double end);
    void InsertPoint(AnchorPoint point);
    void DeletePoints(double start, double end);
    void DeletePoints(IReadOnlyList<AnchorPoint> points);
    void DeleteAllSelectedAnchors();
    void DeleteAnchorGroupAt(int index);
    void ConnectAnchorGroup(int leftIndex);
    void MoveSelectedPoints(double offsetPos, double offsetValue);

    new List<List<Point>> GetInfo();

    IEnumerable<IReadOnlyCollection<Point>> IReadOnlyDataObject<IEnumerable<IReadOnlyCollection<Point>>>.GetInfo() => GetInfo();
}

internal static class IPiecewiseCurveExtension
{
    public struct AreaID(int index)
    {
        public static implicit operator AreaID(int index)
        {
            return new AreaID(index);
        }

        public int Index => index;
        public int LeftIndex => -2 - index;
        public int RightIndex => -1 - index;
        public bool IsInGroup => index >= 0;
    }

    public static double GetValue(this IPiecewiseCurve piecewiseCurve, double x)
    {
        return piecewiseCurve.GetValues([x])[0];
    }

    public static List<List<Point>> RangeInfo(this IPiecewiseCurve piecewiseCurve, double start, double end)
    {
        var result = new List<List<Point>>();
        foreach (var anchorGroup in piecewiseCurve.AnchorGroups)
        {
            if (anchorGroup.End <= start)
                continue;

            if (anchorGroup.Start >= end)
                break;

            result.Add(anchorGroup.RangeInfo(start, end));
        }
        return result;
    }

    public static void AddLine(this IPiecewiseCurve piecewiseCurve, IReadOnlyList<Point> points, double extend)
    {
        piecewiseCurve.AddLine(points.Convert(p => new AnchorPoint(p)), extend);
    }
  
    public static AreaID GetAreaID(this IPiecewiseCurve piecewiseCurve, double pos)
    {
        int i = 0;
        for (; i < piecewiseCurve.AnchorGroups.Count; i++)
        {
            var anchorGroup = piecewiseCurve.AnchorGroups[i];
            if (pos < anchorGroup.Start)
                return -1 - i;

            if (pos <= anchorGroup.End)
                return i;
        }

        return -1 - i;
    }

    public static List<(List<Point>, IAnchorGroup)> TakeAllSelectedAnchors(this IPiecewiseCurve piecewiseCurve)
    {
        var result = new List<(List<Point>, IAnchorGroup)>();
        for (int gi = piecewiseCurve.AnchorGroups.Count - 1; gi >= 0; gi--)
        {
            var anchorGroup = piecewiseCurve.AnchorGroups[gi];
            for (int pi = anchorGroup.Count - 1; pi >= 0; pi--)
            {
                if (anchorGroup[pi].IsSelected)
                    anchorGroup.RemoveAt(pi);
            }

            if (anchorGroup.IsEmpty())
                piecewiseCurve.DeleteAnchorGroupAt(gi);
        }
        return result;
    }

    public static void SelectAllAnchors(this IPiecewiseCurve piecewiseCurve)
    {
        foreach (var anchorGroup in piecewiseCurve.AnchorGroups)
        {
            anchorGroup.SelectAllItems();
        }
    }

    public static void DeselectAllAnchors(this IPiecewiseCurve piecewiseCurve)
    {
        foreach (var anchorGroup in piecewiseCurve.AnchorGroups)
        {
            anchorGroup.DeselectAllItems();
        }
    }
    public static void MoveLine(this IPiecewiseCurve piecewiseCurve, Tuple<double, double>[] ranges, double TickOffset, double PitchOffset)
    {
        List<List<Point>> newLines = new List<List<Point>>();
        foreach (var range in ranges)
        {
            List<double> ticks = new List<double>();
            for (long i = (long)range.Item1; i < (long)range.Item2; i++) ticks.Add(i);
            double[] pitches = piecewiseCurve.GetValues(ticks.ToArray());
            List<Point> newLine = new List<Point>();
            for (int i = 0; i < ticks.Count; i++)
            {
                newLine.Add(new Point() { X = ticks[i] + TickOffset, Y = pitches[i] + PitchOffset });
            }
            newLines.Add(newLine);
            piecewiseCurve.Clear(range.Item1, range.Item2);
        }
        foreach (var newLine in newLines) AddLine(piecewiseCurve, newLine, 5);
    }
}
