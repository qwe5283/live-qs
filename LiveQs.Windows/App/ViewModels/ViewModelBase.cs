using CommunityToolkit.Mvvm.ComponentModel;
using System.Runtime.CompilerServices;

namespace LiveQs.Windows.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        return SetProperty(ref field, value, propertyName);
    }
}
