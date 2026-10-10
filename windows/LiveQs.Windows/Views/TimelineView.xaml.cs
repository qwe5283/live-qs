using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LiveQs.Windows.ViewModels;

namespace LiveQs.Windows.Views;

public partial class TimelineView : System.Windows.Controls.UserControl
{
    public TimelineViewModel ViewModel
    {
        get;
    }
    private ScrollViewer? _scrollViewer;
    private double? _savedOffset;
    private DateTime? _savedDate;
    private bool _paginationCheckScheduled;
    private int _queryVersion;

    public TimelineView(TimelineViewModel viewModel)
    {
        InitializeComponent();
        DataContext = ViewModel = viewModel;
        Loaded += (_, _) =>
        {
            _scrollViewer = FindScrollViewer(TimelineList);
            SchedulePaginationCheck();
        };
        SizeChanged += (_, _) => SchedulePaginationCheck();
        IsVisibleChanged += (_, _) => SchedulePaginationCheck();
        ViewModel.LoadMoreCommand.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModel.LoadMoreCommand.IsRunning))
            {
                SchedulePaginationCheck();
            }
        };
        ViewModel.PropertyChanging += (_, args) =>
        {
            if (args.PropertyName == nameof(TimelineViewModel.IsLoadingMore) && !ViewModel.IsLoadingMore)
            {
                _savedOffset = _scrollViewer?.VerticalOffset;
                _savedDate = ViewModel.SelectedDate;
            }
        };
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TimelineViewModel.IsLoading))
            {
                if (ViewModel.IsLoading)
                {
                    _queryVersion++;
                    _savedOffset = null;
                    _savedDate = null;
                    _scrollViewer?.ScrollToTop();
                }
                else
                {
                    var version = _queryVersion;
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                    {
                        if (version == _queryVersion && !ViewModel.IsLoading)
                        {
                            TimelineList.UpdateLayout();
                            _scrollViewer ??= FindScrollViewer(TimelineList);
                            _scrollViewer?.ScrollToTop();
                        }
                    });
                }
            }

            if (args.PropertyName is nameof(TimelineViewModel.Rows) or nameof(TimelineViewModel.IsLoading)
                or nameof(TimelineViewModel.IsLoadingMore) or nameof(TimelineViewModel.HasMore))
            {
                SchedulePaginationCheck();
            }

            if (args.PropertyName != nameof(TimelineViewModel.IsLoadingMore) || ViewModel.IsLoadingMore
                || _savedOffset is not { } offset)
            {
                return;
            }

            _savedOffset = null;
            var date = _savedDate;
            var queryVersion = _queryVersion;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (queryVersion == _queryVersion && ViewModel.SelectedDate == date && !ViewModel.IsLoading)
                {
                    TimelineList.UpdateLayout();
                    _scrollViewer?.ScrollToVerticalOffset(offset);
                }
            });
        };
    }

    public Task RefreshAsync() => ViewModel.RefreshAsync();

    private void OnTimelineScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        SchedulePaginationCheck();
    }

    private void SchedulePaginationCheck()
    {
        if (!IsLoaded || _paginationCheckScheduled)
        {
            return;
        }

        _paginationCheckScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _paginationCheckScheduled = false;
            _scrollViewer ??= FindScrollViewer(TimelineList);
            if (!IsVisible || _scrollViewer is not { ViewportHeight: > 0 } viewer
                || !ViewModel.CanLoadMore || ViewModel.HasLoadMoreError || ViewModel.LoadMoreCommand.IsRunning)
            {
                return;
            }

            if (viewer.VerticalOffset >= viewer.ScrollableHeight - 240)
            {
                ViewModel.LoadMoreCommand.Execute(null);
            }
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }
}
