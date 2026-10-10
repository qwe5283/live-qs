using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Infrastructure.Persistence.Sqlite;
using LiveQs.Windows.Services;
using LiveQs.Windows.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveQs.Windows.Tests;

public sealed class TimelineRecoveryRepositoryTests : IDisposable
{
    private readonly TestDataPaths _paths = new();
    private readonly TestClock _clock = new();
    private static readonly DateTimeOffset _start = new(2026, 10, 8, 0, 0, 0, TimeZoneInfo.Local.BaseUtcOffset);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcludedFirstSample_StillEndsRecovery(bool excludedWhenSampled)
    {
        var repository = await CreateAsync();
        await AddBootAsync(repository, _start);
        if (excludedWhenSampled)
        {
            await repository.SaveApplicationRuleAsync(new ApplicationRule("hidden.exe", "", "", true));
        }
        await SampleAsync(repository, _start.AddSeconds(10), "hidden.exe", true);
        await SampleAsync(repository, _start.AddSeconds(121), "code.exe", false);
        var model = Model(repository);
        await model.RefreshAsync();
        var originalRecovery = model.Rows.Sum(row => row.Data.RecoveryCoveredDuration.TotalSeconds);

        if (!excludedWhenSampled)
        {
            await repository.SaveApplicationRuleAsync(new ApplicationRule("hidden.exe", "", "", true));
        }
        await model.RefreshAsync();

        Assert.Equal(10, originalRecovery);
        Assert.Equal(10, model.Rows.Sum(row => row.Data.RecoveryCoveredDuration.TotalSeconds));
        Assert.All(model.Rows, row => Assert.DoesNotContain(row.Data.Slices, slice => slice.Record.AppId == "hidden.exe"));
        Assert.Equal(TimeSpan.FromSeconds(111), Assert.Single(model.Rows, row => row.Data.IsGap).Data.Span);
    }

    [Fact]
    public async Task SameStartRecords_KeepBothRawObservationsAtTheDayBoundary()
    {
        var repository = await CreateAsync();
        await SampleAsync(repository, _start, "original.exe", false);
        await SampleAsync(repository, _start, "newer.exe", false);
        var model = Model(repository);

        await model.RefreshAsync();

        var row = Assert.Single(model.Rows);
        row.SelectPositionCommand.Execute(0.5d);
        Assert.Equal(2, row.Data.RecordCount);
        Assert.Equal(2, row.RawRecords.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), row.Data.ObservedDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), row.Data.ActiveDuration);
        Assert.Equal("newer.exe", Assert.Single(row.Data.Applications).AppId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcludedSamplingMarker_RemainsPrivateAndFollowsActivityDeletion(bool deleteRange)
    {
        var repository = await CreateAsync();
        await repository.SaveApplicationRuleAsync(new ApplicationRule("hidden.exe", "", "", true));
        await AddBootAsync(repository, _start);
        await SampleAsync(repository, _start.AddSeconds(10), "hidden.exe", true);
        var reopened = await CreateAsync();
        var model = Model(reopened);
        await model.RefreshAsync();
        Assert.Equal(10, model.Rows.Sum(row => row.Data.RecoveryCoveredDuration.TotalSeconds));
        Assert.Empty(await reopened.GetTimelineAsync(new DateRange(_start, _start.AddDays(1))));
        Assert.Empty(await reopened.GetPendingSyncAsync(10, _clock.GetUtcNow()));

        var deleted = deleteRange
            ? await reopened.DeleteRangeAsync(new DateRange(_start, _start.AddSeconds(11)))
            : await reopened.DeleteBeforeAsync(_start.AddSeconds(11));
        await model.RefreshAsync();

        Assert.Equal(0, deleted);
        Assert.Empty(model.Rows);
    }

    [Fact]
    public async Task PreviousDayFirstSample_EndsRecoveryBeforeDateFiltering()
    {
        var repository = await CreateAsync();
        await AddBootAsync(repository, _start.AddMinutes(-1));
        await SampleAsync(repository, _start.AddSeconds(-50), "hidden.exe", true);
        await SampleAsync(repository, _start.AddMinutes(3), "code.exe", true);
        var model = Model(repository);

        await model.RefreshAsync();

        Assert.All(model.Rows, row => Assert.Empty(row.Data.RecoveryWindows));
        Assert.Equal(0, model.Rows.Sum(row => row.Data.RecoveryCoveredDuration.TotalSeconds));
        Assert.Single(model.Rows);
    }

    private async Task<SqliteActivityRepository> CreateAsync()
    {
        var repository = new SqliteActivityRepository(_paths, _clock);
        await repository.InitializeAsync();
        return repository;
    }

    private Task AddBootAsync(SqliteActivityRepository repository, DateTimeOffset at) =>
        repository.CompleteDeviceStateImportAsync(
            [new DeviceStateEvent("boot", at, DeviceStateKind.Shutdown, false, "system:shutdown", "Windows 开机", 12)],
            _clock.GetUtcNow());

    private static Task SampleAsync(SqliteActivityRepository repository, DateTimeOffset at, string app, bool afk) =>
        repository.RecordSampleAsync(new ActivitySample(at, app, app, "private-path", "private-title", "", 0,
            afk, false, false, null, null), TimeSpan.FromSeconds(10));

    private TimelineViewModel Model(SqliteActivityRepository repository) => new(
        repository, new Dialogs(), NullLogger<TimelineViewModel>.Instance, _clock)
    {
        SelectedDate = _start.Date
    };

    public void Dispose() => _paths.Dispose();

    private sealed class Dialogs : IUserDialogService
    {
        public void ShowError(string title, Exception exception) => throw new InvalidOperationException(title, exception);
        public bool ConfirmDeleteRange() => false;
        public string? SelectExportPath(string defaultFileName) => null;
    }
}
