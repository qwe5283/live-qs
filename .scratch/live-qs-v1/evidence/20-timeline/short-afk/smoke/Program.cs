using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LiveQs.Windows.Controls;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Services;
using LiveQs.Windows.ViewModels;
using LiveQs.Windows.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace ShortAfkSmoke;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var output = System.IO.Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
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
        var start = new DateTimeOffset(2026, 10, 10, 10, 0, 0, TimeSpan.FromHours(8));
        var model = new TimelineViewModel(new Query([
            Record(1, start, start.AddMinutes(10), false, "main.ts"),
            Record(2, start.AddMinutes(10), start.AddMinutes(13), true, "短暂离开电脑"),
            Record(3, start.AddMinutes(13), start.AddMinutes(23), false, "Player.vue")
        ]), new Dialogs(), NullLogger<TimelineViewModel>.Instance, TimeProvider.System);
        model.SelectedDate = start.ToLocalTime().Date;
        var load = model.RefreshAsync();
        while (!load.IsCompleted)
        {
            Pump();
        }
        load.GetAwaiter().GetResult();
        Require(model.Rows.Count == 1, "Short AFK must preserve one episode.");
        var row = model.Rows[0];
        Require(row.Data.ActiveDuration == TimeSpan.FromMinutes(20), "Active time must exclude AFK.");
        Require(row.Data.Span == TimeSpan.FromMinutes(23), "The time axis must retain the break.");
        row.IsExpanded = true;
        var view = new TimelineView(model);
        var window = new Window
        {
            Content = view, Width = 1000, Height = 850, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
        };
        window.Show();
        Pump();
        var tracks = Descendants<TimelineTrack>(view).ToArray();
        Require(tracks.Length == 2, "The expanded page must render the preview and application lane.");
        foreach (var track in tracks)
        {
            var breakPoint = new Point(track.ActualWidth * 11.5 / 23, track.ActualHeight / 2);
            var foreground = track.Children.OfType<Rectangle>().Where(rectangle => rectangle.ToolTip is string).ToArray();
            Require(foreground.Length == 2, "AFK must have no foreground mark.");
            Require(foreground.All(rectangle => !rectangle.TransformToAncestor(track)
                .TransformBounds(new Rect(rectangle.RenderSize)).Contains(breakPoint)), "AFK must remain blank on both tracks.");
        }
        Save(view, System.IO.Path.Combine(output, "expanded-light.png"));
        row.SelectPositionCommand.Execute(11.5 / 23);
        Pump();
        Require(row.RawRecords.Any(record => record.Id == 2 && record.Flags.Contains("AFK")), "AFK must remain inspectable.");
        Require(Descendants<TextBlock>(view).Any(text => text.Text.Contains("短暂离开电脑")), "The actual raw record template must render AFK.");
        Save(view, System.IO.Path.Combine(output, "raw-records-light.png"));
        row.IsRawExpanded = false;
        ((ThemesDictionary)application.Resources.MergedDictionaries[0]).Theme = ApplicationTheme.Dark;
        application.Resources.MergedDictionaries[2] = Theme("Dark");
        window.Width = 720;
        Pump();
        Save(view, System.IO.Path.Combine(output, "expanded-narrow-dark.png"));
        window.Close();
        PresentationTraceSources.DataBindingSource.Flush();
        Require(bindingErrors.ToString().Length == 0, bindingErrors.ToString());
        Console.WriteLine("WPF smoke passed: one episode, 23-minute span, 20 active minutes, blank AFK on both tracks, raw AFK visible, light and narrow dark layouts, no binding errors.");
    }

    private static ActivitySegment Record(long id, DateTimeOffset start, DateTimeOffset end, bool afk, string title) =>
        new(id, start, end, "code", "Visual Studio Code", title, afk, false, false, "开发", "#007AFF");

    private static ResourceDictionary Theme(string theme) => new()
    {
        Source = new Uri($"pack://application:,,,/LiveQs.Windows;component/Styles/Theme.{theme}.xaml")
    };

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

    private sealed class Query(ActivitySegment[] records) : IActivityQueryService
    {
        public Task<TimelinePage> GetTimelinePageAsync(DateRange range, int pageSize, TimelineCursor? cursor = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new TimelinePage(records, null, false));
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
}
