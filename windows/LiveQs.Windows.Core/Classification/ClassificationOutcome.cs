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
