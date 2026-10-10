using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Services;
using Microsoft.Extensions.Logging;

namespace LiveQs.Windows.ViewModels;

public sealed partial class TimelineViewModel : ViewModelBase
{
    private const int PageSize = 500;
    private readonly IActivityQueryService _queryService;
    private readonly IUserDialogService _dialogs;
    private readonly ILogger<TimelineViewModel> _logger;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _loadCancellation;
    private DateRange _currentRange;
    private TimelineCursor? _nextCursor;
    private IReadOnlyList<ActivitySegment> _segments = [];
    private long _requestVersion;
    [ObservableProperty]
    private DateTime? _selectedDate;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListItems))]
    private IReadOnlyList<TimelineEpisodeViewModel> _rows = [];
    [ObservableProperty]
    private bool _isEmpty = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    private bool _isLoading;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    private bool _isLoadingMore;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    private bool _hasMore;
    [ObservableProperty]
    private bool _hasLoadMoreError;
    [ObservableProperty]
    private bool _hasQueryError;
    [ObservableProperty]
    private string _summary = "暂无活动";

    public TimelineViewModel(
        IActivityQueryService queryService,
        IUserDialogService dialogs,
        ILogger<TimelineViewModel> logger,
        TimeProvider timeProvider)
    {
        _queryService = queryService;
        _dialogs = dialogs;
        _logger = logger;
        _timeProvider = timeProvider;
        _selectedDate = Today;
    }

    public IEnumerable<object> ListItems => Rows.Count == 0 ? [] : Rows.Cast<object>().Append(this);
    public bool CanLoadMore => HasMore && !IsLoading && !IsLoadingMore;

    public async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref _requestVersion);
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _loadCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        var date = (SelectedDate ?? Today).Date;
        _currentRange = DateRange.FromLocalDates(date, date);
        _nextCursor = null;
        IsLoading = true;
        IsLoadingMore = false;
        HasMore = false;
        HasLoadMoreError = false;
        HasQueryError = false;
        Rows = [];
        _segments = [];
        IsEmpty = false;
        Summary = "正在加载活动…";
        try
        {
            var result = await LoadPageInBackgroundAsync(_currentRange, null, [], cancellation.Token);
            if (version != Volatile.Read(ref _requestVersion) || cancellation.IsCancellationRequested)
            {
                return;
            }

            Rows = result.Rows;
            _segments = result.Segments;
            _nextCursor = result.NextCursor;
            HasMore = result.HasMore;
            UpdateSummary();
            LogPage(date, result, false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception) when (version == Volatile.Read(ref _requestVersion))
        {
            HasQueryError = true;
            Summary = "查询失败，请重试";
            throw;
        }
        finally
        {
            if (version == Volatile.Read(ref _requestVersion))
            {
                IsLoading = false;
            }
        }
    }

    private async Task MoveAsync(int days)
    {
        SelectedDate = (SelectedDate ?? Today).AddDays(days);
        await RefreshAsync();
    }

    private async Task SelectTodayAsync()
    {
        SelectedDate = Today;
        await RefreshAsync();
    }

    private async Task LoadMorePageAsync()
    {
        if (IsLoading || IsLoadingMore || !HasMore || _nextCursor is not { } cursor || _loadCancellation is not { } cancellation)
        {
            return;
        }

        var version = Volatile.Read(ref _requestVersion);
        HasLoadMoreError = false;
        IsLoadingMore = true;
        try
        {
            var result = await LoadPageInBackgroundAsync(_currentRange, cursor, _segments, cancellation.Token);
            if (version != Volatile.Read(ref _requestVersion) || cancellation.IsCancellationRequested)
            {
                return;
            }

            PreserveExpandedRows(result.Rows);
            Rows = result.Rows;
            _segments = result.Segments;
            _nextCursor = result.NextCursor;
            HasMore = result.HasMore;
            UpdateSummary();
            LogPage((SelectedDate ?? Today).Date, result, true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version == Volatile.Read(ref _requestVersion) && !cancellation.IsCancellationRequested)
            {
                HasLoadMoreError = true;
                _logger.LogWarning(exception, "Loading earlier timeline records failed.");
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _requestVersion))
            {
                IsLoadingMore = false;
            }
        }
    }

    [RelayCommand]
    private Task LoadAsync() => RunAsync(RefreshAsync);

    [RelayCommand]
    private Task LoadMoreAsync() => RunAsync(LoadMorePageAsync);

    [RelayCommand]
    private Task PreviousAsync() => RunAsync(() => MoveAsync(-1));

    [RelayCommand]
    private Task NextAsync() => RunAsync(() => MoveAsync(1));

    [RelayCommand]
    private Task TodayAsync() => RunAsync(SelectTodayAsync);

    private DateTime Today => _timeProvider.GetLocalNow().Date;

    private async Task<TimelinePageResult> LoadPageInBackgroundAsync(
        DateRange range,
        TimelineCursor? cursor,
        IReadOnlyList<ActivitySegment> previous,
        CancellationToken cancellationToken) =>
        await Task.Run(async () =>
        {
            var queryTimer = Stopwatch.StartNew();
            var page = await _queryService
                .GetTimelinePageAsync(range, PageSize, cursor, cancellationToken)
                .ConfigureAwait(false);
            queryTimer.Stop();
            cancellationToken.ThrowIfCancellationRequested();

            var mappingTimer = Stopwatch.StartNew();
            var segments = previous.Concat(page.Items).DistinctBy(segment => segment.Id).ToArray();
            var rows = TimelinePresentation.Build(segments, range, page.HasMore)
                .Select(episode => new TimelineEpisodeViewModel(episode)).ToArray();
            mappingTimer.Stop();
            return new TimelinePageResult(
                rows,
                segments,
                page.Items.Count,
                page.NextCursor,
                page.HasMore,
                queryTimer.Elapsed,
                mappingTimer.Elapsed);
        }, cancellationToken);

    private void PreserveExpandedRows(IReadOnlyList<TimelineEpisodeViewModel> rows)
    {
        var expanded = new Dictionary<long, TimelineEpisodeViewModel>();
        foreach (var row in Rows.Where(row => row.IsExpanded))
        {
            foreach (var slice in row.Data.Slices)
            {
                expanded[slice.Record.Id] = row;
            }
        }
        foreach (var row in rows)
        {
            var original = row.Data.Slices
                .Select(slice => expanded.GetValueOrDefault(slice.Record.Id))
                .FirstOrDefault(value => value is not null);
            if (original is null)
            {
                continue;
            }

            row.IsExpanded = true;
            row.IsRawExpanded = original.IsRawExpanded;
            if (original.SelectedWindow is { } window)
            {
                row.SelectedWindow = row.Windows.FirstOrDefault(candidate =>
                    candidate.StartedAt <= window.StartedAt && candidate.EndedAt > window.StartedAt)
                    ?? row.SelectedWindow;
            }
        }
    }

    private void UpdateSummary()
    {
        IsEmpty = Rows.Count == 0;
        Summary = IsEmpty
            ? "暂无活动"
            : $"{(HasMore ? "已加载 " : "")}{Rows.Count(row => !row.Data.IsGap)} 个活动段落 · {_segments.Count} 条原始记录";
    }

    private void LogPage(DateTime date, TimelinePageResult result, bool append) =>
        _logger.LogInformation(
            "Timeline page loaded for {Date}: {Count} rows, append={Append}, " +
            "query={QueryMilliseconds} ms, mapping={MappingMilliseconds} ms, hasMore={HasMore}.",
            date.ToString("yyyy-MM-dd"),
            result.PageCount,
            append,
            Math.Round(result.QueryDuration.TotalMilliseconds, 1),
            Math.Round(result.MappingDuration.TotalMilliseconds, 1),
            result.HasMore);

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Timeline operation failed.");
            _dialogs.ShowError("查询失败", exception);
        }
    }

    private sealed record TimelinePageResult(
        IReadOnlyList<TimelineEpisodeViewModel> Rows,
        IReadOnlyList<ActivitySegment> Segments,
        int PageCount,
        TimelineCursor? NextCursor,
        bool HasMore,
        TimeSpan QueryDuration,
        TimeSpan MappingDuration);
}
