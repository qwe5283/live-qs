using System.Windows;
using LiveQs.Windows.ViewModels;

namespace LiveQs.Windows.Views;

public partial class DashboardView : System.Windows.Controls.UserControl
{
    public DashboardViewModel ViewModel { get; }

    public DashboardView(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = ViewModel = viewModel;
        Loaded += OnLoaded;
    }

    public Task RefreshAsync() => ViewModel.RefreshAsync();
    private void OnLoaded(object sender, RoutedEventArgs args) => ViewModel.LoadCommand.Execute(null);
}
