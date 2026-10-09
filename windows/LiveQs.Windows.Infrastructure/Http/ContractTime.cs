using System.Globalization;

namespace LiveQs.Windows.Infrastructure.Http;

internal static class ContractTime
{
    public static string Format(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string? Format(DateTimeOffset? instant) => instant is { } value ? Format(value) : null;
}
