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

    private static TimelineViewModel Model(IActivityQueryService query) => new(
        query, new Dialogs(), NullLogger<TimelineViewModel>.Instance, TimeProvider.System);

    private sealed class Query(ActivitySegment[] records) : IActivityQueryService
    {
        public Task<TimelinePage> GetTimelinePageAsync(DateRange range, int pageSize, TimelineCursor? cursor = null, CancellationToken cancellationToken = default)
        {
            var ordered = records.Where(record => record.StartedAt < range.End && record.EndedAt > range.Start)
                .OrderByDescending(record => record.StartedAt).ThenByDescending(record => record.Id)
                .Where(record => cursor is null || record.StartedAt < cursor.Value.StartedAt || record.StartedAt == cursor.Value.StartedAt && record.Id < cursor.Value.Id)
                .ToArray();
            var items = ordered.Take(pageSize).ToArray();
            var more = ordered.Length > pageSize;
            return Task.FromResult(new TimelinePage(items, more ? new TimelineCursor(items[^1].StartedAt, items[^1].Id) : null, more));
        }

        public Task<IReadOnlyList<ActivitySegment>> GetTimelineAsync(DateRange range, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DashboardSnapshot> GetDashboardAsync(DateRange range, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ApplicationRule>> GetApplicationRulesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DelayedQuery : IActivityQueryService
    {
        private int _calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TimelinePage> GetTimelinePageAsync(DateRange range, int pageSize, TimelineCursor? cursor = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) != 1) return new TimelinePage([], null, false);
            Started.SetResult();
            await Complete.Task;
            return new TimelinePage([new ActivitySegment(1, range.Start, range.Start.AddMinutes(1), "old", "Old", "", false, false, false, "", "#007AFF")], null, false);
        }

        public Task<IReadOnlyList<ActivitySegment>> GetTimelineAsync(DateRange range, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DashboardSnapshot> GetDashboardAsync(DateRange range, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ApplicationRule>> GetApplicationRulesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Dialogs : IUserDialogService
    {
        public void ShowError(string title, Exception exception) => throw new InvalidOperationException(title, exception);
        public bool ConfirmDeleteRange() => false;
        public string? SelectExportPath(string defaultFileName) => null;
    }
}
