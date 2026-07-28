using System.Windows;
using System.Windows.Controls;
using LiveQs.Windows.ViewModels;

namespace LiveQs.Windows.Views;

public partial class TimelineView : System.Windows.Controls.UserControl
{
    public TimelineViewModel ViewModel { get; }

    public TimelineView(TimelineViewModel viewModel)
    {
        InitializeComponent();
        DataContext = ViewModel = viewModel;
    }

    public Task RefreshAsync() => ViewModel.RefreshAsync();

    private void TimelineList_ScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (args.ExtentHeight <= args.ViewportHeight || args.VerticalOffset < args.ExtentHeight - args.ViewportHeight - 240)
            return;
        ViewModel.LoadMoreCommand.Execute(null);
    }
}
