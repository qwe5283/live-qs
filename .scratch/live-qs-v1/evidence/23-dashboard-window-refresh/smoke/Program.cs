using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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
using LiveQs.Windows.Infrastructure.DependencyInjection;
using LiveQs.Windows.Services;
using LiveQs.Windows.ViewModels;
using LiveQs.Windows.Views;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
        application.Resources.MergedDictionaries.Add(new ControlsDictionary());
        foreach (var name in new[] { "Theme.Light", "Theme" })
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/LiveQs.Windows;component/Styles/{name}.xaml")
            });
        }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var query = new Query();
        var dialogs = new Dialogs();
        var services = new ServiceCollection().AddLogging().AddLiveQsInfrastructure(new Paths())
            .AddSingleton<IActivityQueryService>(query).AddSingleton<IUserDialogService>(dialogs)
            .AddSingleton<IStartupManager, Startup>()
            .AddSingleton<DashboardViewModel>().AddSingleton<TimelineViewModel>().AddSingleton<SettingsViewModel>()
            .AddSingleton<DashboardView>().AddSingleton<TimelineView>().AddSingleton<SettingsView>()
            .AddSingleton<MainWindow>();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync().GetAwaiter().GetResult();
        var window = provider.GetRequiredService<MainWindow>();
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Left = -10000;
        window.Top = -10000;
        var model = provider.GetRequiredService<DashboardViewModel>();
        var dashboard = provider.GetRequiredService<DashboardView>();
        var dashboardContent = (ScrollViewer)dashboard.FindName("DashboardScrollViewer");
        var companion = new Window { Style = new Style(typeof(Window)), ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000, Width = 100, Height = 100 };
        window.Show();
        Wait(() => model.HasSnapshot && !model.IsLoading);
        Pump();
        Require(query.Count == 1, "Initial display loads once.");
        if (args.Contains("--loading"))
        {
            CheckLoading(window, dashboard, model, query);
            Require(dialogs.Errors.Count == 0, "No query or settings errors.");
            window.Hide();
            return;
        }

        var start = DateTime.Today.AddDays(-8);
        var end = DateTime.Today.AddDays(-5);
        var custom = model.SelectRangeAsync(start, end);
        Wait(() => custom.IsCompleted);
        var range = query.Ranges.Last();
        var before = query.Count;
        window.Hide();
        Pump();
        Require(query.Count == before, "Hiding the window does not query.");
        query.Name = "Shown again";
        window.Show();
        Wait(() => model.Apps.SingleOrDefault()?.AppName == query.Name && !model.IsLoading);
        Pump();
        Require(query.Count == before + 1 && query.Ranges.Last() == range, "Show without activation refreshes once and preserves custom dates.");

        before = query.Count;
        query.Name = "Tray restore";
        window.Hide();
        window.RestoreAndActivate();
        Wait(() => model.Apps.SingleOrDefault()?.AppName == query.Name && !model.IsLoading);
        Pump();
        Require(query.Count == before + 1, "Show and activation coalesce into one refresh.");

        companion.Show();
        Require(companion.Activate(), "Companion window activates.");
        Wait(() => !window.IsActive);
        before = query.Count;
        query.Name = "Focused again";
        Require(window.Activate(), "Main window activates.");
        Wait(() => model.Apps.SingleOrDefault()?.AppName == query.Name && !model.IsLoading);
        Pump();
        Require(query.Count == before + 1, "Regaining focus refreshes once.");

        window.WindowState = WindowState.Minimized;
        Pump();
        before = query.Count;
        query.Name = "Restored again";
        window.WindowState = WindowState.Normal;
        window.Activate();
        Wait(() => model.Apps.SingleOrDefault()?.AppName == query.Name && !model.IsLoading);
        Pump();
        Require(query.Count == before + 1, "Restoring from minimized refreshes once.");

        foreach (var button in new[] { "TimelineButton", "SettingsButton" })
        {
            ((Button)window.FindName(button)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            before = query.Count;
            window.Hide();
            window.RestoreAndActivate();
            Pump();
            companion.Activate();
            Pump();
            window.Activate();
            Pump();
            Require(query.Count == before, $"{button} never refreshes Dashboard.");
        }

        ((Button)window.FindName("StatisticsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Wait(() => model.HasSnapshot && !model.IsLoading);
        Pump();
        before = query.Count;
        var previousName = model.Apps.Single().AppName;
        var previousStatus = model.QueryStatusText;
        query.Pending = new TaskCompletionSource<DashboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        companion.Activate();
        Pump();
        window.Activate();
        Wait(() => query.Count == before + 1 && model.IsLoading);
        Pump();
        Require(dashboardContent.IsVisible && !Descendants<Wpf.Ui.Controls.ProgressRing>(dashboard).Any(ring => ring.IsVisible), "Window refresh keeps the existing content visible without a loading spinner.");
        Require(model.Apps.Single().AppName == previousName && model.QueryStatusText == previousStatus, "Window refresh retains the old data and status until it completes.");
        Save(dashboard, "background-refresh.png");
        companion.Activate();
        Pump();
        window.Activate();
        Pump();
        Require(query.Count == before + 1, "Reactivation during an existing query does not duplicate or cancel it.");
        var pending = query.Pending;
        query.Pending = null;
        pending.SetResult(Query.Snapshot(range, "Pending completed"));
        Wait(() => !model.IsLoading);
        Require(model.Apps.Single().AppName == "Pending completed" && dashboardContent.IsVisible, "Completed background refresh publishes the new snapshot.");

        companion.Activate();
        Pump();
        before = query.Count;
        window.Activate();
        ((Button)window.FindName("TimelineButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Require(query.Count == before, "Queued refresh rechecks the current page.");

        query.Pending = new TaskCompletionSource<DashboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((Button)window.FindName("StatisticsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Wait(() => model.IsLoading);
        Pump();
        Require(!dashboardContent.IsVisible && Descendants<Wpf.Ui.Controls.ProgressRing>(dashboard).Any(ring => ring.IsVisible), "Returning from another page retains the empty loading screen and spinner.");
        Save(dashboard, "navigation-loading.png");
        pending = query.Pending;
        query.Pending = null;
        pending.SetResult(Query.Snapshot(range, "Navigation completed"));
        Wait(() => !model.IsLoading);
        Require(dashboardContent.IsVisible && model.Apps.Single().AppName == "Navigation completed", "Navigation publishes its refreshed snapshot.");
        Require(dialogs.Errors.Count == 0, "No query or settings errors.");
        window.Hide();
        companion.Close();
        Console.WriteLine("PASS: initial load; show without focus; tray show/activate coalescing; focus; minimized restore; timeline/settings guards; custom range; in-flight query; navigation before queued refresh.");
    }

    private static void CheckLoading(MainWindow window, DashboardView dashboard, DashboardViewModel model, Query query)
    {
        var content = (ScrollViewer)dashboard.FindName("DashboardScrollViewer");
        var before = query.Count;
        var previousName = model.Apps.Single().AppName;
        var previousStatus = model.QueryStatusText;
        query.Pending = new TaskCompletionSource<DashboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Hide();
        window.Show();
        Wait(() => query.Count == before + 1 && model.IsLoading);
        Pump();
        Require(content.IsVisible && !Descendants<Wpf.Ui.Controls.ProgressRing>(dashboard).Any(ring => ring.IsVisible), "Window refresh keeps the existing content visible without a loading spinner.");
        Require(model.Apps.Single().AppName == previousName && model.QueryStatusText == previousStatus, "Window refresh retains the old data and status until it completes.");
        Save(dashboard, "background-refresh.png");
        window.Hide();
        window.Show();
        Pump();
        Require(query.Count == before + 1, "A second window event does not duplicate the pending refresh.");
        var pending = query.Pending;
        query.Pending = null;
        pending.SetResult(Query.Snapshot(query.Ranges.Last(), "Background completed"));
        Wait(() => !model.IsLoading);
        Require(content.IsVisible && model.Apps.Single().AppName == "Background completed", "Completed background refresh publishes the new snapshot.");

        ((Button)window.FindName("TimelineButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        query.Pending = new TaskCompletionSource<DashboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((Button)window.FindName("StatisticsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Wait(() => model.IsLoading);
        Pump();
        Require(!content.IsVisible && Descendants<Wpf.Ui.Controls.ProgressRing>(dashboard).Any(ring => ring.IsVisible), "Returning from another page retains the empty loading screen and spinner.");
        Save(dashboard, "navigation-loading.png");
        pending = query.Pending;
        query.Pending = null;
        pending.SetResult(Query.Snapshot(query.Ranges.Last(), "Navigation completed"));
        Wait(() => !model.IsLoading);
        Require(content.IsVisible && model.Apps.Single().AppName == "Navigation completed", "Navigation publishes its refreshed snapshot.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Save(FrameworkElement view, string name)
    {
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var output = Path.GetFullPath(".scratch/live-qs-v1/evidence/23-dashboard-window-refresh");
        Directory.CreateDirectory(output);
        using var file = File.Create(Path.Combine(output, name));
        encoder.Save(file);
    }

    private static void Wait(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("WPF smoke condition did not complete.");
            Pump();
            Thread.Sleep(5);
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS: {message}");
    }

    private sealed class Query : IActivityQueryService
    {
        public string Name { get; set; } = "Initial";
        public TaskCompletionSource<DashboardSnapshot>? Pending { get; set; }
        public ConcurrentQueue<DateRange> Ranges { get; } = new();
        public int Count => Ranges.Count;
        public Task<DashboardSnapshot> GetDashboardAsync(DateRange range, CancellationToken cancellationToken = default)
        {
            Ranges.Enqueue(range);
            return Pending?.Task ?? Task.FromResult(Snapshot(range, Name));
        }
        public static DashboardSnapshot Snapshot(DateRange range, string name) => new(
            range.Start, range.End, TimeSpan.FromHours(1), TimeSpan.Zero, 1,
            [new AppUsage(name, name, "未分类", TimeSpan.FromHours(1), 1, "#007AFF")], []);
        public Task<IReadOnlyList<ActivitySegment>> GetTimelineAsync(DateRange range, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ActivitySegment>>([]);
        public Task<TimelinePage> GetTimelinePageAsync(DateRange range, int pageSize, TimelineCursor? cursor = null, CancellationToken cancellationToken = default) => Task.FromResult(new TimelinePage([], null, false));
        public Task<IReadOnlyList<ApplicationRule>> GetApplicationRulesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ApplicationRule>>([]);
    }

    private sealed class Dialogs : IUserDialogService
    {
        public List<Exception> Errors { get; } = [];
        public void ShowError(string title, Exception exception) => Errors.Add(exception);
        public bool ConfirmDeleteRange() => false;
        public string? SelectExportPath(string defaultFileName) => null;
    }

    private sealed class Startup : IStartupManager
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled) => throw new NotSupportedException();
    }

    private sealed class Paths : IAppPaths
    {
        public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"liveqs-window-refresh-{Guid.NewGuid():N}");
        public string DatabasePath => Path.Combine(DataDirectory, "smoke.db");
        public string LogPath => Path.Combine(DataDirectory, "smoke.log");
        public Paths() => Directory.CreateDirectory(DataDirectory);
    }
}
