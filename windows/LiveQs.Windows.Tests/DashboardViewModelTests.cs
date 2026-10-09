using System.Collections.Concurrent;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Services;
using LiveQs.Windows.ViewModels;

namespace LiveQs.Windows.Tests;

public sealed class DashboardViewModelTests
{
    private static readonly DateTime Today = new(2026, 10, 10);

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    public async Task PresetSelection_QueriesTheInclusiveLocalRangeAndShowsItsSelection(int days)
    {
        var query = new Query();
        var model = Model(query);
        var command = days switch { 1 => model.TodayCommand, 7 => model.WeekCommand, _ => model.MonthCommand };

        await command.ExecuteAsync(null);

        Assert.Equal(DateRange.FromLocalDates(Today.AddDays(1 - days), Today), Assert.Single(query.Ranges));
        Assert.Equal(days, model.PresetDays);
        Assert.Equal(days == 1, model.IsHourlyDistribution);
        Assert.True(model.HasSnapshot);
        Assert.False(model.IsLoading);
    }

    [Theory]
    [InlineData(2025, 12, 30, 2026, 1, 2)]
    [InlineData(2026, 10, 8, 2026, 10, 8)]
    [InlineData(2026, 10, 8, 2026, 9, 30)]
    public async Task CustomRange_CommitsBothDatesInOneQueryAndClearsPresetSelection(
        int startYear, int startMonth, int startDay, int endYear, int endMonth, int endDay)
    {
        var query = new Query();
        var model = Model(query);
        var start = new DateTime(startYear, startMonth, startDay);
        var end = new DateTime(endYear, endMonth, endDay);

        await model.SelectRangeAsync(start, end);

        if (start > end) (start, end) = (end, start);
        Assert.Equal(DateRange.FromLocalDates(start, end), Assert.Single(query.Ranges));
        Assert.Equal(start, model.StartDate);
        Assert.Equal(end, model.EndDate);
        Assert.False(model.IsTodayRange || model.IsWeekRange || model.IsMonthRange);
        Assert.Equal(start == end, model.IsHourlyDistribution);
        if (start.Year != end.Year)
        {
            Assert.Contains("2025", model.RangeText);
            Assert.Contains("2026", model.RangeText);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RapidSelection_AnOlderResultOrErrorCannotReplaceTheLatestRange(bool failOlder)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var earlierResult = new TaskCompletionSource<DashboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var query = new Query((range, _) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return Task.FromResult(Snapshot(range, "Latest"));
            started.SetResult();
            return earlierResult.Task;
        });
        var dialogs = new Dialogs();
        var model = Model(query, dialogs);
        var earlier = model.WeekCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await model.MonthCommand.ExecuteAsync(null);
        if (failOlder) earlierResult.SetException(new InvalidOperationException("Obsolete query"));
        else earlierResult.SetResult(Snapshot(query.Ranges.First(), "Earlier"));
        await earlier;

        Assert.Equal("Latest", Assert.Single(model.Apps).AppName);
        Assert.True(model.IsMonthRange);
        Assert.True(model.HasSnapshot);
        Assert.False(model.IsLoading || model.HasQueryError);
        Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task AnOlderCompletion_DoesNotDismissTheLatestLoadingState()
    {
        var requests = new ConcurrentQueue<TaskCompletionSource<DashboardSnapshot>>();
        var query = new Query((_, _) =>
        {
            var result = new TaskCompletionSource<DashboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            requests.Enqueue(result);
            return result.Task;
        });
        var model = Model(query);
        var earlier = model.WeekCommand.ExecuteAsync(null);
        await WaitForAsync(() => requests.Count == 1);
        var latest = model.MonthCommand.ExecuteAsync(null);
        await WaitForAsync(() => requests.Count == 2);
        requests.First().SetResult(Snapshot(query.Ranges.First()));
        await earlier;
        Assert.True(model.IsLoading);
        Assert.False(model.HasSnapshot);
        requests.Last().SetResult(Snapshot(query.Ranges.Last(), "Latest"));
        await latest;
        Assert.True(model.HasSnapshot);
        Assert.False(model.IsLoading);
    }

    [Fact]
    public async Task CurrentFailure_HidesOldStatisticsAndRetryUsesTheSelectedRange()
    {
        var shouldFail = false;
        var query = new Query((range, _) => shouldFail
            ? Task.FromException<DashboardSnapshot>(new InvalidOperationException("Current query"))
            : Task.FromResult(Snapshot(range)));
        var dialogs = new Dialogs();
        var model = Model(query, dialogs);
        await model.TodayCommand.ExecuteAsync(null);
        shouldFail = true;
        await model.SelectRangeAsync(Today.AddDays(-4), Today.AddDays(-2));
        Assert.False(model.HasSnapshot);
        Assert.True(model.HasQueryError);
        Assert.Single(dialogs.Errors);
        var selected = query.Ranges.Last();
        shouldFail = false;
        await model.LoadCommand.ExecuteAsync(null);
        Assert.Equal(selected, query.Ranges.Last());
        Assert.True(model.HasSnapshot);
        Assert.False(model.HasQueryError);
    }

    [Fact]
    public async Task RefreshAfterMidnight_MovesRollingPresetsAndKeepsHistoricalCustomDates()
    {
        var clock = new Clock();
        var query = new Query();
        var model = new DashboardViewModel(query, new Dialogs(), clock);
        await model.WeekCommand.ExecuteAsync(null);
        clock.Date = Today.AddDays(1);
        await model.RefreshAsync();
        Assert.Equal(DateRange.FromLocalDates(Today.AddDays(-5), Today.AddDays(1)), query.Ranges.Last());
        await model.SelectRangeAsync(Today.AddDays(-4), Today.AddDays(-2));
        var custom = query.Ranges.Last();
        clock.Date = Today.AddDays(2);
        await model.RefreshAsync();
        Assert.Equal(custom, query.Ranges.Last());
    }

    private static DashboardViewModel Model(Query query, Dialogs? dialogs = null) => new(query, dialogs ?? new Dialogs(), new Clock());

    private static DashboardSnapshot Snapshot(DateRange range, string name = "Application") => new(
        range.Start, range.End, TimeSpan.FromHours(1), TimeSpan.Zero, 1,
        [new AppUsage(name, name, "未分类", TimeSpan.FromHours(1), 1, "#007AFF")], []);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Query(Func<DateRange, CancellationToken, Task<DashboardSnapshot>>? handler = null) : IActivityQueryService
    {
        public ConcurrentQueue<DateRange> Ranges { get; } = new();
        public Task<DashboardSnapshot> GetDashboardAsync(DateRange range, CancellationToken cancellationToken = default)
        {
            Ranges.Enqueue(range);
            return handler?.Invoke(range, cancellationToken) ?? Task.FromResult(Snapshot(range));
        }

        public Task<IReadOnlyList<ActivitySegment>> GetTimelineAsync(DateRange range, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TimelinePage> GetTimelinePageAsync(DateRange range, int pageSize, TimelineCursor? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ApplicationRule>> GetApplicationRulesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTime Date { get; set; } = Today;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(Date.AddHours(12), TimeZoneInfo.Local.GetUtcOffset(Date)).ToUniversalTime();
    }

    private sealed class Dialogs : IUserDialogService
    {
        public List<Exception> Errors { get; } = [];
        public void ShowError(string title, Exception exception) => Errors.Add(exception);
        public bool ConfirmDeleteRange() => false;
        public string? SelectExportPath(string defaultFileName) => null;
    }
}
