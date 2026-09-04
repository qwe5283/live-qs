using System.Globalization;
using System.Text.RegularExpressions;

namespace LiveQs.Windows.Core.Update;

/// <summary>
/// Core semver X.Y.Z comparison for update decisions. Release channels use
/// plain semantic versions without pre-release or build metadata; parts
/// compare numerically, never lexically.
/// </summary>
public static partial class UpdateSemver
{
    [GeneratedRegex("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$")]
    private static partial Regex CorePattern();

    public static bool IsCoreSemver(string value) => CorePattern().IsMatch(value);

    /// <summary>Compares two core semver versions; -1 when left is older, 0 when equal, 1 when newer.</summary>
    public static int Compare(string left, string right)
    {
        var first = Parse(left);
        var second = Parse(right);
        for (var index = 0; index < 3; index++)
        {
            if (first[index] != second[index]) return first[index] < second[index] ? -1 : 1;
        }
        return 0;
    }

    private static int[] Parse(string value)
    {
        var match = CorePattern().Match(value);
        if (!match.Success) throw new FormatException($"The value is not a core semver version: {value}");
        return
        [
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
        ];
    }
}
