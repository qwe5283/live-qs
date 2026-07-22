namespace LiveQs.Windows.Core.Settings;

public sealed record ApplicationRule(
    string AppId,
    string Alias,
    string Category,
    bool IsExcluded);
