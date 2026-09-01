using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Infrastructure.Persistence.Sqlite;

namespace LiveQs.Windows.Tests;

public sealed class RepositoryTests : IDisposable
{
    private readonly TestPaths _paths = new();

    [Fact]
    public async Task RecordSample_MergesContinuousStateAndSeparatesAfk()
    {
        var repository = await CreateRepositoryAsync();
        var localNoon = new DateTimeOffset(DateTime.Today.AddHours(12), TimeZoneInfo.Local.GetUtcOffset(DateTime.Today.AddHours(12)));

        await repository.RecordSampleAsync(Sample(localNoon, false), TimeSpan.FromSeconds(5));
        await repository.RecordSampleAsync(Sample(localNoon.AddSeconds(5), false), TimeSpan.FromSeconds(5));
        await repository.RecordSampleAsync(Sample(localNoon.AddSeconds(10), true), TimeSpan.FromSeconds(5));

        var dashboard = await repository.GetDashboardAsync(DateRange.Today());
        var timeline = await repository.GetTimelineAsync(DateRange.Today());
        Assert.Equal(2, timeline.Count);
        Assert.Equal(10, dashboard.ActiveDuration.TotalSeconds, 3);
        Assert.Equal(5, dashboard.AfkDuration.TotalSeconds, 3);
        Assert.Single(dashboard.Apps);
        Assert.Equal(2, await repository.GetPendingSyncCountAsync());
    }

    [Fact]
    public async Task ApplicationRule_ChangesHistoricalQueriesWithoutDeletingRows()
    {
        var repository = await CreateRepositoryAsync();
        var localNoon = new DateTimeOffset(DateTime.Today.AddHours(12), TimeZoneInfo.Local.GetUtcOffset(DateTime.Today.AddHours(12)));
        await repository.RecordSampleAsync(Sample(localNoon, false), TimeSpan.FromSeconds(5));

        await repository.SaveApplicationRuleAsync(new ApplicationRule("browser.exe", "浏览器", "工作", true));
        var hidden = await repository.GetTimelineAsync(DateRange.Today());
        Assert.Empty(hidden);

        await repository.SaveApplicationRuleAsync(new ApplicationRule("browser.exe", "浏览器", "工作", false));
        var restored = await repository.GetTimelineAsync(DateRange.Today());
        Assert.Single(restored);
        Assert.Equal("浏览器", restored[0].AppName);
        Assert.Equal("工作", restored[0].Category);
    }

    [Fact]
    public async Task MarkSyncedAndDeleteRange_MaintainQueueConsistency()
    {
        var repository = await CreateRepositoryAsync();
        var localNoon = new DateTimeOffset(DateTime.Today.AddHours(12), TimeZoneInfo.Local.GetUtcOffset(DateTime.Today.AddHours(12)));
        await repository.RecordSampleAsync(Sample(localNoon, false), TimeSpan.FromSeconds(5));
        var items = await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Single(items);

        await repository.MarkSyncedAsync(items.Select(item => item.SegmentId), DateTimeOffset.UtcNow);
        Assert.Equal(0, await repository.GetPendingSyncCountAsync());

        Assert.Equal(1, await repository.DeleteRangeAsync(DateRange.Today()));
        Assert.Empty(await repository.GetTimelineAsync(DateRange.Today()));
    }

    [Fact]
    public async Task TimelinePage_UsesStableCursorWithoutDuplicatesOrGaps()
    {
        var repository = await CreateRepositoryAsync();
        var localNoon = new DateTimeOffset(DateTime.Today.AddHours(12), TimeZoneInfo.Local.GetUtcOffset(DateTime.Today.AddHours(12)));
        for (var index = 0; index < 7; index++)
            await repository.RecordSampleAsync(Sample(localNoon.AddSeconds(index * 5), index % 2 == 0), TimeSpan.FromSeconds(5));

        var expected = await repository.GetTimelineAsync(DateRange.Today());
        var actual = new List<ActivitySegment>();
        TimelineCursor? cursor = null;
        TimelinePage page;
        do
        {
            page = await repository.GetTimelinePageAsync(DateRange.Today(), 3, cursor);
            actual.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (page.HasMore);

        Assert.Equal(expected.Select(segment => segment.Id), actual.Select(segment => segment.Id));
        Assert.Equal(actual.Count, actual.Select(segment => segment.Id).Distinct().Count());
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task PendingSync_CarriesRevisionAndFinalizesWhenIntervalEnds()
    {
        var repository = await CreateRepositoryAsync();
        var localNoon = new DateTimeOffset(DateTime.Today.AddHours(12), TimeZoneInfo.Local.GetUtcOffset(DateTime.Today.AddHours(12)));

        // Two contiguous samples of the same app merge into one logical interval at revision 2.
        await repository.RecordSampleAsync(Sample(localNoon, false), TimeSpan.FromSeconds(5));
        await repository.RecordSampleAsync(Sample(localNoon.AddSeconds(5), false), TimeSpan.FromSeconds(5));
        var openItem = Assert.Single(await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal(2, openItem.SyncVersion);
        Assert.False(openItem.Finalized);

        // A different foreground app starts a new segment and finalizes the previous one.
        await repository.RecordSampleAsync(Sample(localNoon.AddSeconds(20), false, "editor.exe"), TimeSpan.FromSeconds(5));
        var pending = await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(2, pending.Count);

        var finalized = pending.Single(item => item.AppId == "browser.exe");
        Assert.True(finalized.Finalized);
        Assert.Equal(3, finalized.SyncVersion);

        var fresh = pending.Single(item => item.AppId == "editor.exe");
        Assert.False(fresh.Finalized);
        Assert.Equal(1, fresh.SyncVersion);
    }

    [Fact]
    public async Task MarkPermanent_StopsRetryingRejectedSegments()
    {
        var repository = await CreateRepositoryAsync();
        var localNoon = new DateTimeOffset(DateTime.Today.AddHours(12), TimeZoneInfo.Local.GetUtcOffset(DateTime.Today.AddHours(12)));
        await repository.RecordSampleAsync(Sample(localNoon, false), TimeSpan.FromSeconds(5));
        var item = Assert.Single(await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(1)));

        await repository.MarkPermanentAsync(new[] { item.SegmentId }, "privacy_ceiling_exceeded", DateTimeOffset.UtcNow);

        // The dead letter never retries, but stays visible in the queue depth.
        Assert.Empty(await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal(1, await repository.GetPendingSyncCountAsync());
    }

    [Fact]
    public async Task InstallId_IsStableAcrossReopen()
    {
        var first = await CreateRepositoryAsync();
        var installId = await first.GetInstallIdAsync();
        Assert.False(string.IsNullOrWhiteSpace(installId));

        var reopened = await CreateRepositoryAsync();
        Assert.Equal(installId, await reopened.GetInstallIdAsync());
    }

    private async Task<SqliteActivityRepository> CreateRepositoryAsync()
    {
        var repository = new SqliteActivityRepository(_paths, TimeProvider.System);
        await repository.InitializeAsync();
        return repository;
    }

    private static ActivitySample Sample(DateTimeOffset time, bool afk, string appId = "browser.exe") => new(
        time.ToUniversalTime(), appId, "Browser", "C:\\Browser.exe", "Docs", "hash",
        afk ? 120 : 0, afk, false, false, null, null);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory)) Directory.Delete(_paths.DataDirectory, true);
    }

    private sealed class TestPaths : IAppPaths
    {
        public TestPaths()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "LiveQs.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataDirectory);
        }

        public string DataDirectory { get; }
        public string DatabasePath => Path.Combine(DataDirectory, "test.db");
        public string LogPath => Path.Combine(DataDirectory, "test.log");
    }
}
