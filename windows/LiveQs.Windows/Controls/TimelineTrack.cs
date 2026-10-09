using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LiveQs.Windows.ViewModels;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace LiveQs.Windows.Controls;

/// <summary>Proportional foreground marks with native tooltips and time selection.</summary>
public sealed class TimelineTrack : Panel
{
    private readonly Dictionary<string, Brush> _brushes = new(StringComparer.OrdinalIgnoreCase);
    private TimelineMark[] _marks = [];
    private Rectangle? _selection;

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(TimelineTrack), new FrameworkPropertyMetadata(null, OnMarksChanged));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(TimelineTrack), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(
        nameof(SelectionBrush), typeof(Brush), typeof(TimelineTrack), new FrameworkPropertyMetadata(Brushes.DodgerBlue, OnSelectionChanged));
    public static readonly DependencyProperty SelectionStartProperty = DependencyProperty.Register(
        nameof(SelectionStart), typeof(double), typeof(TimelineTrack), new FrameworkPropertyMetadata(0d, OnSelectionChanged));
    public static readonly DependencyProperty SelectionEndProperty = DependencyProperty.Register(
        nameof(SelectionEnd), typeof(double), typeof(TimelineTrack), new FrameworkPropertyMetadata(0d, OnSelectionChanged));
    public static readonly DependencyProperty SelectPositionCommandProperty = DependencyProperty.Register(
        nameof(SelectPositionCommand), typeof(ICommand), typeof(TimelineTrack));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value);
    }
    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value);
    }
    public Brush SelectionBrush
    {
        get => (Brush)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value);
    }
    public double SelectionStart
    {
        get => (double)GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value);
    }
    public double SelectionEnd
    {
        get => (double)GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value);
    }
    public ICommand? SelectPositionCommand
    {
        get => (ICommand?)GetValue(SelectPositionCommandProperty); set => SetValue(SelectPositionCommandProperty, value);
    }

    public TimelineTrack()
    {
        Cursor = Cursors.Hand;
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in Children)
        {
            child.Measure(availableSize);
        }

        return new Size(0, double.IsNaN(Height) ? 20 : Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var index = 0; index < _marks.Length; index++)
        {
            var start = Math.Clamp(_marks[index].Start, 0, 1);
            var end = Math.Clamp(_marks[index].End, start, 1);
            Children[index].Arrange(new Rect(start * finalSize.Width, 0, (end - start) * finalSize.Width, finalSize.Height));
        }
        if (_selection is not null)
        {
            var start = Math.Clamp(SelectionStart, 0, 1);
            var end = Math.Clamp(SelectionEnd, start, 1);
            _selection.Arrange(new Rect(start * finalSize.Width, 0, (end - start) * finalSize.Width, finalSize.Height));
        }
        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRoundedRectangle(TrackBrush, null, new Rect(RenderSize), 2, 2);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonDown(args);
        if (ActualWidth <= 0)
        {
            return;
        }

        var fraction = Math.Clamp(args.GetPosition(this).X / ActualWidth, 0, 1);
        if (SelectPositionCommand?.CanExecute(fraction) != true)
        {
            return;
        }

        SelectPositionCommand.Execute(fraction);
        args.Handled = true;
    }

    private static void OnMarksChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var track = (TimelineTrack)sender;
        track.Children.Clear();
        track._selection = null;
        track._marks = (track.ItemsSource?.Cast<TimelineMark>() ?? []).ToArray();
        foreach (var mark in track._marks)
        {
            if (!track._brushes.TryGetValue(mark.Color, out var brush))
            {
                brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(mark.Color));
                brush.Freeze();
                track._brushes[mark.Color] = brush;
            }
            var rectangle = new Rectangle { Fill = brush, ToolTip = mark.Description, RadiusX = 1, RadiusY = 1 };
            ToolTipService.SetInitialShowDelay(rectangle, 250);
            ToolTipService.SetBetweenShowDelay(rectangle, 0);
            track.Children.Add(rectangle);
        }
        track.UpdateSelection();
        track.InvalidateMeasure();
    }

    private static void OnSelectionChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) => ((TimelineTrack)sender).UpdateSelection();

    private void UpdateSelection()
    {
        if (_selection is not null)
        {
            Children.Remove(_selection);
        }

        _selection = null;
        if (SelectionEnd > SelectionStart)
        {
            _selection = new Rectangle { Stroke = SelectionBrush, StrokeThickness = 2, Fill = Brushes.Transparent, IsHitTestVisible = false };
            Children.Add(_selection);
        }
        InvalidateArrange();
    }
}
