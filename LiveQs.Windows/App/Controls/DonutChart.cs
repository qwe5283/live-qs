using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using LiveQs.Windows.Core;

namespace LiveQs.Windows.App.Controls;

public sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));

    private INotifyCollectionChanged? _observable;

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var size = Math.Max(0, Math.Min(ActualWidth, ActualHeight) - 24);
        if (size <= 0) return;
        var center = new System.Windows.Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2;
        var thickness = Math.Clamp(size * 0.14, 14, 26);
        var background = new Pen(new SolidColorBrush(Color.FromRgb(226, 231, 234)), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        drawingContext.DrawEllipse(null, background, center, radius, radius);

        var items = ItemsSource?.OfType<AppUsage>().Where(item => item.Share > 0).ToArray() ?? [];
        var startAngle = -90d;
        foreach (var item in items)
        {
            var sweep = Math.Min(359.85, item.Share * 360);
            if (sweep < 0.35) continue;
            DrawArc(drawingContext, center, radius, startAngle, sweep, item.Color, thickness);
            startAngle += sweep;
        }
    }

    private static void DrawArc(DrawingContext context, System.Windows.Point center, double radius, double start, double sweep, string color, double thickness)
    {
        var startPoint = PointAt(center, radius, start);
        var endPoint = PointAt(center, radius, start + sweep);
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            drawing.BeginFigure(startPoint, false, false);
            drawing.ArcTo(endPoint, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pen.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private static System.Windows.Point PointAt(System.Windows.Point center, double radius, double angle)
    {
        var radians = angle * Math.PI / 180;
        return new System.Windows.Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static void OnItemsChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var chart = (DonutChart)owner;
        if (chart._observable is not null) chart._observable.CollectionChanged -= chart.OnCollectionChanged;
        chart._observable = args.NewValue as INotifyCollectionChanged;
        if (chart._observable is not null) chart._observable.CollectionChanged += chart.OnCollectionChanged;
        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => InvalidateVisual();
}
