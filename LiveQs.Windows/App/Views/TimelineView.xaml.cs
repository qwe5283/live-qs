using System.Windows;
using System.Windows.Controls;
using LiveQs.Windows.App.ViewModels;
using LiveQs.Windows.Core;

namespace LiveQs.Windows.App.Views;

public partial class TimelineView : System.Windows.Controls.UserControl
{
    private readonly TimelineViewModel _viewModel;

    public TimelineView(IActivityRepository repository)
    {
        InitializeComponent();
        _viewModel = new TimelineViewModel(repository);
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    public Task RefreshAsync() => _viewModel.LoadAsync();
    private async void OnLoaded(object sender, RoutedEventArgs args) => await RunAsync(_viewModel.LoadAsync);
    private async void Previous_Click(object sender, RoutedEventArgs args) => await RunAsync(() => _viewModel.MoveAsync(-1));
    private async void Next_Click(object sender, RoutedEventArgs args) => await RunAsync(() => _viewModel.MoveAsync(1));
    private async void Today_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.SelectedDate = DateTime.Today;
        await RunAsync(_viewModel.LoadAsync);
    }
    private async void Query_Click(object sender, RoutedEventArgs args) => await RunAsync(_viewModel.LoadAsync);

    private static async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { MessageBox.Show(exception.Message, "查询失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
