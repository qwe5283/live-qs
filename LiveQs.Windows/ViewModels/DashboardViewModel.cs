using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using LiveQs.Windows.Core;
using LiveQs.Windows.Services;

namespace LiveQs.Windows.ViewModels;

public sealed class DashboardViewModel : ViewModelBase
{
    private readonly IActivityRepository _repository;
    private readonly IUserDialogService _dialogs;
    private DateTime? _startDate = DateTime.Today;
    private DateTime? _endDate = DateTime.Today;
    private string _activeText = "0分钟";
    private string _afkText = "0分钟";
    private string _appCountText = "0";
    private string _rangeText = "今天";
    private bool _isEmpty = true;
    private bool _isLoading;

    public DashboardViewModel(IActivityRepository repository, IUserDialogService dialogs)
    {
        _repository = repository;
        _dialogs = dialogs;
        LoadCommand = new AsyncRelayCommand(() => RunAsync(LoadAsync));
        TodayCommand = new AsyncRelayCommand(() => RunAsync(() => SelectPresetAsync(1)));
        WeekCommand = new AsyncRelayCommand(() => RunAsync(() => SelectPresetAsync(7)));
        MonthCommand = new AsyncRelayCommand(() => RunAsync(() => SelectPresetAsync(30)));
    }

    public ObservableCollection<AppUsage> Apps { get; } = [];
    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand TodayCommand { get; }
    public IAsyncRelayCommand WeekCommand { get; }
    public IAsyncRelayCommand MonthCommand { get; }
    public DateTime? StartDate { get => _startDate; set => Set(ref _startDate, value); }
    public DateTime? EndDate { get => _endDate; set => Set(ref _endDate, value); }
    public string ActiveText { get => _activeText; private set => Set(ref _activeText, value); }
    public string AfkText { get => _afkText; private set => Set(ref _afkText, value); }
    public string AppCountText { get => _appCountText; private set => Set(ref _appCountText, value); }
    public string RangeText { get => _rangeText; private set => Set(ref _rangeText, value); }
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var start = (StartDate ?? DateTime.Today).Date;
            var end = (EndDate ?? start).Date;
            if (end < start) (start, end) = (end, start);
            var snapshot = await _repository.GetDashboardAsync(DateRange.FromLocalDates(start, end));
            Apps.Clear();
            foreach (var app in snapshot.Apps) Apps.Add(app);
            ActiveText = DurationText.Format(snapshot.ActiveDuration);
            AfkText = DurationText.Format(snapshot.AfkDuration);
            AppCountText = snapshot.AppCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
            RangeText = start == end ? start.ToString("M月d日") : $"{start:M月d日} - {end:M月d日}";
            IsEmpty = Apps.Count == 0;
        }
        finally { IsLoading = false; }
    }

    public async Task SelectPresetAsync(int days)
    {
        EndDate = DateTime.Today;
        StartDate = DateTime.Today.AddDays(-(Math.Max(1, days) - 1));
        await LoadAsync();
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { _dialogs.ShowError("查询失败", exception); }
    }
}
