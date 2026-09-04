using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LiveQs.Windows.Core.Contracts;

namespace LiveQs.Windows.Core.Classification;

/// <summary>
/// The explainable result of one local match: everything the upload carries,
/// nothing it must not (the raw title stays behind).
/// </summary>
public sealed record ClassificationOutcome(
    string SubjectId,
    string RuleId,
    long RuleVersion,
    double Confidence);

/// <summary>
/// Executes the Owner's versioned rule set entirely on-device: application
/// identity and the raw window title are matched locally, and only the
/// semantic result (subject, rule id, rule version, confidence) travels with
/// the uploaded interval. When nothing matches, the interval uploads without
/// a subject — never with a raw title or an unsalted digest of one.
/// </summary>
public static class ClassificationEngine
{
    /// <summary>
    /// Rules are evaluated in distribution order (priority descending, then
    /// rule_id ascending) and the first match wins, so Owner priorities and
    /// ties resolve deterministically on every device. The device-secret
    /// <paramref name="classificationSecret"/> keys the opaque identifiers of
    /// dynamically discovered project names.
    /// </summary>
    public static ClassificationOutcome? Classify(
        ClassificationRuleSet? ruleSet,
        string platform,
        string appId,
        string? windowTitle,
        string classificationSecret)
    {
        if (ruleSet is null) return null;
        var rules = ruleSet.Rules ?? [];
        if (rules.Length == 0) return null;

        foreach (var rule in rules.OrderBy(rule => rule, RuleOrder.Comparer))
        {
            if (!AppliesTo(rule, platform)) continue;
            if (!Matches(rule, appId, windowTitle, classificationSecret, out var subject)) continue;
            return subject is null ? null : new ClassificationOutcome(
                subject,
                rule.RuleId,
                rule.Version,
                rule.Confidence);
        }
        return null;
    }

    private static bool AppliesTo(ClassificationRule rule, string platform) => rule.Platform switch
    {
        ClassificationRulePlatform.Any => true,
        ClassificationRulePlatform.Windows => string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase),
        ClassificationRulePlatform.Android => string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool Matches(ClassificationRule rule, string appId, string? windowTitle, string classificationSecret, out string? subject)
    {
        subject = null;
        switch (rule.Kind)
        {
            case ClassificationRuleKind.Application:
                // Executable names are case-insensitive on Windows; Android
                // package names are matched by the same tolerant comparison.
                if (!string.Equals(appId, rule.Pattern, StringComparison.OrdinalIgnoreCase)) return false;
                subject = rule.SubjectEntityId;
                return subject is not null;

            case ClassificationRuleKind.TitleKeyword:
                if (string.IsNullOrEmpty(windowTitle)
                    || windowTitle.IndexOf(rule.Pattern, StringComparison.OrdinalIgnoreCase) < 0) return false;
                subject = rule.SubjectEntityId;
                return subject is not null;

            case ClassificationRuleKind.TitleRegex:
                if (string.IsNullOrEmpty(windowTitle)) return false;
                Regex regex;
                try
                {
                    regex = new Regex(rule.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    // The control plane validates patterns, but regex dialects
                    // differ across platforms: an unexecutable rule degrades to
                    // no-match instead of breaking classification.
                    return false;
                }
                var match = regex.Match(windowTitle);
                if (!match.Success) return false;
                if (rule.Dynamic == true)
                {
                    // Dynamic project discovery: capture group 1 names a
                    // candidate project. Before the Owner approves an alias,
                    // the device reports an opaque identifier derived with the
                    // device-secret HMAC — the name itself never leaves.
                    subject = match.Groups.Count > 1 && match.Groups[1].Success
                        ? OpaqueSubjects.ForProjectName(match.Groups[1].Value, classificationSecret)
                        : null;
                    return subject is not null;
                }
                subject = rule.SubjectEntityId;
                return subject is not null;

            default:
                return false;
        }
    }

    private sealed class RuleOrder : IComparer<ClassificationRule>
    {
        public static readonly RuleOrder Comparer = new();

        public int Compare(ClassificationRule? x, ClassificationRule? y)
        {
            if (x is null || y is null) return (x is null ? 0 : 1) - (y is null ? 0 : 1);
            var byPriority = y.Priority.CompareTo(x.Priority);
            return byPriority != 0 ? byPriority : string.CompareOrdinal(x.RuleId, y.RuleId);
        }
    }
}

/// <summary>
/// Opaque identifiers for locally discovered project names. The identifier is
/// the first 128 bits of HMAC-SHA256 keyed with a device-generated secret that
/// never leaves this installation, so the same project aggregates under one
/// stable id while neither the name nor an unsalted digest of it is uploaded.
/// </summary>
public static class OpaqueSubjects
{
    private const string Prefix = "unapproved-";

    /// <summary>Derives the stable opaque subject id for one local project name.</summary>
    public static string ForProjectName(string projectName, string secretBase64)
    {
        var name = projectName.Trim();
        var key = Convert.FromBase64String(secretBase64);
        var digest = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("project:" + name));
        var hex = Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
        return Prefix + hex;
    }

    /// <summary>Generates the per-installation classification secret (32 random bytes).</summary>
    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
