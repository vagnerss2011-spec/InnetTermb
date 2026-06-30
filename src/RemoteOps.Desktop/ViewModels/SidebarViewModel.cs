using System.Collections.ObjectModel;
using RemoteOps.Desktop.Infrastructure;

namespace RemoteOps.Desktop.ViewModels;

public sealed class SidebarViewModel : BaseViewModel
{
    private readonly ILocalStore _store;
    private readonly string _workspaceId;
    private AssetGroupViewModel? _selectedGroup;
    private string _newGroupName = string.Empty;
    private bool _isBusy;

    public SidebarViewModel(ILocalStore store, string workspaceId)
    {
        _store = store;
        _workspaceId = workspaceId;

        AddGroupCommand = new RelayCommand(
            () => _ = AddGroupAsync(),
            () => !IsBusy && !string.IsNullOrWhiteSpace(NewGroupName));

        DeleteGroupCommand = new RelayCommand(
            () => _ = DeleteGroupAsync(),
            () => !IsBusy && SelectedGroup != null);

        LoadCommand = new RelayCommand(() => _ = LoadAsync());
    }

    public ObservableCollection<AssetGroupViewModel> Groups { get; } = [];

    public RelayCommand AddGroupCommand { get; }
    public RelayCommand DeleteGroupCommand { get; }
    public RelayCommand LoadCommand { get; }

    public AssetGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            Set(ref _selectedGroup, value);
            DeleteGroupCommand.RaiseCanExecuteChanged();
            GroupSelected?.Invoke(this, value);
        }
    }

    public string NewGroupName
    {
        get => _newGroupName;
        set
        {
            Set(ref _newGroupName, value);
            AddGroupCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            Set(ref _isBusy, value);
            AddGroupCommand.RaiseCanExecuteChanged();
            DeleteGroupCommand.RaiseCanExecuteChanged();
        }
    }

    public event EventHandler<AssetGroupViewModel?>? GroupSelected;

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var groups = await _store.GetGroupsAsync(_workspaceId);
            Groups.Clear();
            foreach (var g in groups)
            {
                Groups.Add(new AssetGroupViewModel(g));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AddGroupAsync()
    {
        var name = NewGroupName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var group = await _store.AddGroupAsync(_workspaceId, name, SelectedGroup?.Id);
            var vm = new AssetGroupViewModel(group);
            if (SelectedGroup != null)
            {
                SelectedGroup.Children.Add(vm);
            }
            else
            {
                Groups.Add(vm);
            }
            NewGroupName = string.Empty;
            SelectedGroup = vm;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteGroupAsync()
    {
        if (SelectedGroup == null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _store.DeleteGroupAsync(SelectedGroup.Id);
            Groups.Remove(SelectedGroup);
            SelectedGroup = null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
