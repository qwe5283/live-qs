namespace LiveQs.Windows.Core.Common;

public readonly record struct DateRange(DateTimeOffset Start, DateTimeOffset End)
{
    public static DateRange FromLocalDates(DateTime startDate, DateTime endDateInclusive)
    {
        var start = new DateTimeOffset(startDate.Date, TimeZoneInfo.Local.GetUtcOffset(startDate.Date));
        var endDate = endDateInclusive.Date.AddDays(1);
        var end = new DateTimeOffset(endDate, TimeZoneInfo.Local.GetUtcOffset(endDate));
        return new DateRange(start, end);
    }

    public static DateRange Today(TimeProvider? timeProvider = null)
    {
        var today = (timeProvider ?? TimeProvider.System).GetLocalNow().Date;
        return FromLocalDates(today, today);
    }
}
