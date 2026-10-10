using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Services;
using LiveQs.Windows.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveQs.Windows.Tests;

public sealed class TimelineViewModelTests
{
    [Fact]
    public async Task FailedDateQuery_DoesNotPresentThePreviousDatesRecords()
    {
        var date = new DateTime(2026, 10, 9);
        var range = DateRange.FromLocalDates(date, date);
        var query = new FailingQuery(new ActivitySegment(
            1, range.Start, range.Start.AddMinutes(1), "app", "Application", "", false, false, false, "", "#007AFF"));
        var dialogs = new RecordingDialogs();
        var model = new TimelineViewModel(query, dialogs, NullLogger<TimelineViewModel>.Instance, new TestClock());
        model.SelectedDate = date;
        await model.RefreshAsync();
        Assert.Single(model.Rows);

        query.Fail = true;
        model.SelectedDate = date.AddDays(1);
        await model.LoadCommand.ExecuteAsync(null);

        Assert.Empty(model.Rows);
        Assert.False(model.IsLoading);
        Assert.True(model.HasQueryError);
        Assert.Equal(1, dialogs.ErrorCount);
    }

    [Fact]
    public async Task LoadingEarlierRecords_MergesThePartialEpisodeAndRetainsItsExpandedSelection()
    {
        var start = new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);
        var records = Enumerable.Range(0, 600).Select(index => new ActivitySegment(
            index + 1, start.AddSeconds(index * 5), start.AddSeconds((index + 1) * 5),
            index % 2 == 0 ? "code" : "edge", "Application", $"title-{index}", false, false, false, "未分类", "#007AFF")).ToArray();
        var model = Model(new Query(records));
        model.SelectedDate = start.ToLocalTime().Date;
        await model.RefreshAsync();
        var row = Assert.Single(model.Rows);
        Assert.True(model.HasMore);
        Assert.True(row.IsPartial);
        Assert.Equal(500, row.Data.Slices.Count);
        row.SelectPositionCommand.Execute(0.9d);
        var selectedTime = row.SelectedWindow!.StartedAt;

        await model.LoadMoreCommand.ExecuteAsync(null);

        var complete = Assert.Single(model.Rows);
        Assert.False(model.HasMore);
        Assert.False(complete.IsPartial);
        Assert.Equal(600, complete.Data.Slices.Count);
        Assert.True(complete.IsExpanded);
        Assert.True(complete.IsRawExpanded);
        Assert.True(complete.SelectedWindow!.StartedAt <= selectedTime && complete.SelectedWindow.EndedAt > selectedTime);
        Assert.Contains("600 条原始记录", model.Summary);
    }

    [Fact]
    public async Task ChangingDate_DiscardsAnOlderQueryThatCompletesLate()
    {
        var query = new DelayedQuery();
        var model = Model(query);
        model.SelectedDate = new DateTime(2026, 10, 8);
        var earlierLoad = model.RefreshAsync();
        await query.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.SelectedDate = new DateTime(2026, 10, 9);
        await model.RefreshAsync();
        query.Complete.SetResult();
        await earlierLoad;

        Assert.True(model.IsEmpty);
        Assert.Empty(model.Rows);
        Assert.False(model.IsLoading);
    }

    [Fact]
    public async Task FailedPagination_RetainsRecordsAndExpandedSelectionThenRetriesTheSamePage()
    {
        var records = DenseRecords();
        var attempts = 0;
        TimelineCursor? failedCursor = null;
        var query = new Query(records)
        {
            BeforePage = cursor =>
            {
                if (cursor is not null && ++attempts == 1)
                {
                    failedCursor = cursor;
                    throw new IOException("Page unavailable.");
                }
                if (cursor is not null)
                {
                    Assert.Equal(failedCursor, cursor);
                }
                return Task.CompletedTask;
            }
        };
        var dialogs = new RecordingDialogs();
        var model = new TimelineViewModel(query, dialogs, NullLogger<TimelineViewModel>.Instance, new TestClock());
        model.SelectedDate = records[0].StartedAt.ToLocalTime().Date;
        await model.RefreshAsync();
        var original = Assert.Single(model.Rows);
        original.SelectPositionCommand.Execute(0.9d);
        var selectedTime = original.SelectedWindow!.StartedAt;

        await model.LoadMoreCommand.ExecuteAsync(null);

        Assert.Same(original, Assert.Single(model.Rows));
        Assert.True(model.HasLoadMoreError);
        Assert.True(model.CanLoadMore);
        Assert.False(model.IsLoadingMore);
        Assert.Equal(0, dialogs.ErrorCount);

        await model.LoadMoreCommand.ExecuteAsync(null);

        var complete = Assert.Single(model.Rows);
        Assert.Equal(600, complete.Data.Slices.Count);
        Assert.True(complete.IsExpanded);
        Assert.True(complete.IsRawExpanded);
        Assert.True(complete.SelectedWindow!.StartedAt <= selectedTime && complete.SelectedWindow.EndedAt > selectedTime);
        Assert.False(model.HasLoadMoreError);
        Assert.False(model.HasMore);
        Assert.False(model.CanLoadMore);
    }

    [Fact]
    public async Task ChangingDate_DiscardsALatePaginationFailure()
    {
        var records = DenseRecords();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new Query(records)
        {
            BeforePage = async cursor =>
            {
                if (cursor is null)
                {
                    return;
                }
                started.SetResult();
                await finish.Task;
                throw new IOException("Old page unavailable.");
            }
        };
        var model = Model(query);
        model.SelectedDate = records[0].StartedAt.ToLocalTime().Date;
        await model.RefreshAsync();
        var earlierPage = model.LoadMoreCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        model.SelectedDate = model.SelectedDate!.Value.AddDays(1);
        await model.RefreshAsync();
        finish.SetResult();
        await earlierPage;

        Assert.True(model.IsEmpty);
        Assert.Empty(model.Rows);
        Assert.False(model.HasLoadMoreError);
        Assert.False(model.IsLoadingMore);
        Assert.False(model.CanLoadMore);
    }

    [Fact]
    public async Task RepeatedPaginationRequests_OnlyLoadOnePageAtATime()
    {
        var records = DenseRecords();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pageCalls = 0;
        var query = new Query(records)
        {
            BeforePage = async cursor =>
            {
                if (cursor is null)
                {
                    return;
                }
                Interlocked.Increment(ref pageCalls);
                started.TrySetResult();
                await finish.Task;
            }
        };
        var model = Model(query);
        model.SelectedDate = records[0].StartedAt.ToLocalTime().Date;
        await model.RefreshAsync();
        var firstPage = model.LoadMoreCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await model.LoadMoreCommand.ExecuteAsync(null);

        Assert.True(model.IsLoadingMore);
        Assert.False(model.CanLoadMore);
        Assert.Equal(1, pageCalls);
        Assert.Equal(500, Assert.Single(model.Rows).Data.Slices.Count);
        finish.SetResult();
        await firstPage;

        Assert.Equal(600, Assert.Single(model.Rows).Data.Slices.Count);
        Assert.False(model.IsLoadingMore);
        Assert.False(model.HasMore);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EvidenceOnlyEpisode_KeepsExpandedInspectionAfterLoadingEarlierRecords(bool hasPairedState)
    {
        var records = DenseRecords();
        var start = records[^1].EndedAt.AddMinutes(5);
        var query = new Query(records)
        {
            StateEvents = hasPairedState ?
            [
                new DeviceStateEvent("sleep", start, DeviceStateKind.Sleep, true, "system:test", "现代待机"),
                new DeviceStateEvent("wake", start.AddMinutes(10), DeviceStateKind.Sleep, false, "system:test", "现代待机")
            ] :
            [
                new DeviceStateEvent("wake", start.AddMinutes(10), DeviceStateKind.Sleep, false, "system:test", "现代待机")
            ]
        };
        var model = Model(query);
        model.SelectedDate = records[0].StartedAt.ToLocalTime().Date;
        await model.RefreshAsync();
        var original = Assert.Single(model.Rows, row => row.Data.IsAfk);
        original.SelectPositionCommand.Execute(0.5d);
        Assert.Empty(original.Data.Slices);

        await model.LoadMoreCommand.ExecuteAsync(null);

        var updated = Assert.Single(model.Rows, row => row.Data.IsAfk);
        Assert.True(updated.IsExpanded);
        Assert.True(updated.IsRawExpanded);
        Assert.Equal(original.SelectedWindow, updated.SelectedWindow);
        Assert.Single(updated.StateEvidenceRows);
    }

    private static ActivitySegment[] DenseRecords()
    {
        var start = new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, 600).Select(index => new ActivitySegment(
            index + 1, start.AddSeconds(index * 5), start.AddSeconds((index + 1) * 5),
            index % 2 == 0 ? "code" : "edge", "Application", $"title-{index}", false, false, false, "未分类", "#007AFF")).ToArray();
    }

    private static TimelineViewModel Model(IActivityQueryService query) => new(
        query, new Dialogs(), NullLogger<TimelineViewModel>.Instance, new TestClock());

    private sealed class Query(ActivitySegment[] records) : IActivityQueryService
    {
        public Func<TimelineCursor?, Task>? BeforePage
        {
            get; init;
        }
        public IReadOnlyList<DeviceStateEvent> StateEvents { get; init; } = [];

        public async Task<TimelinePage> GetTimelinePageAsync(
            DateRange range,
            int pageSize,
            TimelineCursor? cursor = null,
            CancellationToken cancellationToken = default)
        {
            if (BeforePage is not null)
            {
                await BeforePage(cursor);
            }
            var ordered = records.Where(record => record.StartedAt < range.End && record.EndedAt > range.Start)
                .OrderByDescending(record => record.StartedAt).ThenByDescending(record => record.Id)
                .Where(record => cursor is null || record.StartedAt < cursor.Value.StartedAt || record.StartedAt == cursor.Value.StartedAt && record.Id < cursor.Value.Id)
                .ToArray();
            var items = ordered.Take(pageSize).ToArray();
            var more = ordered.Length > pageSize;
            return new TimelinePage(items, more ? new TimelineCursor(items[^1].StartedAt, items[^1].Id) : null, more)
            {
                StateEvents = StateEvents
            };
        }

        public Task<IReadOnlyList<ActivitySegment>> GetTimelineAsync(
            DateRange range,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DashboardSnapshot> GetDashboardAsync(
            DateRange range,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ApplicationRule>> GetApplicationRulesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DelayedQuery : IActivityQueryService
    {
        private int _calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TimelinePage> GetTimelinePageAsync(
            DateRange range,
            int pageSize,
            TimelineCursor? cursor = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) != 1)
            {
                return new TimelinePage([], null, false);
            }

            Started.SetResult();
            await Complete.Task;
            return new TimelinePage([new ActivitySegment(1, range.Start, range.Start.AddMinutes(1), "old", "Old", "", false, false, false, "", "#007AFF")], null, false);
        }

        public Task<IReadOnlyList<ActivitySegment>> GetTimelineAsync(
            DateRange range,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DashboardSnapshot> GetDashboardAsync(
            DateRange range,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ApplicationRule>> GetApplicationRulesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Dialogs : IUserDialogService
    {
        public void ShowError(
            string title,
            Exception exception) => throw new InvalidOperationException(title, exception);
        public bool ConfirmDeleteRange() => false;
        public string? SelectExportPath(string defaultFileName) => null;
    }

    private sealed class RecordingDialogs : IUserDialogService
    {
        public int ErrorCount
        {
            get; private set;
        }
        public void ShowError(string title, Exception exception) => ErrorCount++;
        public bool ConfirmDeleteRange() => false;
        public string? SelectExportPath(string defaultFileName) => null;
    }

    private sealed class FailingQuery(ActivitySegment record) : IActivityQueryService
    {
        public bool Fail
        {
            get; set;
        }
        public Task<TimelinePage> GetTimelinePageAsync(
            DateRange range,
            int pageSize,
            TimelineCursor? cursor = null,
            CancellationToken cancellationToken = default) =>
            Fail ? throw new IOException("Query failed.") : Task.FromResult(new TimelinePage([record], null, false));

        public Task<IReadOnlyList<ActivitySegment>> GetTimelineAsync(
            DateRange range,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DashboardSnapshot> GetDashboardAsync(
            DateRange range,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ApplicationRule>> GetApplicationRulesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
