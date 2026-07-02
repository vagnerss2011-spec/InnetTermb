using System.Collections.ObjectModel;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;

namespace RemoteOps.Desktop.ViewModels;

public sealed class KeychainViewModel : BaseViewModel
{
    private readonly ILocalStore _store;
    private readonly string _workspaceId;

    public KeychainViewModel(ILocalStore store, string workspaceId)
    {
        _store = store;
        _workspaceId = workspaceId;
    }

    public ObservableCollection<CredentialRef> Credentials { get; } = [];

    public async Task LoadAsync()
    {
        Credentials.Clear();
        foreach (var c in await _store.GetCredentialRefsAsync(_workspaceId))
            Credentials.Add(c);
    }
}
