using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LiveQs.Windows.Formatting;

namespace LiveQs.Windows.Controls;

public partial class DateRangePicker : UserControl
{
    public static readonly DependencyProperty StartDateProperty = DependencyProperty.Register(
        nameof(StartDate), typeof(DateTime?), typeof(DateRangePicker), new PropertyMetadata(null, OnRangeChanged));
    public static readonly DependencyProperty EndDateProperty = DependencyProperty.Register(
        nameof(EndDate), typeof(DateTime?), typeof(DateRangePicker), new PropertyMetadata(null, OnRangeChanged));
    private static readonly DependencyPropertyKey _displayTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayText), typeof(string), typeof(DateRangePicker), new PropertyMetadata("选择日期范围"));
    public static readonly DependencyProperty DisplayTextProperty = _displayTextPropertyKey.DependencyProperty;
    private DateTime? _anchor;
    private DateTime? _previewEnd;
    private DateTime _keyboardDate;
    private bool _returnFocus;

    public DateRangePicker()
    {
        InitializeComponent();
        // WPF-UI 4.3.0's SymbolIcon incorrectly encodes code points above U+FFFF.
        CalendarIcon.Glyph = char.ConvertFromUtf32((int)Wpf.Ui.Controls.SymbolRegular.Calendar24);
        Unloaded += (_, _) => RangePopup.IsOpen = false;
    }

    public DateTime? StartDate
    {
        get => (DateTime?)GetValue(StartDateProperty);
        set => SetValue(StartDateProperty, value);
    }

    public DateTime? EndDate
    {
        get => (DateTime?)GetValue(EndDateProperty);
        set => SetValue(EndDateProperty, value);
    }

    public string DisplayText => (string)GetValue(DisplayTextProperty);
    public event EventHandler<DateRangeSelectedEventArgs>? RangeSelected;

    private static void OnRangeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var picker = (DateRangePicker)sender;
        picker.SetValue(_displayTextPropertyKey, DateRangeText.Format(picker.StartDate, picker.EndDate, DateTime.Today.Year));
        if (picker.RangePopup is not null)
        {
            picker.RangePopup.IsOpen = false;
        }
    }

    private void OnPickerClick(object sender, RoutedEventArgs args) => RangePopup.IsOpen = !RangePopup.IsOpen;

    private void OnPopupOpened(object? sender, EventArgs args)
    {
        _anchor = null;
        _previewEnd = null;
        _returnFocus = false;
        _keyboardDate = (StartDate ?? DateTime.Today).Date;
        RangeCalendar.DisplayMode = CalendarMode.Month;
        RangeCalendar.DisplayDate = _keyboardDate;
        ShowSelection(_keyboardDate, (EndDate ?? _keyboardDate).Date);
        SelectionHint.Text = "选择开始日期";
        Dispatcher.BeginInvoke(FocusKeyboardDate, DispatcherPriority.Input);
    }

    private void OnPopupClosed(object? sender, EventArgs args)
    {
        _anchor = null;
        _previewEnd = null;
        if (_returnFocus)
        {
            PickerButton.Focus();
        }
    }

    private void OnCalendarMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (FindDay(args.OriginalSource as DependencyObject) is not { DataContext: DateTime date } day || !day.IsEnabled)
        {
            return;
        }

        args.Handled = true;
        day.Focus();
        SelectDate(date.Date);
    }

    private void OnCalendarMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (FindDay(args.OriginalSource as DependencyObject) is not null)
        {
            args.Handled = true;
        }
    }

    private void OnCalendarMouseMove(object sender, MouseEventArgs args)
    {
        if (_anchor is not { } anchor ||
            FindDay(args.OriginalSource as DependencyObject) is not { DataContext: DateTime end } day ||
            !day.IsEnabled || _previewEnd == end.Date)
        {
            return;
        }

        _previewEnd = end.Date;
        ShowSelection(anchor, end.Date);
    }

    private void SelectDate(DateTime date)
    {
        _keyboardDate = date;
        if (_anchor is not { } anchor)
        {
            _anchor = date;
            _previewEnd = date;
            ShowSelection(date, date);
            SelectionHint.Text = $"{date:M月d日} 起 · 选择结束日期";
            if (RangeCalendar.DisplayDate.Month != date.Month || RangeCalendar.DisplayDate.Year != date.Year)
            {
                RangeCalendar.DisplayDate = date;
            }

            return;
        }

        var start = anchor <= date ? anchor : date;
        var end = anchor <= date ? date : anchor;
        _returnFocus = true;
        RangePopup.IsOpen = false;
        RangeSelected?.Invoke(this, new DateRangeSelectedEventArgs(start, end));
    }

    private void ShowSelection(DateTime start, DateTime end)
    {
        RangeCalendar.SelectedDates.Clear();
        RangeCalendar.SelectedDates.AddRange(start <= end ? start : end, start <= end ? end : start);
    }

    private void OnPopupKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape)
        {
            return;
        }

        args.Handled = true;
        _returnFocus = true;
        RangePopup.IsOpen = false;
    }

    private void OnCalendarKeyDown(object sender, KeyEventArgs args)
    {
        if (RangeCalendar.DisplayMode != CalendarMode.Month ||
            FindDay(Keyboard.FocusedElement as DependencyObject) is not { DataContext: DateTime focused })
        {
            return;
        }

        if (args.Key is Key.Enter or Key.Space)
        {
            args.Handled = true;
            SelectDate(focused.Date);
            return;
        }

        var dayOfWeek = ((int)focused.DayOfWeek + 6) % 7;
        DateTime? next;
        try
        {
            next = args.Key switch
            {
                Key.Left => focused.AddDays(RangeCalendar.FlowDirection == FlowDirection.RightToLeft ? 1 : -1),
                Key.Right => focused.AddDays(RangeCalendar.FlowDirection == FlowDirection.RightToLeft ? -1 : 1),
                Key.Up => focused.AddDays(-7),
                Key.Down => focused.AddDays(7),
                Key.Home => focused.AddDays(-dayOfWeek),
                Key.End => focused.AddDays(6 - dayOfWeek),
                Key.PageUp => focused.AddMonths(-1),
                Key.PageDown => focused.AddMonths(1),
                _ => null,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            args.Handled = true;
            return;
        }
        if (next is not { } date)
        {
            return;
        }

        args.Handled = true;
        _keyboardDate = date.Date;
        RangeCalendar.DisplayDate = _keyboardDate;
        if (_anchor is { } anchor)
        {
            ShowSelection(anchor, _keyboardDate);
        }

        Dispatcher.BeginInvoke(FocusKeyboardDate, DispatcherPriority.Input);
    }

    private void FocusKeyboardDate()
    {
        if (!RangePopup.IsOpen)
        {
            return;
        }

        RangeCalendar.UpdateLayout();
        FindDays(RangeCalendar).FirstOrDefault(day => day.DataContext is DateTime date && date.Date == _keyboardDate)?.Focus();
    }

    private static CalendarDayButton? FindDay(DependencyObject? child)
    {
        while (child is not null)
        {
            if (child is CalendarDayButton day)
            {
                return day;
            }

            child = child is Visual ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
        }
        return null;
    }

    private static IEnumerable<CalendarDayButton> FindDays(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is CalendarDayButton day)
            {
                yield return day;
            }

            foreach (var descendant in FindDays(child))
            {
                yield return descendant;
            }
        }
    }
}
