namespace LiveQs.Windows.Core.Common;

public static class DurationText
{
    public static string Format(TimeSpan duration)
    {
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}小时 {duration.Minutes}分钟";
        if (duration.TotalMinutes >= 1) return $"{Math.Max(1, (int)duration.TotalMinutes)}分钟";
        return $"{Math.Max(0, (int)duration.TotalSeconds)}秒";
    }
}
