﻿using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TuneLab.GUI.Components;
using TuneLab.GUI;
using TuneLab.Utils;
using TuneLab.Base.Science;
using Avalonia.Controls;

namespace TuneLab.Views;

internal class GainSlider : AbstractSlider
{
    public GainSlider()
    {
        Thumb = new GainThumb(this);
        PointerMoved += (s, e) => { ShowTip(); };
        ShowTip();
    }

    private void ShowTip()
    {
        ToolTip.SetPlacement(this, PlacementMode.Top);
        ToolTip.SetVerticalOffset(this, -this.Height / 2);
        ToolTip.SetShowDelay(this, 0);
        var x = MathUtility.LineValue(MinValue, StartPoint.X, MaxValue, EndPoint.X, Value) - Bounds.Width / 2;
        ToolTip.SetHorizontalOffset(this, x);
        ToolTip.SetTip(this, Value.ToString("+0.00dB;-0.00dB"));
    }

    protected override Point StartPoint => new(2, 0);
    protected override Point EndPoint => new(Bounds.Width - 2, 0);

    protected override void OnDraw(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, this.Rect());
        const double height = 6;
        context.FillRectangle(Style.BACK.ToBrush(), new Rect(0, (Bounds.Height - height) / 2, Bounds.Width, height));
    }

    protected override void OnSizeChanged(Avalonia.Controls.SizeChangedEventArgs e)
    {
        if (Thumb == null)
            return;

        Thumb.Height = e.NewSize.Height;
        InvalidateArrange();
    }

    class GainThumb : AbstractThumb
    {
        public GainThumb(GainSlider slider) : base(slider)
        {
            Width = 4;
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Style.LIGHT_WHITE.ToBrush(), this.Rect(), 1);
        }
    }
}