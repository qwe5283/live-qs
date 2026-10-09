namespace LiveQs.Windows.Tests;

internal sealed class TestClock : TimeProvider
{
    public static readonly DateTimeOffset DefaultInstant = new(2026, 10, 10, 1, 0, 0, TimeSpan.Zero);

    public TestClock(DateTimeOffset? utcNow = null)
    {
        UtcNow = utcNow ?? DefaultInstant;
    }

    public DateTimeOffset UtcNow
    {
        get; set;
    }

    public void Advance(TimeSpan duration) => UtcNow += duration;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
