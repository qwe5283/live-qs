using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LiveQs.Windows.Core.Analytics;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace LiveQs.Windows.Controls;

public sealed class ActiveTimeChart : Panel
{
    private const double AxisWidth = 56;
    private const double RightMargin = 10;
    private const double TopMargin = 16;
    private const double BottomMargin = 32;
    private ActiveTimeBucket[] _items = [];

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(ActiveTimeChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnDataChanged));
    public static readonly DependencyProperty IsHourlyProperty = DependencyProperty.Register(
        nameof(IsHourly), typeof(bool), typeof(ActiveTimeChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnDataChanged));
    public static readonly DependencyProperty BarBrushProperty = RegisterBrush(nameof(BarBrush), Brushes.DodgerBlue);
    public static readonly DependencyProperty TextBrushProperty = RegisterBrush(nameof(TextBrush), Brushes.Gray);
    public static readonly DependencyProperty GridBrushProperty = RegisterBrush(nameof(GridBrush), Brushes.LightGray);

    public ActiveTimeChart()
    {
        Unloaded += (_, _) => CloseTooltips();
    }

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
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        var count = _items.Length;
        return new Size(Math.Max(260, IsHourly || count <= 31 ? 260 : AxisWidth + RightMargin + count * 12), 280);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Max(0, finalSize.Width - AxisWidth - RightMargin);
        var height = Math.Max(0, finalSize.Height - TopMargin - BottomMargin);
        var slot = _items.Length > 0 ? width / _items.Length : 0;
        var barWidth = Math.Max(1, Math.Min(28, slot * 0.65));
        var maximum = GetTickStep() * 4;
        for (var index = 0; index < _items.Length; index++)
        {
            var x = AxisWidth + slot * (index + 0.5);
            var barHeight = height * Math.Max(0, _items[index].ActiveDuration.TotalSeconds / UnitSeconds) / maximum;
            Children[index].Arrange(new Rect(x - barWidth / 2, TopMargin + height - barHeight, barWidth, barHeight));
        }
        return finalSize;
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var width = ActualWidth - AxisWidth - RightMargin;
        var height = ActualHeight - TopMargin - BottomMargin;
        if (width <= 0 || height <= 0 || _items.Length == 0)
        {
            return;
        }

        var baseline = TopMargin + height;
        var gridPen = new Pen(GridBrush, 1);
        var unitText = IsHourly ? "分钟" : "小时";
        var tickStep = GetTickStep();

        for (var tick = 0; tick <= 4; tick++)
        {
            var y = baseline - height * tick / 4;
            context.DrawLine(gridPen, new Point(AxisWidth, y), new Point(ActualWidth - RightMargin, y));
            var label = CreateAxisLabel($"{tick * tickStep:0.#}{unitText}");
            context.DrawText(label, new Point(AxisWidth - label.Width - 8, y - label.Height / 2));
        }

        var slot = width / _items.Length;
        var labelStep = Math.Max(1, (int)Math.Ceiling((IsHourly ? 20 : 46) / slot));
        for (var index = 0; index < _items.Length; index++)
        {
            var x = AxisWidth + slot * (index + 0.5);
            if (index % labelStep != 0)
            {
                continue;
            }

            var label = CreateAxisLabel(_items[index].LocalStart.ToString(IsHourly ? "HH" : "M/d", CultureInfo.CurrentCulture));
            context.DrawText(label, new Point(x - label.Width / 2, baseline + 10));
        }
    }

    private double UnitSeconds => IsHourly ? 60 : 3600;

    private double GetTickStep()
    {
        var minimumMaximum = IsHourly ? 60d : 24d;
        var quantum = IsHourly ? 15d : 1d;
        var maximumDuration = _items.Length > 0 ? _items.Max(item => item.ActiveDuration.TotalSeconds / UnitSeconds) : 0;
        return Math.Ceiling(Math.Max(minimumMaximum, maximumDuration) / 4 / quantum) * quantum;
    }

    private void UpdateBars()
    {
        CloseTooltips();
        Children.Clear();
        _items = ItemsSource?.OfType<ActiveTimeBucket>().ToArray() ?? [];
        foreach (var item in _items)
        {
            var bar = new Rectangle
            {
                RadiusX = 2,
                RadiusY = 2,
                Visibility = item.ActiveDuration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed,
                ToolTip = new ToolTip { Content = FormatTooltip(item) },
            };
            bar.SetBinding(Rectangle.FillProperty, new Binding(nameof(BarBrush)) { Source = this });
            ToolTipService.SetInitialShowDelay(bar, 250);
            ToolTipService.SetBetweenShowDelay(bar, 500);
            ToolTipService.SetShowDuration(bar, int.MaxValue);
            bar.MouseEnter += static (sender, _) => ((Rectangle)sender).Opacity = 0.75;
            bar.MouseLeave += static (sender, _) => ((Rectangle)sender).Opacity = 1;
            Children.Add(bar);
        }
    }

    private string FormatTooltip(ActiveTimeBucket item)
    {
        var end = IsHourly ? item.LocalStart.AddHours(1) : item.LocalStart.AddDays(1);
        var startText = item.LocalStart.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        var endText = end.ToString(end.Date == item.LocalStart.Date ? "HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        var duration = TimeSpan.FromMilliseconds(Math.Round(item.ActiveDuration.TotalMilliseconds, MidpointRounding.AwayFromZero));
        var seconds = (duration.Ticks % TimeSpan.TicksPerMinute) / (double)TimeSpan.TicksPerSecond;
        var value = $"{seconds:0} 秒";
        if (duration.TotalMinutes >= 1)
        {
            value = $"{duration.Minutes} 分钟 {value}";
        }

        if (duration.TotalHours >= 1)
        {
            value = $"{duration.Ticks / TimeSpan.TicksPerHour} 小时 {value}";
        }

        return $"{startText} – {endText}\n活跃时长：{value}";
    }

    private void CloseTooltips()
    {
        foreach (var bar in Children.OfType<Rectangle>())
        {
            if (bar.ToolTip is ToolTip tooltip)
            {
                tooltip.IsOpen = false;
            }
        }
    }

    private FormattedText CreateAxisLabel(string value) => new(value, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, new Typeface("Segoe UI, Microsoft YaHei UI"), 11, TextBrush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static DependencyProperty RegisterBrush(string name, Brush defaultBrush) => DependencyProperty.Register(
        name, typeof(Brush), typeof(ActiveTimeChart),
        new FrameworkPropertyMetadata(defaultBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    private static void OnDataChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        ((ActiveTimeChart)owner).UpdateBars();
    }
}
