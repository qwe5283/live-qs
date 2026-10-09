using System.Windows;
using System.Windows.Controls;
using LiveQs.Windows.Controls;
using LiveQs.Windows.ViewModels;

namespace LiveQs.Windows.Views;

public partial class DashboardView : System.Windows.Controls.UserControl
{
    public DashboardViewModel ViewModel
    {
        get;
    }

    public DashboardView(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = ViewModel = viewModel;
        Loaded += OnLoaded;
    }

    public Task RefreshAsync() => ViewModel.RefreshAsync();
    private void OnLoaded(object sender, RoutedEventArgs args) => ViewModel.LoadCommand.Execute(null);

    private async void OnRangeSelected(object? sender, DateRangeSelectedEventArgs args) =>
        await ViewModel.SelectRangeAsync(args.StartDate, args.EndDate);

    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (RangeFilters is null)
        {
            return;
        }

        var narrow = args.NewSize.Width < 700;
        Grid.SetRow(RangeFilters, narrow ? 1 : 0);
        Grid.SetColumn(RangeFilters, narrow ? 0 : 1);
        Grid.SetColumnSpan(RangeFilters, narrow ? 2 : 1);
        RangeFilters.Margin = narrow ? new Thickness(0, 14, 0, 0) : new Thickness(16, 0, 0, 0);
    }
}
