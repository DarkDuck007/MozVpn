using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Moz_Avalonia.Models;

namespace Moz_Avalonia.Controls;

public sealed class StatSparkline : Control
{
    public static readonly StyledProperty<StatItem?> StatProperty =
        AvaloniaProperty.Register<StatSparkline, StatItem?>(nameof(Stat));

    private static readonly Pen LinePen = new(new SolidColorBrush(Color.Parse("#4CC9F0")), 1.5);
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#0B1724"));

    public StatItem? Stat
    {
        get => GetValue(StatProperty);
        set => SetValue(StatProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != StatProperty) return;
        if (change.OldValue is StatItem oldItem) oldItem.PropertyChanged -= StatChanged;
        if (change.NewValue is StatItem newItem) newItem.PropertyChanged += StatChanged;
        InvalidateVisual();
    }

    private void StatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatItem.GraphVersion)) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size), 2);
        if (Stat?.History is not { Count: >= 2 } values) return;

        var minimum = values.Min();
        var range = Math.Max(1, values.Max() - minimum);
        var step = Bounds.Width / Math.Max(59, values.Count - 1);
        for (var i = 1; i < values.Count; i++)
        {
            var x1 = Bounds.Width - (values.Count - i) * step;
            var x0 = x1 - step;
            var y0 = Bounds.Height - (values[i - 1] - minimum) / range * (Bounds.Height - 6) - 3;
            var y1 = Bounds.Height - (values[i] - minimum) / range * (Bounds.Height - 6) - 3;
            context.DrawLine(LinePen, new Point(x0, y0), new Point(x1, y1));
        }
    }
}
