namespace LiveQs.Windows.Controls;

public sealed class DateRangeSelectedEventArgs(DateTime startDate, DateTime endDate) : EventArgs
{
    public DateTime StartDate { get; } = startDate;
    public DateTime EndDate { get; } = endDate;
}
