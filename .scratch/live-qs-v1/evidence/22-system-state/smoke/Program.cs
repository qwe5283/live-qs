using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Infrastructure.Persistence.Sqlite;
using LiveQs.Windows.Infrastructure.Windows;
using LiveQs.Windows.Services;
using LiveQs.Windows.ViewModels;
using LiveQs.Windows.Views;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace SystemStateSmoke;

internal static class Program
{
    private static DateTimeOffset At(string time) => DateTimeOffset.Parse($"2026-10-08T{time}+08:00");

    [STAThread]
    private static void Main(string[] args)
    {
        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        var paths = new Paths();
        Require(File.Exists(paths.DatabasePath), "The real local history database must exist.");
        var before = Snapshot(paths.DatabasePath);
        var repository = new SqliteActivityRepository(paths, TimeProvider.System);
        repository.InitializeAsync().GetAwaiter().GetResult();
        var reader = new WindowsStateEventReader();
        var until = DateTimeOffset.UtcNow;
        var states = reader.ReadAsync(until.AddDays(-30), until).GetAwaiter().GetResult();
        var pair = DeviceStateTimeline.Pair(states).Single(interval =>
            interval.Start.EventId == 506 && Near(interval.StartedAt, At("00:15:11")) && Near(interval.EndedAt, At("00:32:07")));
        Require(pair.End.EventId == 507, "The real Modern Standby exit must be event 507.");
        repository.CompleteDeviceStateImportAsync(states, until).GetAwaiter().GetResult();
        var day = DateRange.FromLocalDates(new DateTime(2026, 10, 8), new DateTime(2026, 10, 8));
        var importedCount = repository.GetTimelinePageAsync(day, 1).GetAwaiter().GetResult().StateEvents.Count;
        repository.CompleteDeviceStateImportAsync(states, until).GetAwaiter().GetResult();
        Require(importedCount == repository.GetTimelinePageAsync(day, 1).GetAwaiter().GetResult().StateEvents.Count, "Repeated import must retain one event per stable key.");
        Require(before == Snapshot(paths.DatabasePath), "Original activity rows and their queue entries must remain byte-for-byte unchanged.");
        Require(!new WindowsSessionState().IsLocked, "The current interactive smoke session must be unlocked.");

        using var bindingErrors = new StringWriter();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(bindingErrors));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
        application.Resources.MergedDictionaries.Add(new ControlsDictionary());
        application.Resources.MergedDictionaries.Add(Theme("Light"));
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LiveQs.Windows;component/Styles/Theme.xaml")
        });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var query = new FragmentQuery(repository);
        var model = new TimelineViewModel(query, new Dialogs(), NullLogger<TimelineViewModel>.Instance, TimeProvider.System)
        {
            SelectedDate = new DateTime(2026, 10, 8)
        };
        Complete(model.RefreshAsync());
        var away = model.Rows.Single(row => row.Data.IsAfk);
        Require(away.Data.StartedAt == At("00:00:00") && Near(away.Data.EndedAt, At("00:32:14")), "The historical away interval must merge across standby.");
        Require(away.Data.RecordCount == 3 && Math.Abs(away.Data.ObservedDuration.TotalSeconds - 923) < 1, "Keep the three actual records and approximately 923 seconds of sampling coverage.");
        Require(Math.Abs(away.Data.SystemCoveredDuration.TotalSeconds - 1009) < 1, "Add approximately 1009 seconds covered only by system evidence.");
        Require(away.Data.RecoveryCoveredDuration == TimeSpan.Zero && away.Data.RecoveryWindows.Count == 0, "Modern Standby must not add a recovery window.");
        Require(Math.Abs((away.Data.Span - away.Data.ObservedDuration - away.Data.SystemCoveredDuration).TotalSeconds - 2.109) < 0.001, "The short post-standby gap remains uncounted.");
        Require(away.Data.ActiveDuration == TimeSpan.Zero && away.Data.ContinuesBeforeRange, "This is inactive time continuing from the preceding day.");
        Require(model.Rows.All(row => !row.Data.IsGap), "No unexplained long gap should remain in the historical fragment.");
        var rawLock = away.Data.Slices.First(slice => slice.Record.Id == 91793).Record;
        Require(!rawLock.IsAfk && rawLock.IsFullscreen, "Preserve the original LockApp flags.");
        model.Summary = "真实历史片段 · 10 月 8 日凌晨 · 原始记录保持不变";
        var view = new TimelineView(model);
        var window = new Window
        {
            Content = view, Width = 1000, Height = 780, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
        };
        window.Show();
        Pump();
        Save(view, Path.Combine(output, "historical-merged-light.png"));

        away.SelectPositionCommand.Execute(0.7d);
        Pump();
        Require(away.RawRecords.Count == 0, "The chosen standby window must contain no raw foreground record.");
        Require(away.StateEvidenceRows.Any(row => row.Source.Contains("506 → 507")), "Display the actual log pair.");
        Require(Descendants<TextBlock>(view).Any(text => text.Text.Contains("Windows 系统日志 · 事件 506 → 507")), "Render the evidence template in WPF.");
        Save(view, Path.Combine(output, "historical-evidence-light.png"));
        ((ThemesDictionary)application.Resources.MergedDictionaries[0]).Theme = ApplicationTheme.Dark;
        application.Resources.MergedDictionaries[2] = Theme("Dark");
        window.Width = 720;
        Pump();
        Save(view, Path.Combine(output, "historical-evidence-narrow-dark.png"));

        model.SelectedDate = new DateTime(2026, 10, 7);
        Complete(model.RefreshAsync());
        var previousDay = model.Rows.Single(row => row.Data.IsAfk);
        Require(Near(previousDay.Data.StartedAt, At("00:00:00").AddMinutes(-13).AddSeconds(-47)), "Keep the original preceding-day AFK start.");
        Require(previousDay.Data.EndedAt == At("00:00:00") && previousDay.Data.ContinuesAfterRange, "Clip at midnight and indicate continuation.");
        model.Summary = "真实历史片段 · 10 月 7 日晚间 · 跨日延续";
        ((ThemesDictionary)application.Resources.MergedDictionaries[0]).Theme = ApplicationTheme.Light;
        application.Resources.MergedDictionaries[2] = Theme("Light");
        window.Width = 1000;
        Pump();
        Save(view, Path.Combine(output, "historical-previous-day-light.png"));
        VerifyRecoveryDeadline(application, window, output);
        window.Close();
        PresentationTraceSources.DataBindingSource.Flush();
        Require(bindingErrors.ToString().Length == 0, bindingErrors.ToString());
        Require(before == Snapshot(paths.DatabasePath), "Rendering must preserve original rows and their queue entries.");
        var summary = $"""
            Real-device smoke passed.
            Imported normalized System log transitions from the last 30 days: {states.Count}.
            Duplicate import did not create duplicate state events.
            Real paired Modern Standby evidence: {pair.StartedAt.ToLocalTime():O} → {pair.EndedAt.ToLocalTime():O}, events 506 → 507.
            October 8 away episode: {away.Data.StartedAt.ToLocalTime():O} → {away.Data.EndedAt.ToLocalTime():O}, continuing from {previousDay.Data.StartedAt.ToLocalTime():O}.
            Sampling coverage: {away.Data.ObservedDuration.TotalSeconds:F3} seconds across three original records.
            System-only coverage: {away.Data.SystemCoveredDuration.TotalSeconds:F3} seconds.
            Recovery grace: {away.Data.RecoveryCoveredDuration.TotalSeconds:F3} seconds; remaining {(away.Data.Span - away.Data.ObservedDuration - away.Data.SystemCoveredDuration - away.Data.RecoveryCoveredDuration).TotalSeconds:F3} seconds uncounted.
            Active duration: zero. Original activity rows 91791–91794 and their sync queue entries unchanged.
            WPF: light, narrow dark, preceding-day continuation, actual evidence template, no binding errors.
            Current native session query: unlocked.
            Screenshots show the bounded historical fragment, not the entire day's activity.
            Synthetic boot-delay WPF smoke: first sample at 2 minutes 1 second, 2-minute grace and separate 1-second gap; grace source visible in light and narrow dark layouts.
            """;
        File.WriteAllText(Path.Combine(output, "smoke-result.txt"), summary);
        Console.WriteLine(summary);
    }

    private static void VerifyRecoveryDeadline(Application application, Window window, string output)
    {
        var boot = At("08:00:00");
        var records = new[]
        {
            new ActivitySegment(1, boot.AddSeconds(121), boot.AddSeconds(721), "code", "IDE", "", false,
                false, false, "开发", "#007AFF")
        };
        var states = new[]
        {
            new DeviceStateEvent("shutdown", boot.AddMinutes(-10), DeviceStateKind.Shutdown, true, "system:shutdown", "Windows 关机", 13),
            new DeviceStateEvent("boot", boot, DeviceStateKind.Shutdown, false, "system:shutdown", "Windows 关机", 12)
        };
        var model = new TimelineViewModel(new Samples(records, states), new Dialogs(), NullLogger<TimelineViewModel>.Instance, TimeProvider.System)
        {
            SelectedDate = boot.ToLocalTime().Date
        };
        Complete(model.RefreshAsync());
        Require(model.Rows.Count == 3, "Boot grace, unexplained excess and active use must be independent rows.");
        Require(model.Rows.Single(row => row.Data.IsGap).Data.Span == TimeSpan.FromSeconds(1), "Excess beyond the 2-minute deadline cannot use ordinary 30-second tolerance.");
        var away = model.Rows.Single(row => row.Data.IsAfk);
        away.SelectPositionCommand.Execute(0.98d);
        Require(away.Data.RecoveryCoveredDuration == TimeSpan.FromMinutes(2), "Cap missing boot time at 2 minutes.");
        model.Summary = "开机恢复规则样例 · 2 分钟缓冲 · 超出的 1 秒单列";
        var view = new TimelineView(model);
        window.Content = view;
        window.Width = 1000;
        Pump();
        Require(Descendants<TextBlock>(view).Any(text => text.Text == "采集恢复窗口"), "Render the recovery window title without the duration suffix.");
        Save(view, Path.Combine(output, "boot-delay-rule-light.png"));
        ((ThemesDictionary)application.Resources.MergedDictionaries[0]).Theme = ApplicationTheme.Dark;
        application.Resources.MergedDictionaries[2] = Theme("Dark");
        window.Width = 720;
        Pump();
        Save(view, Path.Combine(output, "boot-delay-rule-narrow-dark.png"));
    }

    private static string Snapshot(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        using var text = new StringWriter();
        foreach (var table in new[] { "activity_segments", "sync_queue" })
        {
            using var command = connection.CreateCommand();
            var key = table == "activity_segments" ? "id" : "segment_id";
            command.CommandText = $"SELECT * FROM {table} WHERE {key} BETWEEN 91791 AND 91794 ORDER BY {key};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    var value = reader.GetValue(index).ToString() ?? "";
                    text.Write($"{value.Length}:{value}");
                }
                text.WriteLine();
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static ResourceDictionary Theme(string name) => new()
    {
        Source = new Uri($"pack://application:,,,/LiveQs.Windows;component/Styles/Theme.{name}.xaml")
    };

    private static void Complete(Task task)
    {
        while (!task.IsCompleted)
        {
            Pump();
        }
        task.GetAwaiter().GetResult();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static void Save(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool Near(DateTimeOffset actual, DateTimeOffset expected) => Math.Abs((actual - expected).TotalSeconds) < 1;

    private sealed class Paths : IAppPaths
    {
        public string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveQs", "Windows");
        public string DatabasePath => Path.Combine(DataDirectory, "liveqs.db");
        public string LogPath => Path.Combine(DataDirectory, "liveqs.log");
    }

    private sealed class FragmentQuery(SqliteActivityRepository repository) : IActivityQueryService
    {
        public async Task<TimelinePage> GetTimelinePageAsync(DateRange range, int pageSize, TimelineCursor? cursor = null,
            CancellationToken cancellationToken = default)
        {
            var first = At("00:00:00").AddMinutes(-13).AddSeconds(-47);
            var last = At("00:32:24");
            var fragment = new DateRange(range.Start > first ? range.Start : first, range.End < last ? range.End : last);
            var page = await repository.GetTimelinePageAsync(fragment, pageSize, cursor, cancellationToken);
            var events = DeviceStateTimeline.Pair(page.StateEvents)
                .Where(interval => interval.StartedAt < fragment.End && interval.EndedAt > fragment.Start)
                .SelectMany(interval => new[] { interval.Start, interval.End }).DistinctBy(value => value.Key).ToArray();
            return page with { StateEvents = events };
        }

        public Task<IReadOnlyList<ActivitySegment>> GetTimelineAsync(DateRange range,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DashboardSnapshot> GetDashboardAsync(DateRange range,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ApplicationRule>> GetApplicationRulesAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Dialogs : IUserDialogService
    {
        public void ShowError(string title, Exception exception) => throw new InvalidOperationException(title, exception);
        public bool ConfirmDeleteRange() => false;
        public string? SelectExportPath(string defaultFileName) => null;
    }

    private sealed class Samples(ActivitySegment[] records, DeviceStateEvent[] states) : IActivityQueryService
    {
        public Task<TimelinePage> GetTimelinePageAsync(DateRange range, int pageSize, TimelineCursor? cursor = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new TimelinePage(records, null, false) { StateEvents = states });
        public Task<IReadOnlyList<ActivitySegment>> GetTimelineAsync(DateRange range,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DashboardSnapshot> GetDashboardAsync(DateRange range,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ApplicationRule>> GetApplicationRulesAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
