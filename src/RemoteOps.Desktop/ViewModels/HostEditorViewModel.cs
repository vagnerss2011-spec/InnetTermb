using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;

namespace RemoteOps.Desktop.ViewModels;

public sealed class HostEditorViewModel : BaseViewModel
{
    private readonly ILocalStore _store;
    private readonly string _workspaceId;
    private readonly Asset? _existing;
    private readonly string? _groupId;
    private string _name = string.Empty;
    private string _newEndpointProtocol = "ssh";
    private string _newEndpointAddress = string.Empty;
    private int _newEndpointPort = 22;
    private string? _newEndpointCredentialId;

    public HostEditorViewModel(ILocalStore store, string workspaceId, Asset? existing, string? groupId)
    {
        _store = store;
        _workspaceId = workspaceId;
        _existing = existing;
        _groupId = existing?.GroupId ?? groupId;
        if (existing != null)
        {
            _name = existing.Name;
            foreach (var ep in existing.Endpoints) Endpoints.Add(ep);

            // Sincroniza o seletor de protocolo com o endpoint salvo — antes ficava
            // travado em "ssh" sem seleção visível, confundindo a edição.
            if (existing.Endpoints.Count > 0)
            {
                _newEndpointProtocol = existing.Endpoints[0].Protocol;
                _newEndpointPort = DefaultPortFor(_newEndpointProtocol);
            }
        }
        AddEndpointCommand = new RelayCommand(AddEndpoint, () => !string.IsNullOrWhiteSpace(NewEndpointAddress));
        RemoveEndpointCommand = new RelayCommand(obj => { if (obj is Endpoint ep) Endpoints.Remove(ep); });
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !string.IsNullOrWhiteSpace(Name));
    }

    public bool IsEdit => _existing != null;
    public string Title => IsEdit ? "Editar host" : "Novo host";
    public ObservableCollection<Endpoint> Endpoints { get; } = [];

    /// <summary>Credenciais disponíveis (metadados) para anexar ao endpoint em edição.</summary>
    public ObservableCollection<CredentialRef> AvailableCredentials { get; } = [];

    public string Name { get => _name; set { Set(ref _name, value); SaveCommand.RaiseCanExecuteChanged(); } }
    public string NewEndpointProtocol { get => _newEndpointProtocol; set { Set(ref _newEndpointProtocol, value); NewEndpointPort = DefaultPortFor(value); } }

    private int DefaultPortFor(string protocol) => protocol switch
    {
        "ssh" => 22,
        "telnet" => 23,
        "rdp" => 3389,
        "mikrotik" => 8291,
        _ => NewEndpointPort,
    };
    public string NewEndpointAddress { get => _newEndpointAddress; set { Set(ref _newEndpointAddress, value); AddEndpointCommand.RaiseCanExecuteChanged(); } }
    public int NewEndpointPort { get => _newEndpointPort; set => Set(ref _newEndpointPort, value); }
    public string? NewEndpointCredentialId { get => _newEndpointCredentialId; set => Set(ref _newEndpointCredentialId, value); }

    public RelayCommand AddEndpointCommand { get; }
    public RelayCommand RemoveEndpointCommand { get; }
    public RelayCommand SaveCommand { get; }

    public event EventHandler? Saved;

    private void AddEndpoint()
    {
        if (string.IsNullOrWhiteSpace(NewEndpointAddress)) return;
        bool isIp = System.Net.IPAddress.TryParse(NewEndpointAddress, out var ip);
        bool v6 = isIp && ip!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        bool v4 = isIp && ip!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        Endpoints.Add(new Endpoint
        {
            Id = Guid.NewGuid().ToString("n"),
            AssetId = _existing?.Id ?? string.Empty,
            Protocol = NewEndpointProtocol,
            Port = NewEndpointPort,
            Ipv4 = v4 ? NewEndpointAddress : null,
            Ipv6 = v6 ? NewEndpointAddress : null,
            Fqdn = isIp ? null : NewEndpointAddress,
            CredentialRefId = _newEndpointCredentialId,
        });
        NewEndpointAddress = string.Empty;
        NewEndpointCredentialId = null;
    }

    /// <summary>Carrega as credenciais do workspace para o seletor (sem segredo).</summary>
    public async Task LoadCredentialsAsync()
    {
        AvailableCredentials.Clear();
        foreach (var c in await _store.GetCredentialRefsAsync(_workspaceId))
            AvailableCredentials.Add(c);
    }

    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name)) return;
        if (_existing is null)
        {
            var asset = await _store.AddAssetAsync(new AddAssetRequest { WorkspaceId = _workspaceId, GroupId = _groupId, Name = Name.Trim() });
            foreach (var ep in Endpoints)
            {
                var toAdd = new Endpoint
                {
                    Id = ep.Id,
                    AssetId = asset.Id,
                    Protocol = ep.Protocol,
                    Port = ep.Port,
                    Ipv4 = ep.Ipv4,
                    Ipv6 = ep.Ipv6,
                    Fqdn = ep.Fqdn,
                    PreferIpv6 = ep.PreferIpv6,
                    CredentialRefId = ep.CredentialRefId,
                    Profile = ep.Profile,
                };
                await _store.AddEndpointAsync(toAdd);
            }
        }
        else
        {
            await _store.UpdateAssetAsync(new Asset { Id = _existing.Id, WorkspaceId = _workspaceId, GroupId = _groupId, Name = Name.Trim(), Tags = _existing.Tags, Version = _existing.Version });

            var existingEndpointIds = new System.Collections.Generic.HashSet<string>(System.Linq.Enumerable.Select(_existing.Endpoints, e => e.Id));
            var currentEndpointIds = new System.Collections.Generic.HashSet<string>(System.Linq.Enumerable.Select(Endpoints, e => e.Id));

            foreach (var removedId in existingEndpointIds)
            {
                if (!currentEndpointIds.Contains(removedId))
                    await _store.DeleteEndpointAsync(removedId);
            }

            foreach (var ep in Endpoints)
            {
                if (!existingEndpointIds.Contains(ep.Id))
                {
                    var toAdd = new Endpoint
                    {
                        Id = ep.Id,
                        AssetId = _existing.Id,
                        Protocol = ep.Protocol,
                        Port = ep.Port,
                        Ipv4 = ep.Ipv4,
                        Ipv6 = ep.Ipv6,
                        Fqdn = ep.Fqdn,
                        PreferIpv6 = ep.PreferIpv6,
                        CredentialRefId = ep.CredentialRefId,
                        Profile = ep.Profile,
                    };
                    await _store.AddEndpointAsync(toAdd);
                }
            }
        }
        Saved?.Invoke(this, EventArgs.Empty);
    }
}
