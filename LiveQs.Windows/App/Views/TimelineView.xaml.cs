using System.Windows;
using LiveQs.Windows.App.ViewModels;
using LiveQs.Windows.Core;

namespace LiveQs.Windows.App.Views;

public partial class TimelineView : System.Windows.Controls.UserControl
{
    private readonly TimelineViewModel _viewModel;

    public TimelineView(IActivityRepository repository, IUserDialogService dialogs)
    {
        InitializeComponent();
        _viewModel = new TimelineViewModel(repository, dialogs);
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    public Task RefreshAsync() => _viewModel.LoadAsync();
    private void OnLoaded(object sender, RoutedEventArgs args) => _viewModel.LoadCommand.Execute(null);
}
