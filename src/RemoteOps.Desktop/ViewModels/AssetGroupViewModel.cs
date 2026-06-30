using System.Collections.ObjectModel;
using RemoteOps.Desktop.Domain;

namespace RemoteOps.Desktop.ViewModels;

public sealed class AssetGroupViewModel : BaseViewModel
{
    private string _name;
    private bool _isSelected;
    private bool _isExpanded = true;

    public AssetGroupViewModel(AssetGroup group)
    {
        Group = group;
        _name = group.Name;
    }

    public AssetGroup Group { get; }

    public string Id => Group.Id;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public ObservableCollection<AssetGroupViewModel> Children { get; } = [];
}
