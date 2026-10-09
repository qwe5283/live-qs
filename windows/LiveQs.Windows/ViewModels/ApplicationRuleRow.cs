using CommunityToolkit.Mvvm.ComponentModel;
using LiveQs.Windows.Core.Settings;

namespace LiveQs.Windows.ViewModels;

public sealed partial class ApplicationRuleRow : ViewModelBase
{
    [ObservableProperty]
    private string _alias;
    [ObservableProperty]
    private string _category;
    [ObservableProperty]
    private bool _isExcluded;

    public ApplicationRuleRow(ApplicationRule rule)
    {
        AppId = rule.AppId;
        _alias = rule.Alias;
        _category = rule.Category;
        _isExcluded = rule.IsExcluded;
    }

    public string AppId
    {
        get;
    }
    public ApplicationRule ToRule() => new(AppId, Alias ?? "", Category ?? "未分类", IsExcluded);
}
