using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LiveQs.Windows.ViewModels;

namespace LiveQs.Windows.Views;

public partial class TimelineView : System.Windows.Controls.UserControl
{
    public TimelineViewModel ViewModel { get; }
    private ScrollViewer? _scrollViewer;
    private double? _savedOffset;

    public TimelineView(TimelineViewModel viewModel)
    {
        InitializeComponent();
        DataContext = ViewModel = viewModel;
        Loaded += (_, _) => _scrollViewer = FindScrollViewer(TimelineList);
        ViewModel.PropertyChanging += (_, args) =>
        {
            if (args.PropertyName == nameof(TimelineViewModel.Rows) && ViewModel.IsLoadingMore)
                _savedOffset = _scrollViewer?.VerticalOffset;
        };
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(TimelineViewModel.Rows) || _savedOffset is not { } offset) return;
            _savedOffset = null;
            var date = ViewModel.SelectedDate;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (ViewModel.SelectedDate == date) _scrollViewer?.ScrollToVerticalOffset(offset);
            });
        };
    }

    public Task RefreshAsync() => ViewModel.RefreshAsync();

    private void TimelineList_ScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (args.ExtentHeight <= args.ViewportHeight || args.VerticalOffset < args.ExtentHeight - args.ViewportHeight - 240)
            return;
        ViewModel.LoadMoreCommand.Execute(null);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
}
