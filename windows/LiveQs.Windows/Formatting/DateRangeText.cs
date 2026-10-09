using System.Globalization;

namespace LiveQs.Windows.Formatting;

public static class DateRangeText
{
    public static string Format(DateTime? startDate, DateTime? endDate, int currentYear)
    {
        if (startDate is not { } start)
        {
            return "选择日期范围";
        }

        var end = endDate ?? start;
        var format = start.Year == currentYear && end.Year == currentYear ? "M月d日" : "yyyy年M月d日";
        var first = start.ToString(format, CultureInfo.CurrentCulture);
        return start.Date == end.Date ? first : $"{first} – {end.ToString(format, CultureInfo.CurrentCulture)}";
    }
}
