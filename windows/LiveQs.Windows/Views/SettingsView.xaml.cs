using System.Windows;
using System.Windows.Controls;
using LiveQs.Windows.Services;
using LiveQs.Windows.ViewModels;

namespace LiveQs.Windows.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    private readonly IUserDialogService _dialogs;
    private bool _isLoadingToken;

    public SettingsViewModel ViewModel { get; }

    public SettingsView(SettingsViewModel viewModel, IUserDialogService dialogs)
    {
        InitializeComponent();
        _dialogs = dialogs;
        DataContext = ViewModel = viewModel;
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
        await ViewModel.LoadAsync();
        _isLoadingToken = true;
        try { TokenBox.Password = ViewModel.DeviceToken; }
        finally { _isLoadingToken = false; }
    }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs args)
    {
        if (!_isLoadingToken) ViewModel.DeviceToken = TokenBox.Password;
    }
}
