using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Common;

namespace LiveQs.Windows.ViewModels;

public sealed partial class TimelineEpisodeViewModel : ObservableObject
{
    public TimelineEpisode Data { get; }
    public string Title => Data.IsGap ? "无活动记录" : Data.IsAfk ? "离开电脑" :
        Data.Applications.Count == 1 ? Data.Applications[0].AppName : "多应用活动";
    public string StartTime => LocalTime(Data.StartedAt);
    public string EndTime => LocalTime(Data.EndedAt);
    public string MiddleTime => LocalTime(Data.StartedAt + Data.Span / 2);
    public string TimeRange => FormatRange(Data.StartedAt, Data.EndedAt);
    public bool HasDetails => !Data.IsGap;
    public bool HasActiveApplications => !Data.IsGap && !Data.IsAfk;
    public bool HasChanges => Data.Slices.Count > 1;
    public bool HasFlags => Flags.Length > 0;
    public bool HasCoverageDifference => Data.IsGap || Data.Span != Data.ObservedDuration;
    public bool IsPartial => Data.IsPartial;
    public string Summary => Data.IsGap ? $"未观测 {DurationText.Format(Data.Span)}" :
        $"{(Data.IsAfk ? "AFK" : "活跃")} {DurationText.Format(Data.IsAfk ? Data.ObservedDuration : Data.ActiveDuration)} · {Data.Slices.Count} 条原始记录";
    public string Coverage => Data.IsGap ? "此区间没有活动记录" :
        $"跨度 {DurationText.Format(Data.Span)} · 记录覆盖 {DurationText.Format(Data.ObservedDuration)}";
    public string Changes => Data.TitleChanges > 0
        ? $"{Data.ApplicationSwitches} 次应用切换 · {Data.TitleChanges} 次同应用标题变化"
        : Data.ApplicationSwitches > 0 ? $"{Data.ApplicationSwitches} 次应用切换" : $"{Data.Slices.Count} 条同应用记录已折叠";
    public string Flags => string.Join(" · ", new[]
    {
        Data.Slices.Any(slice => slice.Record.IsFullscreen) ? "含全屏记录" : "",
        Data.Slices.Any(slice => slice.Record.IsAudioPlaying) ? "含音频播放" : "",
    }.Where(value => value.Length > 0));
    public IReadOnlyList<TimelineMark> Marks { get; }
    public IReadOnlyList<TimelineApplicationRow> Applications { get; }
    public IReadOnlyList<TimelineWindow> Windows { get; }

    [ObservableProperty]
    private bool _isExpanded;
    [ObservableProperty]
    private bool _isRawExpanded;
    [ObservableProperty]
    private TimelineWindow? _selectedWindow;
    [ObservableProperty]
    private IReadOnlyList<TimelineRawRow> _rawRecords = [];
    [ObservableProperty]
    private string _rawSummary = "";
    [ObservableProperty]
    private double _selectionStart;
    [ObservableProperty]
    private double _selectionEnd;

    public TimelineEpisodeViewModel(TimelineEpisode data)
    {
        Data = data;
        Marks = data.Slices.Where(slice => slice.Duration > TimeSpan.Zero).Select(slice => new TimelineMark(
            Fraction(slice.StartedAt), Fraction(slice.EndedAt), slice.Record.Color,
            $"{slice.Record.AppName} · {FormatRange(slice.StartedAt, slice.EndedAt)} · {DurationText.Format(slice.Duration)}")).ToArray();
        Applications = data.Applications.Select(application => new TimelineApplicationRow(
            application.AppId, application.AppName, application.Color, DurationText.Format(application.Duration),
            data.Slices.Where(slice => string.Equals(slice.Record.AppId, application.AppId, StringComparison.OrdinalIgnoreCase) && slice.Duration > TimeSpan.Zero)
                .Select(slice => new TimelineMark(Fraction(slice.StartedAt), Fraction(slice.EndedAt), application.Color,
                    $"{application.AppName} · {FormatRange(slice.StartedAt, slice.EndedAt)}")).ToArray())).ToArray();
        var windows = new List<TimelineWindow>();
        if (!data.IsGap)
        {
            for (var start = data.StartedAt; start < data.EndedAt; start += TimeSpan.FromMinutes(5))
            {
                var end = start + TimeSpan.FromMinutes(5);
                if (end > data.EndedAt) end = data.EndedAt;
                windows.Add(new TimelineWindow(start, end, FormatRange(start, end)));
            }
        }
        Windows = windows;
        SelectedWindow = windows.LastOrDefault();
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleRawRecords() => IsRawExpanded = !IsRawExpanded;

    [RelayCommand]
    private void SelectPosition(double fraction)
    {
        var at = Data.StartedAt + TimeSpan.FromTicks((long)(Data.Span.Ticks * Math.Clamp(fraction, 0, 1)));
        SelectedWindow = Windows.FirstOrDefault(window => window.EndedAt > at) ?? Windows.LastOrDefault();
        IsExpanded = true;
        IsRawExpanded = true;
    }

    partial void OnSelectedWindowChanged(TimelineWindow? value)
    {
        if (value is null) return;
        SelectionStart = Fraction(value.StartedAt);
        SelectionEnd = Fraction(value.EndedAt);
        RawRecords = Data.Slices.Where(slice => slice.Record.StartedAt < value.EndedAt && slice.Record.EndedAt > value.StartedAt)
            .OrderByDescending(slice => slice.Record.StartedAt).ThenByDescending(slice => slice.Record.Id)
            .Select(slice => new TimelineRawRow(slice.Record.Id, FormatRange(slice.Record.StartedAt, slice.Record.EndedAt),
                slice.Record.AppName, string.IsNullOrWhiteSpace(slice.Record.WindowTitle) ? "未保留窗口标题" : slice.Record.WindowTitle,
                ExactDuration(slice.Record.Duration), string.Join(" · ", new[]
                {
                    slice.Record.IsAfk ? "AFK" : "活跃",
                    slice.Record.IsFullscreen ? "全屏" : "",
                    slice.Record.IsAudioPlaying ? "音频播放" : "",
                    slice.Record.Category,
                }.Where(flag => !string.IsNullOrWhiteSpace(flag))))).ToArray();
        RawSummary = $"{value.Label} · {RawRecords.Count} 条原始记录";
    }

    private double Fraction(DateTimeOffset at) => Data.Span.Ticks > 0
        ? Math.Clamp((double)(at - Data.StartedAt).Ticks / Data.Span.Ticks, 0, 1) : 0;

    private static string LocalTime(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm");

    private static string ExactDuration(TimeSpan duration) => duration.TotalMinutes >= 1 && duration.Seconds > 0
        ? $"{DurationText.Format(duration)} {duration.Seconds}秒" : DurationText.Format(duration);

    private static string FormatRange(DateTimeOffset start, DateTimeOffset end)
    {
        var localStart = start.ToLocalTime();
        var localEnd = end.ToLocalTime();
        var includeOffset = localStart.Offset != localEnd.Offset;
        var startFormat = includeOffset ? "HH:mm:ss zzz" : "HH:mm:ss";
        var endFormat = localStart.Date != localEnd.Date
            ? includeOffset ? "MM-dd HH:mm:ss zzz" : "MM-dd HH:mm:ss" : startFormat;
        return $"{localStart.ToString(startFormat)} – {localEnd.ToString(endFormat)}";
    }
}

public sealed record TimelineMark(double Start, double End, string Color, string Description);
public sealed record TimelineApplicationRow(string AppId, string Name, string Color, string Duration, IReadOnlyList<TimelineMark> Marks);
public sealed record TimelineWindow(DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Label);
public sealed record TimelineRawRow(long Id, string TimeRange, string AppName, string Title, string Duration, string Flags);
