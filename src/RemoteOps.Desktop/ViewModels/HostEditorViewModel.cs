using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Credentials;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;

namespace RemoteOps.Desktop.ViewModels;

public sealed class HostEditorViewModel : BaseViewModel
{
    private readonly ILocalStore _store;
    private readonly IInlineCredentialService? _inlineCreds;
    private readonly string _workspaceId;
    private readonly Asset? _existing;
    private readonly string? _groupId;

    // Rascunhos de senha inline (usuário + char[]) por endpoint AINDA não salvo. Materializados no
    // cofre só no Salvar; zerados no cancelar. Só existem para endpoints adicionados nesta sessão.
    private readonly Dictionary<string, InlineDraft> _inlineDrafts = [];

    private string _name = string.Empty;
    private string _newEndpointProtocol = "ssh";
    private string _newEndpointAddress = string.Empty;
    private int _newEndpointPort = 22;
    private string? _newEndpointCredentialId;
    private string _newEndpointSshProfile = "auto";
    private bool _useInlineCredential;
    private string _newEndpointInlineUsername = string.Empty;
    private bool _hasInlinePassword;

    private string? _vendor;
    private string? _model;
    private string? _deviceRole;
    // O operador mexeu no "Tipo" à mão? Se sim, a auto-sugestão para de sobrescrever.
    private bool _roleTouched;

    // inlineCreds é o ÚLTIMO parâmetro e opcional de propósito: mantém compatível a assinatura
    // (store, workspaceId, existing, groupId) usada nos testes de Keychain/endereço/perfil. É
    // obrigatório na prática (o MainWindow injeta) só quando a senha inline é usada.
    public HostEditorViewModel(ILocalStore store, string workspaceId, Asset? existing, string? groupId, IInlineCredentialService? inlineCreds = null)
    {
        _store = store;
        _inlineCreds = inlineCreds;
        _workspaceId = workspaceId;
        _existing = existing;
        _groupId = existing?.GroupId ?? groupId;
        if (existing != null)
        {
            _name = existing.Name;
            _vendor = existing.Vendor;
            _model = existing.Model;
            _deviceRole = existing.DeviceRole;
            _roleTouched = existing.DeviceRole != null; // já classificado → respeita a escolha salva
            foreach (var ep in existing.Endpoints) Endpoints.Add(ep);

            // Sincroniza o seletor de protocolo com o endpoint salvo — antes ficava
            // travado em "ssh" sem seleção visível, confundindo a edição.
            if (existing.Endpoints.Count > 0)
            {
                _newEndpointProtocol = existing.Endpoints[0].Protocol;
                _newEndpointPort = DefaultPortFor(_newEndpointProtocol);
            }
        }
        AddEndpointCommand = new RelayCommand(AddEndpoint, () => CanAddEndpoint);
        RemoveEndpointCommand = new RelayCommand(obj => { if (obj is Endpoint ep) RemoveEndpoint(ep); });
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !string.IsNullOrWhiteSpace(Name));
    }

    public bool IsEdit => _existing != null;
    public string Title => IsEdit ? "Editar host" : "Novo host";
    public ObservableCollection<Endpoint> Endpoints { get; } = [];

    /// <summary>Credenciais do Keychain (metadados) disponíveis para anexar ao endpoint em edição.</summary>
    public ObservableCollection<CredentialRef> AvailableCredentials { get; } = [];

    public string Name { get => _name; set { Set(ref _name, value); SaveCommand.RaiseCanExecuteChanged(); } }
    public string NewEndpointProtocol { get => _newEndpointProtocol; set { Set(ref _newEndpointProtocol, value); NewEndpointPort = DefaultPortFor(value); AutoSuggestRole(); } }

    /// <summary>Fabricante do device (ex.: Huawei, MikroTik, Debian). Alimenta a auto-sugestão do Tipo.</summary>
    public string? Vendor { get => _vendor; set { Set(ref _vendor, value); AutoSuggestRole(); } }

    /// <summary>Modelo/OS (ex.: NE8000, CCR2004, 22.04). Alimenta a auto-sugestão do Tipo.</summary>
    public string? Model { get => _model; set { Set(ref _model, value); AutoSuggestRole(); } }

    /// <summary>Papéis disponíveis no ComboBox "Tipo".</summary>
    public IReadOnlyList<string> DeviceRoleOptions => DeviceRoles.All;

    /// <summary>Papel do device. Setar manualmente marca <c>_roleTouched</c> → a auto-sugestão para de sobrescrever.</summary>
    public string? DeviceRole
    {
        get => _deviceRole;
        set
        {
            _roleTouched = true;
            Set(ref _deviceRole, value);
            RaisePropertyChanged(nameof(DeviceRoleLabel));
            RaisePropertyChanged(nameof(DeviceVendorKey));
        }
    }

    /// <summary>Rótulo pt-BR do papel atual (preview no editor).</summary>
    public string DeviceRoleLabel => DeviceCatalog.RoleLabel(_deviceRole);

    /// <summary>Chave de vendor derivada (pro preview do ícone/logo).</summary>
    public string? DeviceVendorKey => DeviceClassifier.Suggest(_vendor, _model, _newEndpointProtocol).VendorKey;

    /// <summary>
    /// Sugere o papel a partir de Vendor/Model/Protocolo enquanto o operador NÃO tiver mexido no
    /// "Tipo" à mão (<c>_roleTouched</c>). Só aplica sugestões reconhecidas (não sobrescreve com
    /// "Other"), pra não apagar um papel já bom quando o texto fica ambíguo.
    /// </summary>
    private void AutoSuggestRole()
    {
        RaisePropertyChanged(nameof(DeviceVendorKey));
        if (_roleTouched) return;
        var c = DeviceClassifier.Suggest(_vendor, _model, _newEndpointProtocol);
        if (c.Role == DeviceRoles.Other) return;
        Set(ref _deviceRole, c.Role, nameof(DeviceRole));
        RaisePropertyChanged(nameof(DeviceRoleLabel));
    }

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private int DefaultPortFor(string protocol) => protocol switch
    {
        "ssh" => 22,
        "telnet" => 23,
        "rdp" => 3389,
        "mikrotik" => 8291,
        _ => NewEndpointPort,
    };
    public string NewEndpointAddress
    {
        get => _newEndpointAddress;
        set { Set(ref _newEndpointAddress, value); RaiseCanAddEndpointChanged(); }
    }
    public int NewEndpointPort { get => _newEndpointPort; set => Set(ref _newEndpointPort, value); }
    public string? NewEndpointCredentialId { get => _newEndpointCredentialId; set => Set(ref _newEndpointCredentialId, value); }

    /// <summary>Perfil de segurança SSH do endpoint: "auto" | "strict".</summary>
    public string NewEndpointSshProfile { get => _newEndpointSshProfile; set => Set(ref _newEndpointSshProfile, value); }

    /// <summary>
    /// Modo de credencial do endpoint em edição: false = escolher do Keychain (compartilhada),
    /// true = "senha só deste dispositivo" (usuário+senha inline, cifrada e presa ao device).
    /// </summary>
    public bool UseInlineCredential
    {
        get => _useInlineCredential;
        set
        {
            if (_useInlineCredential == value)
            {
                return;
            }
            _useInlineCredential = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(UseKeychainCredential));
            RaiseCanAddEndpointChanged(); // trocar de modo muda os requisitos do "Adicionar"
        }
    }

    /// <summary>Inverso de <see cref="UseInlineCredential"/> (pros dois RadioButtons e a visibilidade).</summary>
    public bool UseKeychainCredential
    {
        get => !_useInlineCredential;
        set => UseInlineCredential = !value;
    }

    /// <summary>Usuário da credencial inline (a senha vem do PasswordBox, nunca por binding).</summary>
    public string NewEndpointInlineUsername
    {
        get => _newEndpointInlineUsername;
        set { Set(ref _newEndpointInlineUsername, value); RaiseCanAddEndpointChanged(); }
    }

    /// <summary>
    /// Se o PasswordBox inline tem senha (a View avisa via PasswordChanged — o valor NUNCA passa por
    /// binding). Usado só pra habilitar/desabilitar o "Adicionar" no modo inline.
    /// </summary>
    public bool HasInlinePassword
    {
        get => _hasInlinePassword;
        set { if (_hasInlinePassword != value) { _hasInlinePassword = value; RaiseCanAddEndpointChanged(); } }
    }

    /// <summary>
    /// No modo Keychain, basta o endereço. No modo inline, exige também usuário E senha — senão o
    /// operador salvaria um host inconectável (SSH tenta autenticar com usuário/senha vazio e falha
    /// com erro confuso) e deixaria uma credencial inútil escondida no cofre.
    /// </summary>
    public bool CanAddEndpoint =>
        !string.IsNullOrWhiteSpace(NewEndpointAddress)
        && (!UseInlineCredential
            || (!string.IsNullOrWhiteSpace(NewEndpointInlineUsername) && HasInlinePassword));

    private void RaiseCanAddEndpointChanged()
    {
        AddEndpointCommand.RaiseCanExecuteChanged();
        RaisePropertyChanged(nameof(CanAddEndpoint));
    }

    public RelayCommand AddEndpointCommand { get; }
    public RelayCommand RemoveEndpointCommand { get; }
    public RelayCommand SaveCommand { get; }

    public event EventHandler? Saved;

    /// <summary>Adiciona um endpoint com credencial do Keychain (ou nenhuma).</summary>
    public void AddEndpoint()
    {
        Endpoint? ep = BuildEndpointFromForm(_newEndpointCredentialId);
        if (ep is null)
        {
            return;
        }
        Endpoints.Add(ep);
        ResetEndpointForm();
    }

    /// <summary>
    /// Adiciona um endpoint com senha INLINE (só deste device). A senha (char[]) fica num rascunho
    /// e é cifrada no cofre apenas no Salvar; aqui não toca no store. Zera <paramref name="password"/>
    /// se o endereço estiver vazio (nada a adicionar).
    /// </summary>
    public void AddInlineEndpoint(char[] password)
    {
        Endpoint? ep = BuildEndpointFromForm(credentialRefId: null);
        if (ep is null)
        {
            Array.Clear(password);
            return;
        }
        Endpoints.Add(ep);
        _inlineDrafts[ep.Id] = new InlineDraft(NewEndpointInlineUsername.Trim(), password);
        ResetEndpointForm();
    }

    private Endpoint? BuildEndpointFromForm(string? credentialRefId)
    {
        if (string.IsNullOrWhiteSpace(NewEndpointAddress))
        {
            return null;
        }

        // Normaliza a entrada: espaços e colchetes de IPv6 ("[2001:db8::1]" → "2001:db8::1").
        // IPAddress.TryParse ACEITA a forma com colchetes, então sem o strip o IPv6 era
        // salvo com colchetes e a conexão SSH/Telnet falhava com "host inválido".
        string address = NewEndpointAddress.Trim();
        if (address.Length > 2 && address[0] == '[' && address[^1] == ']')
        {
            address = address[1..^1];
        }

        bool isIp = System.Net.IPAddress.TryParse(address, out var ip);
        bool v6 = isIp && ip!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        bool v4 = isIp && ip!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        return new Endpoint
        {
            Id = Guid.NewGuid().ToString("n"),
            AssetId = _existing?.Id ?? string.Empty,
            Protocol = NewEndpointProtocol,
            Port = NewEndpointPort,
            Ipv4 = v4 ? address : null,
            Ipv6 = v6 ? address : null,
            Fqdn = isIp ? null : address,
            CredentialRefId = credentialRefId,
            Profile = (NewEndpointProtocol == "ssh" && _newEndpointSshProfile == "strict")
                ? new EndpointProfile { SshAlgorithmProfile = "strict" }
                : null,
        };
    }

    private void ResetEndpointForm()
    {
        NewEndpointAddress = string.Empty;
        NewEndpointCredentialId = null;
        NewEndpointSshProfile = "auto";
        NewEndpointInlineUsername = string.Empty;
        HasInlinePassword = false; // a View limpa o PasswordBox no add; espelha aqui por robustez
    }

    private void RemoveEndpoint(Endpoint ep)
    {
        Endpoints.Remove(ep);
        // Se era um rascunho inline ainda não salvo, zera a senha e descarta.
        if (_inlineDrafts.Remove(ep.Id, out InlineDraft? draft))
        {
            Array.Clear(draft.Password);
        }
    }

    /// <summary>Carrega as credenciais do Keychain (sem segredo). Inline não aparece (têm escopo).</summary>
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
            var asset = await _store.AddAssetAsync(new AddAssetRequest { WorkspaceId = _workspaceId, GroupId = _groupId, Name = Name.Trim(), Vendor = Trimmed(_vendor), Model = Trimmed(_model), DeviceRole = _deviceRole });
            foreach (var ep in Endpoints)
            {
                await AddEndpointWithCredentialAsync(ep, asset.Id);
            }
        }
        else
        {
            await _store.UpdateAssetAsync(new Asset { Id = _existing.Id, WorkspaceId = _workspaceId, GroupId = _groupId, Name = Name.Trim(), Vendor = Trimmed(_vendor), Model = Trimmed(_model), DeviceRole = _deviceRole, Site = _existing.Site, Tags = _existing.Tags, Version = _existing.Version });

            var existingEndpointIds = new System.Collections.Generic.HashSet<string>(System.Linq.Enumerable.Select(_existing.Endpoints, e => e.Id));
            var currentEndpointIds = new System.Collections.Generic.HashSet<string>(System.Linq.Enumerable.Select(Endpoints, e => e.Id));

            foreach (var removed in _existing.Endpoints)
            {
                if (!currentEndpointIds.Contains(removed.Id))
                {
                    // Remove a credencial INLINE do endpoint (se houver) pra não deixar segredo órfão
                    // no cofre; credencial do Keychain (compartilhada) NÃO é apagada.
                    if (_inlineCreds is not null)
                    {
                        await _inlineCreds.DeleteForEndpointAsync(removed);
                    }
                    await _store.DeleteEndpointAsync(removed.Id);
                }
            }

            foreach (var ep in Endpoints)
            {
                if (!existingEndpointIds.Contains(ep.Id))
                {
                    await AddEndpointWithCredentialAsync(ep, _existing.Id);
                }
            }
        }
        Saved?.Invoke(this, EventArgs.Empty);
    }

    // Persiste um endpoint no store, materializando a senha inline no cofre (se houver rascunho).
    private async Task AddEndpointWithCredentialAsync(Endpoint ep, string assetId)
    {
        string? credentialRefId = ep.CredentialRefId;
        if (_inlineDrafts.Remove(ep.Id, out InlineDraft? draft))
        {
            // Cria a credencial cifrada presa a este endpoint (escondida do Keychain). Zera a senha.
            if (_inlineCreds is not null)
            {
                credentialRefId = await _inlineCreds.CreateForEndpointAsync(_workspaceId, ep.Id, draft.Username, draft.Password);
            }
            else
            {
                Array.Clear(draft.Password); // sem serviço de credencial inline: descarta a senha
            }
        }

        await _store.AddEndpointAsync(new Endpoint
        {
            Id = ep.Id,
            AssetId = assetId,
            Protocol = ep.Protocol,
            Port = ep.Port,
            Ipv4 = ep.Ipv4,
            Ipv6 = ep.Ipv6,
            Fqdn = ep.Fqdn,
            PreferIpv6 = ep.PreferIpv6,
            CredentialRefId = credentialRefId,
            Profile = ep.Profile,
        });
    }

    /// <summary>Zera qualquer senha inline em rascunho — chamado ao CANCELAR o editor.</summary>
    public void ClearInlineDrafts()
    {
        foreach (InlineDraft draft in _inlineDrafts.Values)
        {
            Array.Clear(draft.Password);
        }
        _inlineDrafts.Clear();
    }

    private sealed record InlineDraft(string Username, char[] Password);
}
