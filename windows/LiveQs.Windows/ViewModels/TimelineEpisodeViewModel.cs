using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Common;
using Wpf.Ui.Controls;

namespace LiveQs.Windows.ViewModels;

public sealed partial class TimelineEpisodeViewModel : ObservableObject
{
    public TimelineEpisode Data
    {
        get;
    }
    public string Title => Data.IsGap ? "无活动记录" : Data.IsAfk ? "离开电脑" :
        Data.Applications.Count == 1 ? Data.Applications[0].AppName : "多应用活动";
    public bool HasApplicationIcon => HasActiveApplications && Data.Applications.Count == 1;
    public SymbolRegular TitleSymbol => Data.IsGap ? SymbolRegular.EyeOff24 :
        Data.IsAfk ? SymbolRegular.DrinkCoffee24 : SymbolRegular.Apps24;
    public string ExecutablePath => HasApplicationIcon
        ? Data.Slices.LastOrDefault(slice => !slice.Record.IsAfk &&
            !string.IsNullOrWhiteSpace(slice.Record.ExecutablePath))?.Record.ExecutablePath ?? ""
        : "";
    public string StartTime => FormatLocalTime(Data.StartedAt);
    public string EndTime => FormatLocalTime(Data.EndedAt);
    public string MiddleTime => FormatLocalTime(Data.StartedAt + Data.Span / 2);
    public string TimeRange => FormatRange(Data.StartedAt, Data.EndedAt);
    public bool HasDetails => !Data.IsGap;
    public bool HasActiveApplications => !Data.IsGap && !Data.IsAfk;
    public bool HasChanges => Data.Slices.Count(slice => !slice.Record.IsAfk) > 1;
    public bool HasFlags => Flags.Length > 0;
    public bool HasCoverageDifference => Data.IsGap || Data.Span != Data.ObservedDuration ||
        (!Data.IsAfk && Data.ActiveDuration != Data.ObservedDuration);
    public bool IsPartial => Data.IsPartial;
    public string Summary => Data.IsGap ? $"未观测 {DurationText.Format(Data.Span)}" :
        $"{(Data.IsAfk ? "AFK" : "活跃")} {DurationText.Format(Data.IsAfk ? Data.ObservedDuration : Data.ActiveDuration)} · {Data.Slices.Count} 条原始记录";
    public string Coverage => Data.IsGap ? "此区间没有活动记录" :
        $"跨度 {DurationText.Format(Data.Span)} · 记录覆盖 {DurationText.Format(Data.ObservedDuration)}" +
        (!Data.IsAfk && Data.ObservedDuration > Data.ActiveDuration
            ? $" · AFK {DurationText.Format(Data.ObservedDuration - Data.ActiveDuration)}" : "");
    public string Changes => Data.TitleChanges > 0
        ? $"{Data.ApplicationSwitches} 次应用切换 · {Data.TitleChanges} 次同应用标题变化"
        : Data.ApplicationSwitches > 0 ? $"{Data.ApplicationSwitches} 次应用切换" : $"{Data.Slices.Count(slice => !slice.Record.IsAfk)} 条同应用记录已折叠";
    public string Flags => string.Join(" · ", new[]
    {
        Data.Slices.Any(slice => slice.Record.IsFullscreen) ? "含全屏记录" : "",
        Data.Slices.Any(slice => slice.Record.IsAudioPlaying) ? "含音频播放" : "",
    }.Where(value => value.Length > 0));
    public IReadOnlyList<TimelineMark> Marks
    {
        get;
    }
    public IReadOnlyList<TimelineApplicationRow> Applications
    {
        get;
    }
    public IReadOnlyList<TimelineWindow> Windows
    {
        get;
    }

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
        Marks = data.Slices
            .Where(slice => !slice.Record.IsAfk && slice.Duration > TimeSpan.Zero)
            .Select(CreateActivityMark)
            .ToArray();
        Applications = data.Applications.Select(CreateApplicationRow).ToArray();
        Windows = CreateInspectionWindows();
        SelectedWindow = Windows.LastOrDefault();
    }

    private TimelineMark CreateActivityMark(ActivitySlice slice) => new(
        GetPositionFraction(slice.StartedAt),
        GetPositionFraction(slice.EndedAt),
        slice.Record.Color,
        $"{slice.Record.AppName} · {FormatRange(slice.StartedAt, slice.EndedAt)} · {DurationText.Format(slice.Duration)}");

    private TimelineApplicationRow CreateApplicationRow(TimelineApplication application)
    {
        var marks = Data.Slices
            .Where(slice => !slice.Record.IsAfk && slice.Duration > TimeSpan.Zero &&
                string.Equals(slice.Record.AppId, application.AppId, StringComparison.OrdinalIgnoreCase))
            .Select(slice => new TimelineMark(
                GetPositionFraction(slice.StartedAt),
                GetPositionFraction(slice.EndedAt),
                application.Color,
                $"{application.AppName} · {FormatRange(slice.StartedAt, slice.EndedAt)}"))
            .ToArray();
        return new TimelineApplicationRow(
            application.AppId,
            application.AppName,
            application.Color,
            DurationText.Format(application.Duration),
            marks);
    }

    private IReadOnlyList<TimelineWindow> CreateInspectionWindows()
    {
        if (Data.IsGap)
        {
            return [];
        }

        var windows = new List<TimelineWindow>();
        var windowDuration = TimeSpan.FromMinutes(5);
        for (var start = Data.StartedAt; start < Data.EndedAt; start += windowDuration)
        {
            var end = start + windowDuration;
            if (end > Data.EndedAt)
            {
                end = Data.EndedAt;
            }

            windows.Add(new TimelineWindow(start, end, FormatRange(start, end)));
        }
        return windows;
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
        if (value is null)
        {
            return;
        }

        SelectionStart = GetPositionFraction(value.StartedAt);
        SelectionEnd = GetPositionFraction(value.EndedAt);
        RawRecords = Data.Slices
            .Select(slice => slice.Record)
            .Where(record => record.StartedAt < value.EndedAt && record.EndedAt > value.StartedAt)
            .OrderByDescending(record => record.StartedAt)
            .ThenByDescending(record => record.Id)
            .Select(CreateRawRow)
            .ToArray();
        RawSummary = $"{value.Label} · {RawRecords.Count} 条原始记录";
    }

    private static TimelineRawRow CreateRawRow(ActivitySegment record) => new(
        record.Id,
        FormatRange(record.StartedAt, record.EndedAt),
        record.AppName,
        string.IsNullOrWhiteSpace(record.WindowTitle) ? "未保留窗口标题" : record.WindowTitle,
        FormatExactDuration(record.Duration),
        FormatRecordFlags(record));

    private static string FormatRecordFlags(ActivitySegment record) => string.Join(" · ", new[]
    {
        record.IsAfk ? "AFK" : "活跃",
        record.IsFullscreen ? "全屏" : "",
        record.IsAudioPlaying ? "音频播放" : "",
        record.Category,
    }.Where(flag => !string.IsNullOrWhiteSpace(flag)));

    private double GetPositionFraction(DateTimeOffset at) => Data.Span.Ticks > 0
        ? Math.Clamp((double)(at - Data.StartedAt).Ticks / Data.Span.Ticks, 0, 1) : 0;

    private static string FormatLocalTime(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm");

    private static string FormatExactDuration(TimeSpan duration) => duration.TotalMinutes >= 1 && duration.Seconds > 0
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
