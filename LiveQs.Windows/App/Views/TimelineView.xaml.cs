using System.Windows;
using System.Windows.Controls;
using LiveQs.Windows.App.ViewModels;
using LiveQs.Windows.Core;
using Microsoft.Extensions.Logging;

namespace LiveQs.Windows.App.Views;

public partial class TimelineView : System.Windows.Controls.UserControl
{
    private readonly TimelineViewModel _viewModel;

    public TimelineView(
        IActivityRepository repository,
        IUserDialogService dialogs,
        ILogger<TimelineViewModel> logger)
    {
        InitializeComponent();
        _viewModel = new TimelineViewModel(repository, dialogs, logger);
        DataContext = _viewModel;
    }

    public Task RefreshAsync() => _viewModel.LoadAsync();

    private void TimelineList_ScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (args.ExtentHeight <= args.ViewportHeight || args.VerticalOffset < args.ExtentHeight - args.ViewportHeight - 240)
            return;
        _viewModel.LoadMoreCommand.Execute(null);
    }
}
