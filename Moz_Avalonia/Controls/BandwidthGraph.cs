using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Moz_Avalonia.ViewModels;

namespace Moz_Avalonia.Controls;

public sealed class BandwidthGraph : Control
{
    public static readonly StyledProperty<ConnectionViewModel?> ConnectionProperty =
        AvaloniaProperty.Register<BandwidthGraph, ConnectionViewModel?>(nameof(Connection));

    private static readonly Pen InboundPen = new(new SolidColorBrush(Color.Parse("#4CC9F0")), 2.5);
    private static readonly Pen OutboundPen = new(new SolidColorBrush(Color.Parse("#F72585")), 2.5);
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#294052")), 1);

    public ConnectionViewModel? Connection
    {
        get => GetValue(ConnectionProperty);
        set => SetValue(ConnectionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ConnectionProperty)
        {
            if (change.OldValue is ConnectionViewModel oldConnection) oldConnection.PropertyChanged -= ConnectionChanged;
            if (change.NewValue is ConnectionViewModel newConnection) newConnection.PropertyChanged += ConnectionChanged;
            InvalidateVisual();
        }
    }

    private void ConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.GraphVersion)) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new LinearGradientBrush
        {
            StartPoint = RelativePoint.TopLeft,
            EndPoint = RelativePoint.BottomRight,
            GradientStops =
            {
                new GradientStop(Color.Parse("#142536"), 0),
                new GradientStop(Color.Parse("#0B1724"), 1)
            }
        }, bounds, 3);
        for (var i = 1; i < 4; i++)
            context.DrawLine(GridPen, new Point(0, Bounds.Height * i / 4), new Point(Bounds.Width, Bounds.Height * i / 4));

        if (Connection is null) return;
        var maximum = Math.Max(1, Connection.InboundHistory.Concat(Connection.OutboundHistory).DefaultIfEmpty().Max());
        DrawSeries(context, Connection.InboundHistory, maximum, InboundPen);
        DrawSeries(context, Connection.OutboundHistory, maximum, OutboundPen);
    }

    private void DrawSeries(DrawingContext context, System.Collections.Generic.IReadOnlyList<double> values, double maximum, Pen pen)
    {
        if (values.Count < 2) return;
        var width = Bounds.Width / Math.Max(89, values.Count - 1);
        for (var i = 1; i < values.Count; i++)
        {
            var x1 = Bounds.Width - (values.Count - i) * width;
            var x0 = x1 - width;
            var y0 = Bounds.Height - values[i - 1] / maximum * (Bounds.Height - 10) - 5;
            var y1 = Bounds.Height - values[i] / maximum * (Bounds.Height - 10) - 5;
            context.DrawLine(pen, new Point(x0, y0), new Point(x1, y1));
        }
    }
}
