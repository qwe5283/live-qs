using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Common;

namespace LiveQs.Windows.Controls;

public sealed class ActiveTimeChart : FrameworkElement
{
    private const double AxisWidth = 44;
    private const double RightMargin = 10;
    private const double TopMargin = 16;
    private const double BottomMargin = 32;
    private int? _hoveredIndex;

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(ActiveTimeChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnDataChanged));
    public static readonly DependencyProperty IsHourlyProperty = DependencyProperty.Register(
        nameof(IsHourly), typeof(bool), typeof(ActiveTimeChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnDataChanged));
    public static readonly DependencyProperty BarBrushProperty = RegisterBrush(nameof(BarBrush), Brushes.DodgerBlue);
    public static readonly DependencyProperty TextBrushProperty = RegisterBrush(nameof(TextBrush), Brushes.Gray);
    public static readonly DependencyProperty GridBrushProperty = RegisterBrush(nameof(GridBrush), Brushes.LightGray);

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public bool IsHourly
    {
        get => (bool)GetValue(IsHourlyProperty);
        set => SetValue(IsHourlyProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = ItemsSource?.OfType<ActiveTimeBucket>().Count() ?? 0;
        return new Size(Math.Max(260, IsHourly || count <= 31 ? 260 : AxisWidth + RightMargin + count * 12), 280);
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var items = ItemsSource?.OfType<ActiveTimeBucket>().ToArray() ?? [];
        var width = ActualWidth - AxisWidth - RightMargin;
        var height = ActualHeight - TopMargin - BottomMargin;
        if (width <= 0 || height <= 0 || items.Length == 0) return;
        var baseline = TopMargin + height;
        var gridPen = new Pen(GridBrush, 1);

        for (var tick = 0; tick <= 4; tick++)
        {
            var y = baseline - height * tick / 4;
            context.DrawLine(gridPen, new Point(AxisWidth, y), new Point(ActualWidth - RightMargin, y));
            var label = Text($"{tick * 25}%");
            context.DrawText(label, new Point(AxisWidth - label.Width - 8, y - label.Height / 2));
        }

        var slot = width / items.Length;
        var barWidth = Math.Max(1, Math.Min(28, slot * 0.65));
        var labelStep = Math.Max(1, (int)Math.Ceiling((IsHourly ? 20 : 46) / slot));
        for (var index = 0; index < items.Length; index++)
        {
            var x = AxisWidth + slot * (index + 0.5);
            var barHeight = height * items[index].Share;
            if (barHeight > 0)
            {
                context.PushOpacity(_hoveredIndex == index ? 0.75 : 1);
                context.DrawRoundedRectangle(BarBrush, null,
                    new Rect(x - barWidth / 2, baseline - barHeight, barWidth, barHeight), 2, 2);
                context.Pop();
            }
            if (index % labelStep != 0) continue;
            var label = Text(items[index].LocalStart.ToString(IsHourly ? "HH" : "M/d", CultureInfo.CurrentCulture));
            context.DrawText(label, new Point(x - label.Width / 2, baseline + 10));
        }
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters parameters) =>
        new PointHitTestResult(this, parameters.HitPoint);

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        var items = ItemsSource?.OfType<ActiveTimeBucket>().ToArray() ?? [];
        var point = args.GetPosition(this);
        var width = ActualWidth - AxisWidth - RightMargin;
        int? index = items.Length > 0 && width > 0 && point.X >= AxisWidth && point.X < AxisWidth + width
            && point.Y >= TopMargin && point.Y <= ActualHeight - BottomMargin
            ? Math.Min(items.Length - 1, (int)((point.X - AxisWidth) / width * items.Length))
            : null;
        if (_hoveredIndex == index) return;
        _hoveredIndex = index;
        ToolTip = index is { } selected
            ? $"{items[selected].LocalStart.ToString(IsHourly ? "M月d日 HH:00" : "yyyy年M月d日", CultureInfo.CurrentCulture)}\n活跃 {DurationText.Format(items[selected].ActiveDuration)} · {items[selected].Share:P1}"
            : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs args)
    {
        base.OnMouseLeave(args);
        _hoveredIndex = null;
        ToolTip = null;
        InvalidateVisual();
    }

    private FormattedText Text(string value) => new(value, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, new Typeface("Segoe UI, Microsoft YaHei UI"), 11, TextBrush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static DependencyProperty RegisterBrush(string name, Brush defaultBrush) => DependencyProperty.Register(
        name, typeof(Brush), typeof(ActiveTimeChart),
        new FrameworkPropertyMetadata(defaultBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    private static void OnDataChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var chart = (ActiveTimeChart)owner;
        chart._hoveredIndex = null;
        chart.ToolTip = null;
    }
}
