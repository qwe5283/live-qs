using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveQs.Windows.Controls;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Services;
using LiveQs.Windows.Infrastructure.Configuration;
using LiveQs.Windows.Infrastructure.Persistence.Sqlite;
using LiveQs.Windows.ViewModels;
using LiveQs.Windows.Views;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
    private static readonly DateTime Date = new(2026, 10, 9);
    private static readonly DateTimeOffset Start = new(Date.AddHours(14), TimeZoneInfo.Local.GetUtcOffset(Date));
    private static readonly BindingErrors Errors = new();
    private static readonly string DirectoryPath = Path.GetFullPath(".scratch/live-qs-v1/evidence/20-timeline");

    [STAThread]
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(DirectoryPath);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(Errors);
        SetTheme(application, "Light");
        if (args.Contains("--live"))
        {
            var paths = new AppPaths();
            if (!File.Exists(paths.DatabasePath)) throw new InvalidOperationException("An existing activity database is required.");
            var query = new SqliteActivityRepository(paths, TimeProvider.System);
            var live = new TimelineViewModel(query, new Dialogs(), NullLogger<TimelineViewModel>.Instance, TimeProvider.System) { SelectedDate = Date };
            live.RefreshAsync().GetAwaiter().GetResult();
            var liveView = new TimelineView(live);
            var liveWindow = new Window { Style = new Style(typeof(Window)), Content = liveView, Width = 860, Height = 900,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000 };
            liveWindow.Show();
            Pump();
            Capture(liveView, "live-data.png");
            Console.WriteLine(live.Summary);
            liveWindow.Close();
            application.Shutdown();
            return;
        }
        var model = Model(ExampleRecords());
        var view = new TimelineView(model);
        var window = new Window { Style = new Style(typeof(Window)), Content = view, Width = 860, Height = 820, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -30000, Top = -30000 };
        window.Show();
        Pump();
        Require(!Descendants<Button>(view).Any(button => button.Command is not null && model.Rows.Any(row => button.Command == row.ToggleRawRecordsCommand)),
            "Collapsed episodes must not construct the raw-record controls.");
        Capture(view, "collapsed-light.png");

        var development = model.Rows.First(row => row.Data.Applications.Count == 4);
        var button = Descendants<Button>(view).First(button => ReferenceEquals(button.DataContext, development) && button.Command == development.ToggleExpandedCommand);
        button.Command!.Execute(null);
        Pump();
        var lane = Descendants<TimelineTrack>(view).First(track => track.Height == 16);
        Require(lane.SelectPositionCommand == development.SelectPositionCommand, "Application lane selection command must be bound to the episode.");
        Require(Math.Abs(lane.SelectionStart - development.SelectionStart) < 0.0001, "Lane selection must follow the selected window.");
        Require(lane.Children.Count > 1 && lane.Children[0].RenderSize.Width > 0, "Application marks must have a proportional rendered width.");
        Capture(view, "expanded-light.png");

        lane.SelectPositionCommand!.Execute(0.1d);
        Pump();
        Require(development.IsRawExpanded && development.RawRecords.Count == 7, "Clicking a lane must open the corresponding seven raw records.");
        var picker = Descendants<ComboBox>(view).First(picker => picker.ItemsSource == development.Windows);
        picker.SelectedIndex = 0;
        Pump();
        Require(development.SelectedWindow == development.Windows[0], "The time picker must update the ViewModel.");
        window.Height = 1380;
        Pump();
        Capture(view, "raw-records-light.png");

        development.IsRawExpanded = false;
        window.Width = 680;
        window.Height = 940;
        SetTheme(application, "Dark");
        Pump();
        Capture(view, "expanded-narrow-dark.png");

        development.IsExpanded = false;
        window.Width = 860;
        window.Height = 820;
        Pump();
        Capture(view, "collapsed-dark.png");

        SetTheme(application, "Light");
        var missing = Model([
            Record(1, Start, Start.AddMinutes(2), "code", "VS Code", "main.ts", "#007AFF"),
            Record(2, Start.AddMinutes(7), Start.AddMinutes(9), "edge", "Microsoft Edge", "MDN Web Docs", "#159B7B")
        ]);
        window.Content = new TimelineView(missing);
        Pump();
        Capture((FrameworkElement)window.Content, "gap-light.png");
        window.Content = new TimelineView(Model([]));
        Pump();
        Capture((FrameworkElement)window.Content, "empty-light.png");

        if (Errors.Messages.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, Errors.Messages));
        Console.WriteLine("WPF smoke passed: collapsed/expanded/raw records, time selection, narrow/dark, gap/empty; no binding errors.");
        window.Close();
        application.Shutdown();
    }

    private static TimelineViewModel Model(ActivitySegment[] records)
    {
        var model = new TimelineViewModel(new Query(records), new Dialogs(), NullLogger<TimelineViewModel>.Instance, TimeProvider.System) { SelectedDate = Date };
        model.RefreshAsync().GetAwaiter().GetResult();
        return model;
    }

    private static void SetTheme(Application application, string theme)
    {
        application.Resources = (ResourceDictionary)XamlReader.Parse($"""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:ui="clr-namespace:Wpf.Ui.Markup;assembly=Wpf.Ui">
              <ResourceDictionary.MergedDictionaries>
                <ui:ThemesDictionary Theme="{theme}" />
                <ui:ControlsDictionary />
                <ResourceDictionary Source="pack://application:,,,/LiveQs.Windows;component/Styles/Theme.{theme}.xaml" />
                <ResourceDictionary Source="pack://application:,,,/LiveQs.Windows;component/Styles/Theme.xaml" />
              </ResourceDictionary.MergedDictionaries>
            </ResourceDictionary>
            """);
    }

    private static void Capture(FrameworkElement view, string name)
    {
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(DirectoryPath, name));
        encoder.Save(output);
        Console.WriteLine($"{name}: {view.ActualWidth} x {view.ActualHeight}");
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static ActivitySegment[] ExampleRecords()
    {
        var result = new List<ActivitySegment>
        {
            Record(1, Start, Start.AddHours(1), "edge", "Microsoft Edge", "视频播放页面", "#159B7B") with { IsFullscreen = true },
            Record(2, Start.AddHours(1), Start.AddMinutes(70), "edge", "Microsoft Edge", "视频播放页面", "#159B7B") with { IsAfk = true },
        };
        var parts = new (string App, string Name, string Color, int Seconds, string Title)[]
        {
            ("code", "VS Code", "#007AFF", 80, "main.ts — demo-project — Visual Studio Code"),
            ("code", "VS Code", "#007AFF", 40, "Player.vue — demo-project — Visual Studio Code"),
            ("terminal", "Terminal", "#9254C8", 45, "npm run dev — Windows Terminal"),
            ("edge", "Microsoft Edge", "#159B7B", 60, "localhost:5173 — Microsoft Edge"),
            ("edge", "Microsoft Edge", "#159B7B", 30, "MDN Web Docs — Microsoft Edge"),
            ("explorer", "Explorer", "#D87E25", 15, "demo-project — 文件资源管理器"),
            ("code", "VS Code", "#007AFF", 30, "Player.test.ts — demo-project — Visual Studio Code"),
        };
        var at = Start.AddMinutes(70);
        for (var cycle = 0; cycle < 12; cycle++)
        {
            foreach (var part in parts)
            {
                result.Add(Record(result.Count + 1, at, at.AddSeconds(part.Seconds), part.App, part.Name, part.Title, part.Color));
                at = at.AddSeconds(part.Seconds);
            }
        }
        return result.ToArray();
    }

    private static ActivitySegment Record(long id, DateTimeOffset start, DateTimeOffset end, string app, string name, string title, string color) =>
        new(id, start, end, app, name, title, false, false, false, "未分类", color);

    private sealed class Query(ActivitySegment[] records) : IActivityQueryService
    {
        public Task<TimelinePage> GetTimelinePageAsync(DateRange range, int pageSize, TimelineCursor? cursor = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TimelinePage(records, null, false));
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

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrEmpty(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
