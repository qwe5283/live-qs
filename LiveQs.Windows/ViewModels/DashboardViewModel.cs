using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveQs.Windows.Core;
using LiveQs.Windows.Services;

namespace LiveQs.Windows.ViewModels;

public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly IActivityRepository _repository;
    private readonly IUserDialogService _dialogs;
    [ObservableProperty]
    private DateTime? _startDate = DateTime.Today;
    [ObservableProperty]
    private DateTime? _endDate = DateTime.Today;
    [ObservableProperty]
    private string _activeText = "0分钟";
    [ObservableProperty]
    private string _afkText = "0分钟";
    [ObservableProperty]
    private string _appCountText = "0";
    [ObservableProperty]
    private string _rangeText = "今天";
    [ObservableProperty]
    private bool _isEmpty = true;
    [ObservableProperty]
    private bool _isLoading;

    public DashboardViewModel(IActivityRepository repository, IUserDialogService dialogs)
    {
        _repository = repository;
        _dialogs = dialogs;
    }

    public ObservableCollection<AppUsage> Apps { get; } = [];

    public async Task RefreshAsync()
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

    private async Task SelectPresetAsync(int days)
    {
        EndDate = DateTime.Today;
        StartDate = DateTime.Today.AddDays(-(Math.Max(1, days) - 1));
        await RefreshAsync();
    }

    [RelayCommand]
    private Task LoadAsync() => RunAsync(RefreshAsync);

    [RelayCommand]
    private Task TodayAsync() => RunAsync(() => SelectPresetAsync(1));

    [RelayCommand]
    private Task WeekAsync() => RunAsync(() => SelectPresetAsync(7));

    [RelayCommand]
    private Task MonthAsync() => RunAsync(() => SelectPresetAsync(30));

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { _dialogs.ShowError("查询失败", exception); }
    }
}
