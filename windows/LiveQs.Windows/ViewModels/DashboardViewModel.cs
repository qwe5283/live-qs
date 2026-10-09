using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Services;

namespace LiveQs.Windows.ViewModels;

public sealed partial class DashboardViewModel : ViewModelBase
{
    private static readonly string[] _topAppColors = ["#007AFF", "#FF7A00", "#1DB954", "#8B5CF6", "#E94D73"];
    private readonly IActivityQueryService _queryService;
    private readonly IUserDialogService _dialogs;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _loadCancellation;
    private long _requestVersion;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeText))]
    private DateTime? _startDate;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeText))]
    private DateTime? _endDate;
    [ObservableProperty]
    private string _activeText = "0分钟";
    [ObservableProperty]
    private string _afkText = "0分钟";
    [ObservableProperty]
    private string _appCountText = "0";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTodayRange), nameof(IsWeekRange), nameof(IsMonthRange))]
    private int _presetDays = 1;
    [ObservableProperty]
    private bool _isEmpty = true;
    [ObservableProperty]
    private bool _isLoading;
    [ObservableProperty]
    private bool _hasSnapshot;
    [ObservableProperty]
    private bool _hasQueryError;
    [ObservableProperty]
    private string _queryStatusText = "正在加载统计…";
    [ObservableProperty]
    private IReadOnlyList<ActiveTimeBucket> _activeBuckets = [];
    [ObservableProperty]
    private bool _isHourlyDistribution = true;
    [ObservableProperty]
    private string _distributionText = "每小时活跃时长";
    [ObservableProperty]
    private IReadOnlyList<AppUsage> _topApps = [];

    public DashboardViewModel(IActivityQueryService queryService, IUserDialogService dialogs, TimeProvider timeProvider)
    {
        _queryService = queryService;
        _dialogs = dialogs;
        _timeProvider = timeProvider;
        _startDate = Today;
        _endDate = Today;
    }

    public ObservableCollection<AppUsage> Apps { get; } = [];
    public bool IsTodayRange => PresetDays == 1;
    public bool IsWeekRange => PresetDays == 7;
    public bool IsMonthRange => PresetDays == 30;
    public string RangeText => Formatting.DateRangeText.Format(StartDate, EndDate, Today.Year);

    public async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref _requestVersion);
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _loadCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        var token = cancellation.Token;
        if (PresetDays > 0)
        {
            StartDate = Today.AddDays(-(PresetDays - 1));
            EndDate = Today;
        }
        IsLoading = true;
        HasSnapshot = false;
        HasQueryError = false;
        QueryStatusText = "正在更新统计…";
        try
        {
            var start = (StartDate ?? Today).Date;
            var end = (EndDate ?? start).Date;
            if (end < start)
            {
                (start, end) = (end, start);
            }

            var range = DateRange.FromLocalDates(start, end);
            var snapshot = await Task.Run(() => _queryService.GetDashboardAsync(range, token), token);
            if (version != Volatile.Read(ref _requestVersion) || token.IsCancellationRequested)
            {
                return;
            }

            ActiveBuckets = snapshot.ActiveBuckets;
            IsHourlyDistribution = start == end;
            DistributionText = IsHourlyDistribution ? "每小时活跃时长" : "每日活跃时长";
            var apps = snapshot.Apps.Select((app, index) => index < _topAppColors.Length
                ? app with
                {
                    Color = _topAppColors[index]
                }
                : app).ToArray();
            Apps.Clear();
            foreach (var app in apps)
            {
                Apps.Add(app);
            }

            TopApps = apps.Take(_topAppColors.Length).ToArray();
            ActiveText = DurationText.Format(snapshot.ActiveDuration);
            AfkText = DurationText.Format(snapshot.AfkDuration);
            AppCountText = snapshot.AppCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
            IsEmpty = Apps.Count == 0;
            HasSnapshot = true;
            QueryStatusText = DistributionText;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version != Volatile.Read(ref _requestVersion))
            {
                return;
            }

            HasQueryError = true;
            QueryStatusText = "查询失败，请重试或选择其他日期范围。";
            _dialogs.ShowError("查询失败", exception);
        }
        finally
        {
            if (version == Volatile.Read(ref _requestVersion))
            {
                IsLoading = false;
            }
        }
    }

    public Task SelectRangeAsync(DateTime start, DateTime end)
    {
        start = start.Date;
        end = end.Date;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        PresetDays = end == Today && (end - start).Days + 1 is 1 or 7 or 30 ? (end - start).Days + 1 : 0;
        StartDate = start;
        EndDate = end;
        return RefreshAsync();
    }

    private async Task SelectPresetAsync(int days)
    {
        PresetDays = days;
        EndDate = Today;
        StartDate = Today.AddDays(-(Math.Max(1, days) - 1));
        await RefreshAsync();
    }

    private DateTime Today => _timeProvider.GetLocalNow().Date;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task LoadAsync() => RefreshAsync();

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task TodayAsync() => SelectPresetAsync(1);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task WeekAsync() => SelectPresetAsync(7);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task MonthAsync() => SelectPresetAsync(30);
}
