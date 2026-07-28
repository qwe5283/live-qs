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
    private long _requestVersion;
    [ObservableProperty]
    private DateTime? _selectedDate;
    [ObservableProperty]
    private IReadOnlyList<TimelineRow> _rows = Array.Empty<TimelineRow>();
    [ObservableProperty]
    private bool _isEmpty = true;
    [ObservableProperty]
    private bool _isLoading;
    [ObservableProperty]
    private bool _isLoadingMore;
    [ObservableProperty]
    private bool _hasMore;
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
        try
        {
            var result = await LoadPageInBackgroundAsync(_currentRange, null, cancellation.Token);
            if (version != Volatile.Read(ref _requestVersion) || cancellation.IsCancellationRequested) return;

            Rows = result.Rows;
            _nextCursor = result.NextCursor;
            HasMore = result.HasMore;
            UpdateSummary();
            LogPage(date, result, false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (version == Volatile.Read(ref _requestVersion)) IsLoading = false;
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
            return;

        var version = Volatile.Read(ref _requestVersion);
        IsLoadingMore = true;
        try
        {
            var result = await LoadPageInBackgroundAsync(_currentRange, cursor, cancellation.Token);
            if (version != Volatile.Read(ref _requestVersion) || cancellation.IsCancellationRequested) return;

            Rows = Rows.Concat(result.Rows).ToArray();
            _nextCursor = result.NextCursor;
            HasMore = result.HasMore;
            UpdateSummary();
            LogPage((SelectedDate ?? Today).Date, result, true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (version == Volatile.Read(ref _requestVersion)) IsLoadingMore = false;
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
            var rows = page.Items.Select(MapRow).ToArray();
            mappingTimer.Stop();
            return new TimelinePageResult(
                rows,
                page.NextCursor,
                page.HasMore,
                queryTimer.Elapsed,
                mappingTimer.Elapsed);
        }, cancellationToken);

    private static TimelineRow MapRow(ActivitySegment segment)
    {
        var start = segment.StartedAt.ToLocalTime();
        var end = segment.EndedAt.ToLocalTime();
        var title = string.IsNullOrWhiteSpace(segment.WindowTitle) ? segment.AppId : segment.WindowTitle;
        var flags = string.Join(" · ", new[]
        {
            segment.IsAfk ? "AFK" : "活跃",
            segment.IsAudioPlaying ? "音频播放" : "",
            segment.IsFullscreen ? "全屏" : "",
            segment.Category,
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new TimelineRow(
            segment.Id, start.ToString("HH:mm"), $"{start:HH:mm:ss} - {end:HH:mm:ss}",
            segment.AppName, title, DurationText.Format(segment.Duration), flags,
            segment.Color, segment.IsAfk);
    }

    private void UpdateSummary()
    {
        IsEmpty = Rows.Count == 0;
        Summary = IsEmpty
            ? "暂无活动"
            : HasMore ? $"已加载 {Rows.Count} 个时间段" : $"{Rows.Count} 个时间段";
    }

    private void LogPage(DateTime date, TimelinePageResult result, bool append) =>
        _logger.LogInformation(
            "Timeline page loaded for {Date}: {Count} rows, append={Append}, query={QueryMilliseconds} ms, mapping={MappingMilliseconds} ms, hasMore={HasMore}.",
            date.ToString("yyyy-MM-dd"),
            result.Rows.Count,
            append,
            Math.Round(result.QueryDuration.TotalMilliseconds, 1),
            Math.Round(result.MappingDuration.TotalMilliseconds, 1),
            result.HasMore);

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Timeline operation failed.");
            _dialogs.ShowError("查询失败", exception);
        }
    }

    private sealed record TimelinePageResult(
        IReadOnlyList<TimelineRow> Rows,
        TimelineCursor? NextCursor,
        bool HasMore,
        TimeSpan QueryDuration,
        TimeSpan MappingDuration);
}

public sealed record TimelineRow(
    long Id,
    string Time,
    string TimeRange,
    string AppName,
    string Detail,
    string Duration,
    string Flags,
    string Color,
    bool IsAfk);
