using System;
using System.Threading.Tasks;
using RemoteOps.Desktop.Infrastructure;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>
/// ViewModel do diálogo "Novo grupo" — nome + workspace/grupo pai, criação via
/// <see cref="ILocalStore.AddGroupAsync"/>. Contraparte de grupo do <see cref="HostEditorViewModel"/>
/// (que cobre apenas hosts).
/// </summary>
public sealed class NewGroupViewModel : BaseViewModel
{
    private readonly ILocalStore _store;
    private readonly string _workspaceId;
    private readonly string? _parentGroupId;
    private string _name = string.Empty;

    public NewGroupViewModel(ILocalStore store, string workspaceId, string? parentGroupId)
    {
        _store = store;
        _workspaceId = workspaceId;
        _parentGroupId = parentGroupId;
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !string.IsNullOrWhiteSpace(Name));
    }

    public string Name
    {
        get => _name;
        set { Set(ref _name, value); SaveCommand.RaiseCanExecuteChanged(); }
    }

    public RelayCommand SaveCommand { get; }

    public event EventHandler? Saved;

    public async Task SaveAsync()
    {
        var trimmed = Name.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        await _store.AddGroupAsync(_workspaceId, trimmed, _parentGroupId);
        Saved?.Invoke(this, EventArgs.Empty);
    }
}
