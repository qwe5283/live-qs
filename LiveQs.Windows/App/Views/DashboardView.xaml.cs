using System.Windows;
using System.Windows.Controls;
using LiveQs.Windows.App.ViewModels;
using LiveQs.Windows.Core;

namespace LiveQs.Windows.App.Views;

public partial class DashboardView : System.Windows.Controls.UserControl
{
    private readonly DashboardViewModel _viewModel;

    public DashboardView(IActivityRepository repository)
    {
        InitializeComponent();
        _viewModel = new DashboardViewModel(repository);
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    public Task RefreshAsync() => _viewModel.LoadAsync();
    private async void OnLoaded(object sender, RoutedEventArgs args) => await RunAsync(_viewModel.LoadAsync);
    private async void Today_Click(object sender, RoutedEventArgs args) => await RunAsync(() => _viewModel.SelectPresetAsync(1));
    private async void Week_Click(object sender, RoutedEventArgs args) => await RunAsync(() => _viewModel.SelectPresetAsync(7));
    private async void Month_Click(object sender, RoutedEventArgs args) => await RunAsync(() => _viewModel.SelectPresetAsync(30));
    private async void Query_Click(object sender, RoutedEventArgs args) => await RunAsync(_viewModel.LoadAsync);

    private static async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { MessageBox.Show(exception.Message, "查询失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
