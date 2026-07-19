using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LiveQs.Windows.App.ViewModels;
using LiveQs.Windows.Core;
using Microsoft.Win32;

namespace LiveQs.Windows.App.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    private readonly SettingsViewModel _viewModel;
    private readonly IAppPaths _paths;

    public SettingsView(IActivityRepository repository, IStartupManager startupManager, ISyncStatusService syncStatusService, IAppPaths paths)
    {
        InitializeComponent();
        _paths = paths;
        _viewModel = new SettingsViewModel(repository, startupManager, syncStatusService, paths);
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    public Task RefreshAsync() => LoadAsync();

    private async void OnLoaded(object sender, RoutedEventArgs args) => await RunAsync(LoadAsync, "读取设置失败");

    private async Task LoadAsync()
    {
        await _viewModel.LoadAsync();
        TokenBox.Password = _viewModel.DeviceToken;
    }

    private async void Save_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.DeviceToken = TokenBox.Password;
        await RunAsync(_viewModel.SaveAsync, "保存设置失败");
    }

    private async void Export_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出活动数据",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"LiveQs-{DateTime.Today:yyyy-MM-dd}.csv",
            AddExtension = true,
        };
        if (dialog.ShowDialog() == true)
            await RunAsync(() => _viewModel.ExportAsync(dialog.FileName), "导出失败");
    }

    private async void Delete_Click(object sender, RoutedEventArgs args)
    {
        var answer = MessageBox.Show("将永久删除选定日期范围内的本地活动数据。是否继续？", "删除本地数据",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        await RunAsync(async () => { _ = await _viewModel.DeleteSelectedRangeAsync(); }, "删除失败");
    }

    private async void Optimize_Click(object sender, RoutedEventArgs args) =>
        await RunAsync(_viewModel.OptimizeAsync, "数据库维护失败");

    private void OpenDataFolder_Click(object sender, RoutedEventArgs args)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.DataDirectory}\"") { UseShellExecute = true }); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { MessageBox.Show(exception.Message, "无法打开目录", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static async Task RunAsync(Func<Task> action, string title)
    {
        try { await action(); }
        catch (Exception exception) { MessageBox.Show(exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
