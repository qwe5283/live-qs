using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using LiveQs.Windows.Core.Common;

namespace LiveQs.Windows.ViewModels;

public sealed partial class SettingsViewModel
{
    public async Task<int> DeleteSelectedRangeAsync()
    {
        var range = SelectedMaintenanceRange();
        var deleted = await _maintenance.DeleteRangeAsync(range);
        StatusText = $"已删除 {deleted} 个时间段";
        return deleted;
    }

    public async Task ExportAsync(string path)
    {
        await _maintenance.ExportCsvAsync(path, SelectedMaintenanceRange());
        StatusText = "CSV 导出完成";
    }

    private async Task OptimizeCoreAsync()
    {
        await _maintenance.OptimizeAsync();
        StatusText = "数据库维护完成";
    }

    private DateRange SelectedMaintenanceRange()
    {
        var start = (MaintenanceStartDate ?? Today).Date;
        var end = (MaintenanceEndDate ?? start).Date;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return DateRange.FromLocalDates(start, end);
    }

    private async Task ExportSelectedRangeAsync()
    {
        var path = _dialogs.SelectExportPath($"LiveQs-{Today:yyyy-MM-dd}.csv");
        if (path is not null)
        {
            await RunAsync(() => ExportAsync(path), "导出失败");
        }
    }

    private async Task DeleteSelectedRangeWithConfirmationAsync()
    {
        if (_dialogs.ConfirmDeleteRange())
        {
            await RunAsync(async () => { _ = await DeleteSelectedRangeAsync(); }, "删除失败");
        }
    }

    [RelayCommand]
    private Task ExportAsync() => ExportSelectedRangeAsync();

    [RelayCommand]
    private Task DeleteAsync() => DeleteSelectedRangeWithConfirmationAsync();

    [RelayCommand]
    private Task OptimizeAsync() => RunAsync(OptimizeCoreAsync, "数据库维护失败");

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.DataDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _dialogs.ShowError("无法打开目录", exception);
        }
    }

}
