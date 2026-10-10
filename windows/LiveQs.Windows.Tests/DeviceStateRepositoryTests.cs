using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Infrastructure.Persistence.Sqlite;
using LiveQs.Windows.Infrastructure.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveQs.Windows.Tests;

public sealed class DeviceStateRepositoryTests : IDisposable
{
    private readonly TestDataPaths _paths = new();
    private readonly TestClock _clock = new();
    private static readonly DateRange _range = DateRange.FromLocalDates(new DateTime(2026, 10, 8), new DateTime(2026, 10, 8));
    private static readonly DeviceStateEvent[] _events =
    [
        new("sleep", DateTimeOffset.Parse("2026-10-08T00:15:11+08:00"), DeviceStateKind.Sleep, true, "system:test", "现代待机", 506, 1),
        new("wake", DateTimeOffset.Parse("2026-10-08T00:32:07+08:00"), DeviceStateKind.Sleep, false, "system:test", "现代待机", 507, 2)
    ];

    [Fact]
    public async Task DuplicateImports_KeepOneStateHistoryAcrossRestartAndDoNotEnqueueActivity()
    {
        var repository = await CreateAsync();
        await repository.CompleteDeviceStateImportAsync(_events, _clock.GetUtcNow());
        await repository.CompleteDeviceStateImportAsync(_events.Reverse().ToArray(), _clock.GetUtcNow().AddMinutes(-1));
        var reopened = await CreateAsync();
        var page = await reopened.GetTimelinePageAsync(_range, 1);

        Assert.Equal(2, page.StateEvents.Count);
        Assert.Single(DeviceStateTimeline.Pair(page.StateEvents));
        Assert.Equal(_clock.GetUtcNow(), (await reopened.GetDeviceStateImportStatusAsync()).ImportedUntil);
        Assert.Equal(0, await reopened.GetPendingSyncCountAsync());
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task LogFailure_RetainsPriorEvidenceAndWatermarkAndRemainsVisibleInQueries()
    {
        var repository = await CreateAsync();
        var checkpoint = _clock.GetUtcNow().AddHours(-1);
        await repository.CompleteDeviceStateImportAsync(_events, checkpoint);
        var reader = new Reader { Fail = true };
        var worker = new WindowsDeviceStateWorker(repository, reader, new WindowsSessionState(), _clock,
            NullLogger<WindowsDeviceStateWorker>.Instance);
        await worker.ImportAsync();
        var failed = await repository.GetTimelinePageAsync(_range, 500);

        Assert.Equal(2, failed.StateEvents.Count);
        Assert.Contains("系统日志暂不可读取", failed.StateWarning);
        Assert.Equal(checkpoint, (await repository.GetDeviceStateImportStatusAsync()).ImportedUntil);
        reader.Fail = false;
        await worker.ImportAsync();
        var recovered = await repository.GetTimelinePageAsync(_range, 500);
        Assert.Empty(recovered.StateWarning);
        Assert.Equal(2, recovered.StateEvents.Count);
        Assert.Equal(checkpoint.AddMinutes(-15), reader.LastStart);
    }

    [Fact]
    public async Task LocalStateImport_DoesNotChangeOriginalActivityRevisionOrPayload()
    {
        var repository = await CreateAsync();
        var at = DateTimeOffset.Parse("2026-10-08T00:32:09+08:00");
        await repository.RecordSampleAsync(new ActivitySample(at, "lockapp.exe", "锁屏", "", "", "", 0,
            false, false, true, null, null), TimeSpan.FromSeconds(5));
        var original = Assert.Single(await repository.GetPendingSyncAsync(10, _clock.GetUtcNow()));
        await repository.CompleteDeviceStateImportAsync(_events, _clock.GetUtcNow());
        var after = Assert.Single(await repository.GetPendingSyncAsync(10, _clock.GetUtcNow()));

        Assert.Equal(original, after);
        Assert.False(Assert.Single((await repository.GetTimelinePageAsync(_range, 500)).Items).IsAfk);
        Assert.Equal(TimeSpan.Zero, (await repository.GetDashboardAsync(_range)).ActiveDuration);
    }

    private async Task<SqliteActivityRepository> CreateAsync()
    {
        var repository = new SqliteActivityRepository(_paths, _clock);
        await repository.InitializeAsync();
        return repository;
    }

    public void Dispose() => _paths.Dispose();

    private sealed class Reader : IWindowsStateEventReader
    {
        public bool Fail
        {
            get; set;
        }
        public DateTimeOffset LastStart
        {
            get; private set;
        }
        public Task<IReadOnlyList<DeviceStateEvent>> ReadAsync(DateTimeOffset start, DateTimeOffset end,
            CancellationToken cancellationToken = default)
        {
            LastStart = start;
            return Fail ? throw new UnauthorizedAccessException() : Task.FromResult<IReadOnlyList<DeviceStateEvent>>(_events);
        }
    }
}
