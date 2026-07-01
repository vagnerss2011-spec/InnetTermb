using System.Collections.ObjectModel;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;

namespace RemoteOps.Desktop.ViewModels;

public sealed class HostListViewModel : BaseViewModel
{
    private readonly ILocalStore _store;
    private readonly string _workspaceId;
    private AssetViewModel? _selectedAsset;
    private string? _activeGroupId;
    private string _newHostName = string.Empty;
    private bool _isBusy;
    private string _filterText = string.Empty;

    public HostListViewModel(ILocalStore store, string workspaceId)
    {
        _store = store;
        _workspaceId = workspaceId;

        AddHostCommand = new RelayCommand(
            () => _ = AddHostAsync(),
            () => !IsBusy && !string.IsNullOrWhiteSpace(NewHostName));

        DeleteHostCommand = new RelayCommand(
            () => _ = DeleteHostAsync(),
            () => !IsBusy && SelectedAsset != null);

        LoadCommand = new RelayCommand(() => _ = LoadAsync());

        ConnectCommand = new RelayCommand(
            obj => ConnectRequested?.Invoke(this, obj as string ?? "ssh"),
            _ => SelectedAsset != null);

        OpenWinBoxCommand = new RelayCommand(
            () => WinBoxRequested?.Invoke(this, EventArgs.Empty),
            () => SelectedAsset != null);
    }

    public ObservableCollection<AssetViewModel> Assets { get; } = [];

    public RelayCommand AddHostCommand { get; }
    public RelayCommand DeleteHostCommand { get; }
    public RelayCommand LoadCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand OpenWinBoxCommand { get; }

    public AssetViewModel? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            Set(ref _selectedAsset, value);
            DeleteHostCommand.RaiseCanExecuteChanged();
            ConnectCommand.RaiseCanExecuteChanged();
            OpenWinBoxCommand.RaiseCanExecuteChanged();
            AssetSelected?.Invoke(this, value);
        }
    }

    public string NewHostName
    {
        get => _newHostName;
        set
        {
            Set(ref _newHostName, value);
            AddHostCommand.RaiseCanExecuteChanged();
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            Set(ref _filterText, value);
            _ = LoadAsync();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            Set(ref _isBusy, value);
            AddHostCommand.RaiseCanExecuteChanged();
            DeleteHostCommand.RaiseCanExecuteChanged();
        }
    }

    public event EventHandler<AssetViewModel?>? AssetSelected;
    public event EventHandler<string>? ConnectRequested;
    public event EventHandler? WinBoxRequested;

    public async Task LoadAsync(string? groupId = null)
    {
        _activeGroupId = groupId ?? _activeGroupId;
        IsBusy = true;
        try
        {
            var assets = await _store.GetAssetsAsync(_workspaceId, _activeGroupId);
            var filter = FilterText.Trim();

            Assets.Clear();
            foreach (var a in assets)
            {
                if (!string.IsNullOrEmpty(filter) &&
                    !a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !string.Join(" ", a.Tags).Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                Assets.Add(new AssetViewModel(a));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AddHostAsync()
    {
        var name = NewHostName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var asset = await _store.AddAssetAsync(new AddAssetRequest
            {
                WorkspaceId = _workspaceId,
                GroupId = _activeGroupId,
                Name = name,
            });
            var vm = new AssetViewModel(asset);
            Assets.Add(vm);
            NewHostName = string.Empty;
            SelectedAsset = vm;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteHostAsync()
    {
        if (SelectedAsset == null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _store.DeleteAssetAsync(SelectedAsset.Id);
            Assets.Remove(SelectedAsset);
            SelectedAsset = null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
