using System.Windows;
using System.Windows.Controls;
using LiveQs.Windows.App.ViewModels;
using LiveQs.Windows.Core;

namespace LiveQs.Windows.App.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    private readonly SettingsViewModel _viewModel;
    private readonly IUserDialogService _dialogs;
    private bool _isLoadingToken;

    public SettingsView(
        IActivityRepository repository,
        IStartupManager startupManager,
        ISyncStatusService syncStatusService,
        IAppPaths paths,
        IUserDialogService dialogs)
    {
        InitializeComponent();
        _dialogs = dialogs;
        _viewModel = new SettingsViewModel(repository, startupManager, syncStatusService, paths, dialogs);
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    public Task RefreshAsync() => LoadAsync();

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        try { await LoadAsync(); }
        catch (Exception exception) { _dialogs.ShowError("读取设置失败", exception); }
    }

    private async Task LoadAsync()
    {
        await _viewModel.LoadAsync();
        _isLoadingToken = true;
        try { TokenBox.Password = _viewModel.DeviceToken; }
        finally { _isLoadingToken = false; }
    }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs args)
    {
        if (!_isLoadingToken) _viewModel.DeviceToken = TokenBox.Password;
    }
}
