using System.Collections.ObjectModel;
using LiveQs.Windows.Core;

namespace LiveQs.Windows.App.ViewModels;

public sealed class TimelineViewModel(IActivityRepository repository) : ViewModelBase
{
    private DateTime? _selectedDate = DateTime.Today;
    private bool _isEmpty = true;
    private bool _isLoading;
    private string _summary = "暂无活动";

    public ObservableCollection<TimelineRow> Rows { get; } = [];
    public DateTime? SelectedDate { get => _selectedDate; set => Set(ref _selectedDate, value); }
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var date = (SelectedDate ?? DateTime.Today).Date;
            var segments = await repository.GetTimelineAsync(DateRange.FromLocalDates(date, date));
            Rows.Clear();
            foreach (var segment in segments)
            {
                var start = segment.StartedAt.ToLocalTime();
                var end = segment.EndedAt.ToLocalTime();
                var title = string.IsNullOrWhiteSpace(segment.WindowTitle) ? segment.AppId : segment.WindowTitle;
                var flags = string.Join(" · ", new[]
                {
                    segment.IsAfk ? "AFK" : "活跃",
                    segment.IsAudioPlaying ? "音频播放" : "",
                    segment.IsFullscreen ? "全屏" : "",
                    segment.Category,
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
                Rows.Add(new TimelineRow(
                    segment.Id, start.ToString("HH:mm"), $"{start:HH:mm:ss} - {end:HH:mm:ss}",
                    segment.AppName, title, DurationText.Format(segment.Duration), flags,
                    segment.Color, segment.IsAfk));
            }
            IsEmpty = Rows.Count == 0;
            Summary = Rows.Count == 0 ? "暂无活动" : $"{Rows.Count} 个时间段";
        }
        finally { IsLoading = false; }
    }

    public async Task MoveAsync(int days)
    {
        SelectedDate = (SelectedDate ?? DateTime.Today).AddDays(days);
        await LoadAsync();
    }
}

public sealed record TimelineRow(
    long Id,
    string Time,
    string TimeRange,
    string AppName,
    string Detail,
    string Duration,
    string Flags,
    string Color,
    bool IsAfk);
