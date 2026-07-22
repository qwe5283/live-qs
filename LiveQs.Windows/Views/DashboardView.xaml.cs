using System.Windows;
using LiveQs.Windows.Core;
using LiveQs.Windows.Services;
using LiveQs.Windows.ViewModels;

namespace LiveQs.Windows.Views;

public partial class DashboardView : System.Windows.Controls.UserControl
{
    private readonly DashboardViewModel _viewModel;

    public DashboardView(IActivityRepository repository, IUserDialogService dialogs)
    {
        InitializeComponent();
        _viewModel = new DashboardViewModel(repository, dialogs);
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    public Task RefreshAsync() => _viewModel.LoadAsync();
    private void OnLoaded(object sender, RoutedEventArgs args) => _viewModel.LoadCommand.Execute(null);
}
