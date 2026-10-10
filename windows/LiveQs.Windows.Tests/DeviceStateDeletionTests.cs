using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Infrastructure.Persistence.Sqlite;
using LiveQs.Windows.Infrastructure.Windows;
using LiveQs.Windows.Services;
using LiveQs.Windows.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveQs.Windows.Tests;

public sealed class DeviceStateDeletionTests : IDisposable
{
    private readonly TestDataPaths _paths = new();
    private readonly TestClock _clock = new();
    private static readonly DateTimeOffset _day = new(2026, 10, 8, 0, 0, 0, TimeZoneInfo.Local.BaseUtcOffset);
    private static readonly DateRange _range = new(_day, _day.AddDays(1));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_RemovesSystemOnlyHistoryAndKeepsCheckpoint(bool retention)
    {
        var repository = await CreateAsync();
        await repository.CompleteDeviceStateImportAsync(Pair(_day.AddMinutes(15), _day.AddMinutes(32)), _clock.GetUtcNow());
        await SampleAsync(repository, _day.AddHours(1));

        var deleted = retention
            ? await repository.DeleteBeforeAsync(_range.End)
            : await repository.DeleteRangeAsync(_range);
        var reopened = await CreateAsync();
        var page = await reopened.GetTimelinePageAsync(_range, 500);

        Assert.Equal(1, deleted);
        Assert.Empty(page.Items);
        Assert.Empty(page.StateEvents);
        Assert.Equal(0, await CountAsync("device_state_events"));
        Assert.Empty(await RowsAsync(reopened));
        Assert.Equal(_clock.GetUtcNow(), (await reopened.GetDeviceStateImportStatusAsync()).ImportedUntil);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_RejectsEvidenceFromAnImportAlreadyInFlight(bool retention)
    {
        _clock.UtcNow = _day.AddMinutes(40);
        var repository = await CreateAsync();
        var events = Pair(_day.AddMinutes(30), _day.AddMinutes(32));
        await repository.CompleteDeviceStateImportAsync(events, _clock.GetUtcNow());
        var reader = new PausedReader(events);
        var worker = new WindowsDeviceStateWorker(repository, reader, new WindowsSessionState(), _clock,
            NullLogger<WindowsDeviceStateWorker>.Instance);
        var import = worker.ImportAsync();
        await reader.Started.Task;

        if (retention)
        {
            await repository.DeleteBeforeAsync(_day.AddMinutes(35));
        }
        else
        {
            await repository.DeleteRangeAsync(new DateRange(_day.AddMinutes(25), _day.AddMinutes(35)));
        }
        reader.Complete();
        await import;
        var reopened = await CreateAsync();
        await reopened.CompleteDeviceStateImportAsync(events, _clock.GetUtcNow().AddMinutes(5));
        await reopened.AppendDeviceStateEventsAsync(events);

        Assert.Equal(_day.AddMinutes(25), reader.Since);
        Assert.Empty((await reopened.GetTimelinePageAsync(_range, 500)).StateEvents);
        Assert.Empty(await RowsAsync(reopened));
        Assert.Equal(_clock.GetUtcNow().AddMinutes(5), (await reopened.GetDeviceStateImportStatusAsync()).ImportedUntil);
    }

    [Fact]
    public async Task DeletedDay_IsNotReconstructedFromCrossDayEndpoints()
    {
        var repository = await CreateAsync();
        await repository.CompleteDeviceStateImportAsync(Pair(_day.AddHours(-1), _range.End.AddHours(1)), _clock.GetUtcNow());

        await repository.DeleteRangeAsync(_range);
        var reopened = await CreateAsync();

        Assert.Equal(2, (await reopened.GetTimelinePageAsync(_range, 500)).StateEvents.Count);
        Assert.Empty(await RowsAsync(reopened));
    }

    [Fact]
    public async Task DeletedGap_PreventsJoiningNearbyActivityAndKeepsOutsideEvidence()
    {
        var repository = await CreateAsync();
        await SampleAsync(repository, _day);
        await SampleAsync(repository, _day.AddSeconds(35));
        await repository.CompleteDeviceStateImportAsync(Pair(_day.AddHours(1), _day.AddHours(2)), _clock.GetUtcNow());

        await repository.DeleteRangeAsync(new DateRange(_day.AddSeconds(12), _day.AddSeconds(14)));
        var rows = await RowsAsync(repository);

        Assert.Equal(2, rows.Count(row => !row.IsAfk && !row.IsGap));
        Assert.Equal(TimeSpan.FromHours(1), Assert.Single(rows, row => row.IsAfk).SystemCoveredDuration);
        Assert.Contains(rows, row => row.IsGap && row.StartedAt == _day.AddSeconds(10) && row.EndedAt == _day.AddSeconds(35));
    }

    [Fact]
    public async Task DeletingFirstSample_DoesNotExtendAnEarlierRecoveryWindow()
    {
        var repository = await CreateAsync();
        await repository.CompleteDeviceStateImportAsync(
            [new DeviceStateEvent("boot", _day, DeviceStateKind.Shutdown, false, "system:shutdown", "Windows 开机", 12)],
            _clock.GetUtcNow());
        await SampleAsync(repository, _day.AddSeconds(10));

        await repository.DeleteRangeAsync(new DateRange(_day.AddSeconds(10), _day.AddSeconds(11)));
        var reopened = await CreateAsync();
        var page = await reopened.GetTimelinePageAsync(_range, 500);
        var recovery = Assert.Single(page.RecoveryWindows!);

        Assert.Equal(_day.AddSeconds(10), recovery.EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(await RowsAsync(reopened)).RecoveryCoveredDuration);
    }

    [Fact]
    public async Task RetentionFloor_DoesNotMoveBackwardsOrRemoveEventsAtTheCutoff()
    {
        var repository = await CreateAsync();
        var cutoff = _day.AddMinutes(30);
        await repository.DeleteBeforeAsync(cutoff);
        var reopened = await CreateAsync();
        await reopened.DeleteBeforeAsync(cutoff.AddMinutes(-10));
        await reopened.CompleteDeviceStateImportAsync(Pair(cutoff.AddTicks(-1), cutoff), _clock.GetUtcNow());

        var page = await reopened.GetTimelinePageAsync(_range, 500);

        Assert.Equal(cutoff, Assert.Single(page.StateEvents).OccurredAt);
    }

    [Fact]
    public async Task DeleteToday_DoesNotBlockFutureLiveEvents()
    {
        _clock.UtcNow = _day.AddHours(12);
        var deletedAt = _clock.GetUtcNow();
        var repository = await CreateAsync();
        var atDeletion = new DeviceStateEvent("at-deletion", deletedAt, DeviceStateKind.Shutdown, false,
            "system:shutdown", "Windows 开机", 12);
        await repository.AppendDeviceStateEventsAsync([atDeletion]);
        await repository.DeleteRangeAsync(_range);
        var past = Pair(_day.AddHours(10), _day.AddHours(11));
        var future = Pair(_day.AddHours(13), _day.AddHours(14));
        _clock.Advance(TimeSpan.FromHours(2));

        await repository.AppendDeviceStateEventsAsync(past.Append(atDeletion)
            .Concat(future.Select(value => value with { Key = "future-" + value.Key })).ToArray());

        var page = await repository.GetTimelinePageAsync(_range, 500);
        Assert.Equal(2, page.StateEvents.Count);
        Assert.All(page.StateEvents, value => Assert.True(value.OccurredAt > deletedAt));
        Assert.Equal(TimeSpan.FromHours(1), Assert.Single(await RowsAsync(repository)).SystemCoveredDuration);
    }

    [Fact]
    public async Task DeletedBoundary_StopsRecoveryFromThePreviousDay()
    {
        var repository = await CreateAsync();
        await repository.CompleteDeviceStateImportAsync(
            [new DeviceStateEvent("boot", _day.AddMinutes(-1), DeviceStateKind.Shutdown, false,
                "system:shutdown", "Windows 开机", 12)], _clock.GetUtcNow());

        await repository.DeleteRangeAsync(_range);
        var page = await repository.GetTimelinePageAsync(_range, 500);

        Assert.Single(page.StateEvents);
        Assert.Empty(page.RecoveryWindows!);
        Assert.Empty(await RowsAsync(repository));
    }

    [Fact]
    public async Task CrossDeletionPair_IsNotUsedToMarkLaterSamplingInactive()
    {
        var repository = await CreateAsync();
        await repository.CompleteDeviceStateImportAsync(Pair(_day.AddMinutes(-10), _day.AddMinutes(10)), _clock.GetUtcNow());
        await SampleAsync(repository, _day.AddMinutes(3));

        await repository.DeleteRangeAsync(new DateRange(_day.AddMinutes(1), _day.AddMinutes(2)));
        var dashboard = await repository.GetDashboardAsync(_range);
        var row = Assert.Single(await RowsAsync(repository));

        Assert.Equal(TimeSpan.FromSeconds(10), dashboard.ActiveDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), row.ActiveDuration);
        Assert.Empty(row.StateEvidence);
    }

    [Fact]
    public async Task OverlappingAndAdjacentDeletes_CoalesceAndKeepTheExclusiveEnd()
    {
        var repository = await CreateAsync();
        await repository.DeleteRangeAsync(new DateRange(_day.AddSeconds(10), _day.AddSeconds(20)));
        await repository.DeleteRangeAsync(new DateRange(_day.AddSeconds(30), _day.AddSeconds(40)));
        await repository.DeleteRangeAsync(new DateRange(_day.AddSeconds(15), _day.AddSeconds(30)));
        await repository.DeleteRangeAsync(new DateRange(_day.AddSeconds(10), _day.AddSeconds(40)));
        var reopened = await CreateAsync();
        await reopened.AppendDeviceStateEventsAsync(Pair(_day.AddSeconds(10), _day.AddSeconds(40)));

        Assert.Equal(_day.AddSeconds(40), Assert.Single((await reopened.GetTimelinePageAsync(_range, 500)).StateEvents).OccurredAt);
        Assert.Equal(1, await CountAsync("device_state_deleted_ranges"));

        await reopened.DeleteBeforeAsync(_day.AddSeconds(30));
        await reopened.DeleteBeforeAsync(_day.AddSeconds(20));
        await reopened.AppendDeviceStateEventsAsync(Pair(_day.AddSeconds(39), _day.AddSeconds(40)));

        Assert.Equal(_day.AddSeconds(40), Assert.Single((await reopened.GetTimelinePageAsync(_range, 500)).StateEvents).OccurredAt);
        Assert.Equal(1, await CountAsync("device_state_deleted_ranges"));
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<SqliteActivityRepository> CreateAsync()
    {
        var repository = new SqliteActivityRepository(_paths, _clock);
        await repository.InitializeAsync();
        return repository;
    }

    private static DeviceStateEvent[] Pair(DateTimeOffset start, DateTimeOffset end) =>
    [
        new("sleep", start, DeviceStateKind.Sleep, true, "system:modern-standby", "现代待机", 506, 1),
        new("wake", end, DeviceStateKind.Sleep, false, "system:modern-standby", "现代待机", 507, 2)
    ];

    private static Task SampleAsync(SqliteActivityRepository repository, DateTimeOffset at) =>
        repository.RecordSampleAsync(new ActivitySample(at, "test.exe", "Test", "", "", "", 0,
            false, false, false, null, null), TimeSpan.FromSeconds(10));

    private async Task<IReadOnlyList<TimelineEpisode>> RowsAsync(SqliteActivityRepository repository)
    {
        var model = new TimelineViewModel(repository, new Dialogs(), NullLogger<TimelineViewModel>.Instance, _clock)
        {
            SelectedDate = _day.Date
        };
        await model.RefreshAsync();
        return model.Rows.Select(row => row.Data).ToArray();
    }

    public void Dispose() => _paths.Dispose();

    private sealed class Dialogs : IUserDialogService
    {
        public void ShowError(string title, Exception exception) => throw new InvalidOperationException(title, exception);
        public bool ConfirmDeleteRange() => false;
        public string? SelectExportPath(string defaultFileName) => null;
    }

    private sealed class PausedReader(IReadOnlyList<DeviceStateEvent> events) : IWindowsStateEventReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<DeviceStateEvent>> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset Since
        {
            get; private set;
        }

        public Task<IReadOnlyList<DeviceStateEvent>> ReadAsync(DateTimeOffset start, DateTimeOffset end,
            CancellationToken cancellationToken = default)
        {
            Since = start;
            Started.SetResult();
            return _result.Task.WaitAsync(cancellationToken);
        }

        public void Complete() => _result.SetResult(events);
    }
}
