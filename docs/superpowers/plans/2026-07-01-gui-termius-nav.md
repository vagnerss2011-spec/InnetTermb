# GUI estilo Termius — navegação por grupos/hosts (Fase 1) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Substituir o layout de 3 painéis + barra de menus do RemoteOps.Desktop por uma navegação estilo Termius: shell com abas no topo (aba fixa "Hosts" + abas de sessão), rail Hosts/Keychain/Logs, grid de grupos em cards com drill-down, e abrir host = aba de terminal.

**Architecture:** Extrair um `SessionLauncher` (fonte única de "abrir host por protocolo", hoje espalhado em MainViewModel/InspectorViewModel). Novos ViewModels (Hosts/HostEditor/Keychain/Logs/Browser/Workspace) reusam `ILocalStore` e o `SessionLauncher`. O `MainWindow` vira um `TabControl` (browser fixo + sessões, reusando `TabsViewModel`). Views antigas (Sidebar/HostList/Inspector) e a barra de menus são removidas.

**Tech Stack:** .NET 10, WPF (net10.0-windows), Microsoft.Extensions.DependencyInjection 10, xUnit 2.9.3.

**Worktree:** `C:\dev\remoteops-termius` (branch `feature/gui-termius-nav`, a partir de `f3c91ed`). Todos os caminhos são relativos a essa base.

**Spec:** `docs/superpowers/specs/2026-07-01-gui-termius-nav-design.md`

## Global Constraints

- Repo-wide `TreatWarningsAsErrors=true` — build 0/0. `Nullable=enable`, `ImplicitUsings=enable`.
- WPF theme brushes via `DynamicResource` (nunca StaticResource). Tokens já existem em `Themes/Tokens/`.
- UI copy em **pt-BR**, sentence case.
- Nenhum segredo trafega pela UI — só `CredentialRef` (metadados) e `SecretEnvelopeId`.
- Abrir host reusa a MESMA lógica de sessão (via `SessionLauncher`) — não duplicar; respeita a flag `rdp.enabled`.
- Testes xUnit (`[Fact]`/`Assert`), namespace espelha a pasta (`RemoteOps.UnitTests.Desktop.*`). Os testes existentes devem ficar verdes (menos os dos VMs removidos, que são migrados na Task 13).
- Build: `dotnet build "C:\dev\remoteops-termius\RemoteOps.sln" -c Debug --nologo`. Test: `dotnet test "C:\dev\remoteops-termius\RemoteOps.sln" -c Debug --nologo`.
- WorkspaceId local fixo: `"ws-local"` (mesmo default do MainViewModel atual).

## Interfaces existentes reaproveitadas (não recriar)

- `ILocalStore` (Infrastructure): `GetGroupsAsync(ws)`, `AddGroupAsync(ws,name,parentId?)`, `DeleteGroupAsync(id)`, `GetAssetsAsync(ws,groupId?)`, `GetAssetAsync(id)`, `AddAssetAsync(AddAssetRequest)`, `UpdateAssetAsync(Asset)`, `DeleteAssetAsync(id)`, `AddEndpointAsync(Endpoint)`, `DeleteEndpointAsync(id)`, `GetCredentialRefsAsync(ws)`.
- `Asset` (Contracts.Assets): Id, WorkspaceId, GroupId?, Name, Vendor?, Model?, Site?, Tags(List), Endpoints(List<Endpoint>).
- `Endpoint`: Id, AssetId, Protocol (ssh|telnet|rdp|mikrotik|ndesk), Fqdn?/Ipv4?/Ipv6?, Port, PreferIpv6, CredentialRefId?.
- `CredentialRef`: Id, Name, Type, Scope?, Metadata?(Username?, HasPrivateKey), SecretEnvelopeId?.
- `AssetViewModel` (ViewModels): ctor(Asset); Id, Name, Vendor, Tags, PrimaryProtocol, PrimaryAddress, IsSelected, Asset, Refresh(Asset).
- `AddAssetRequest` (Domain): WorkspaceId, GroupId?, Name.
- `TabsViewModel` (ViewModels): `OpenTerminalTab(TerminalTabViewModel)`, `OpenRdpTab(RdpTabViewModel)`, `OpenTab(name,protocol)`, `Tabs`, `ActiveTab`, `HasTabs`, `CloseTabCommand`, `CloseActiveTabCommand`.
- `TerminalTabViewModel(id,title,protocol,provider,baseRequest)`, `RdpTabViewModel(id,title,protocol,provider,credentialResolver,baseRequest)`.
- `SessionRequest`/`OpenSessionRequest` (Contracts.Sessions / InspectorViewModel): SessionId/Protocol/EndpointId/CredentialRefId etc.
- Providers (keyed): `ITerminalSessionProvider` (Ssh/Telnet), `IRdpSessionProvider`, `IRdpCredentialResolver`, `IWinBoxRunner`, `IFeatureFlags` (`FeatureFlagNames.RdpEnabled`).
- `RelayCommand`, `BaseViewModel` (Set/RaisePropertyChanged).
- `MainViewModel.OnSessionRequested` + `InspectorViewModel.OpenWinBoxAsync` — a lógica a ser EXTRAÍDA para `SessionLauncher` (Task 1).
- `SettingsWindow(SettingsViewModel)` + `MainViewModel.CreateSettingsViewModel()`/`AppVersionText` — reusados pelo menu da conta.

## Estrutura de arquivos (novos)

```
src/RemoteOps.Desktop/
  Sessions/SessionLauncher.cs           (fonte única de abrir host por protocolo)
  Infrastructure/IUiLogSink.cs          (sink de eventos p/ a tela Logs)
  ViewModels/GroupCardViewModel.cs
  ViewModels/HostsViewModel.cs
  ViewModels/HostEditorViewModel.cs
  ViewModels/KeychainViewModel.cs
  ViewModels/LogsViewModel.cs
  ViewModels/BrowserViewModel.cs
  ViewModels/WorkspaceViewModel.cs
  Views/HostsView.xaml(.cs)
  Views/HostEditorDialog.xaml(.cs)
  Views/KeychainView.xaml(.cs)
  Views/LogsView.xaml(.cs)
  Views/BrowserView.xaml(.cs)
```
Removidos na Task 13: `Views/SidebarView.*`, `Views/HostListView.*`, `Views/InspectorView.*`, `ViewModels/SidebarViewModel.cs`, `ViewModels/HostListViewModel.cs`, `ViewModels/InspectorViewModel.cs` (a lógica migra), `ViewModels/MainViewModel.cs` (vira WorkspaceViewModel).

---

### Task 1: Extrair `SessionLauncher` (fonte única de conexão)

**Files:**
- Create: `src/RemoteOps.Desktop/Sessions/SessionLauncher.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Sessions/SessionLauncherTests.cs`

**Interfaces:**
- Produces: `SessionLauncher` — ctor(`TabsViewModel tabs`, `IWinBoxRunner? winBox`, `IFeatureFlags? flags`, `ITerminalSessionProvider? ssh`, `ITerminalSessionProvider? telnet`, `IRdpSessionProvider? rdp`, `IRdpCredentialResolver? rdpCred`); `string PrimaryProtocol(Asset asset)`; `bool CanLaunch(Asset asset, string protocol)`; `Task LaunchAsync(Asset asset, string protocol)`.

- [ ] **Step 1: Teste que falha** — `SessionLauncherTests.cs`:
```csharp
using System.Collections.Generic;
using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Terminal;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Sessions;

public sealed class SessionLauncherTests
{
    private sealed class FakeTerminalProvider : ITerminalSessionProvider
    {
        public ITerminalSession Create(SessionRequest request) => null!;
    }

    private static Asset AssetWith(params string[] protocols)
    {
        var eps = new List<Endpoint>();
        foreach (var p in protocols)
            eps.Add(new Endpoint { Id = "e-" + p, AssetId = "a1", Protocol = p, Ipv4 = "10.0.0.1", Port = 22, CredentialRefId = "c1" });
        return new Asset { Id = "a1", WorkspaceId = "ws-local", Name = "r1", Endpoints = eps };
    }

    [Fact]
    public void PrimaryProtocol_PrefersSshThenTelnetThenRdp()
    {
        var l = new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null);
        Assert.Equal("ssh", l.PrimaryProtocol(AssetWith("telnet", "ssh")));
        Assert.Equal("telnet", l.PrimaryProtocol(AssetWith("rdp", "telnet")));
        Assert.Equal("rdp", l.PrimaryProtocol(AssetWith("rdp")));
    }

    [Fact]
    public async Task LaunchAsync_Ssh_OpensTerminalTab()
    {
        var tabs = new TabsViewModel();
        var l = new SessionLauncher(tabs, null, null, new FakeTerminalProvider(), null, null, null);

        await l.LaunchAsync(AssetWith("ssh"), "ssh");

        Assert.True(tabs.HasTabs);
    }

    [Fact]
    public void CanLaunch_Rdp_RequiresFlag()
    {
        var l = new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null);
        // sem flag rdp.enabled e sem provider → não pode
        Assert.False(l.CanLaunch(AssetWith("rdp"), "rdp"));
    }
}
```

- [ ] **Step 2: Rodar e ver falhar** — `dotnet test "C:\dev\remoteops-termius\RemoteOps.sln" --filter "FullyQualifiedName~SessionLauncherTests" --nologo` → FALHA de compilação.

- [ ] **Step 3: Implementar `SessionLauncher.cs`** (mover a lógica de `MainViewModel.OnSessionRequested` + `InspectorViewModel.OpenWinBoxAsync`):
```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.ExternalTools;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Rdp;
using RemoteOps.Desktop.Terminal;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.MikroTik;
using RemoteOps.Rdp;
using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Sessions;

/// <summary>
/// Fonte única de "abrir um host por protocolo". Substitui a lógica antes espalhada
/// em MainViewModel.OnSessionRequested e InspectorViewModel.OpenWinBoxAsync.
/// </summary>
public sealed class SessionLauncher
{
    private static readonly string[] Preference = { RemoteProtocol.Ssh, RemoteProtocol.Telnet, RemoteProtocol.Rdp, RemoteProtocol.MikroTik };

    private readonly TabsViewModel _tabs;
    private readonly IWinBoxRunner? _winBox;
    private readonly IFeatureFlags? _flags;
    private readonly ITerminalSessionProvider? _ssh;
    private readonly ITerminalSessionProvider? _telnet;
    private readonly IRdpSessionProvider? _rdp;
    private readonly IRdpCredentialResolver? _rdpCred;

    public SessionLauncher(
        TabsViewModel tabs,
        IWinBoxRunner? winBox,
        IFeatureFlags? flags,
        ITerminalSessionProvider? ssh,
        ITerminalSessionProvider? telnet,
        IRdpSessionProvider? rdp,
        IRdpCredentialResolver? rdpCred)
    {
        _tabs = tabs;
        _winBox = winBox;
        _flags = flags;
        _ssh = ssh;
        _telnet = telnet;
        _rdp = rdp;
        _rdpCred = rdpCred;
    }

    public string PrimaryProtocol(Asset asset)
    {
        foreach (var p in Preference)
        {
            if (asset.Endpoints.Any(e => e.Protocol == p))
            {
                return p;
            }
        }
        return asset.Endpoints.Count > 0 ? asset.Endpoints[0].Protocol : RemoteProtocol.Ssh;
    }

    public bool CanLaunch(Asset asset, string protocol)
    {
        var ep = asset.Endpoints.FirstOrDefault(e => e.Protocol == protocol);
        if (ep is null)
        {
            return false;
        }
        return protocol switch
        {
            RemoteProtocol.Ssh => _ssh != null,
            RemoteProtocol.Telnet => _telnet != null,
            RemoteProtocol.Rdp => (_flags?.IsEnabled(FeatureFlagNames.RdpEnabled) ?? false) && _rdp != null && _rdpCred != null,
            RemoteProtocol.MikroTik => _winBox != null,
            _ => false,
        };
    }

    public async Task LaunchAsync(Asset asset, string protocol)
    {
        var ep = asset.Endpoints.FirstOrDefault(e => e.Protocol == protocol);
        if (ep is null)
        {
            return;
        }

        if (protocol == RemoteProtocol.MikroTik)
        {
            await LaunchWinBoxAsync(asset, ep);
            return;
        }

        if (protocol == RemoteProtocol.Rdp)
        {
            if (!(_flags?.IsEnabled(FeatureFlagNames.RdpEnabled) ?? false) || _rdp is null || _rdpCred is null || ep.CredentialRefId is null)
            {
                _tabs.OpenTab(asset.Name, protocol);
                return;
            }
            var req = new SessionRequest { SessionId = Guid.NewGuid().ToString("n"), Protocol = protocol, EndpointId = ep.Id, CredentialRefId = ep.CredentialRefId };
            _tabs.OpenRdpTab(new RdpTabViewModel(req.SessionId, $"{asset.Name} ({protocol.ToUpperInvariant()})", protocol, _rdp, _rdpCred, req));
            return;
        }

        var provider = protocol == RemoteProtocol.Ssh ? _ssh : protocol == RemoteProtocol.Telnet ? _telnet : null;
        if (provider != null && ep.CredentialRefId != null)
        {
            var req = new SessionRequest { SessionId = Guid.NewGuid().ToString("n"), Protocol = protocol, EndpointId = ep.Id, CredentialRefId = ep.CredentialRefId };
            _tabs.OpenTerminalTab(new TerminalTabViewModel(req.SessionId, $"{asset.Name} ({protocol.ToUpperInvariant()})", protocol, provider, req));
        }
        else
        {
            _tabs.OpenTab(asset.Name, protocol);
        }
    }

    private async Task LaunchWinBoxAsync(Asset asset, Endpoint ep)
    {
        if (_winBox is null)
        {
            return;
        }
        var (address, family) = ResolveAddress(ep);
        var request = new ExternalToolLaunchRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkspaceId = asset.WorkspaceId,
            Tool = "winbox",
            HostId = asset.Id,
            Target = new ExternalToolTarget { Address = address, AddressFamily = family, Port = ep.Port, PreferIpv6 = ep.PreferIpv6 },
            CredentialRefId = ep.CredentialRefId,
            IncludePasswordArgument = false,
            RequestedBy = "local-user",
            RequestedAt = DateTimeOffset.UtcNow,
        };
        await _winBox.LaunchAsync(request);
    }

    private static (string address, string? family) ResolveAddress(Endpoint ep)
    {
        if (ep.PreferIpv6 && ep.Ipv6 != null) return (ep.Ipv6, "ipv6");
        if (ep.Ipv4 != null) return (ep.Ipv4, "ipv4");
        if (ep.Ipv6 != null) return (ep.Ipv6, "ipv6");
        return (ep.Fqdn ?? string.Empty, ep.Fqdn != null ? "dns" : null);
    }
}
```
> Verificar os nomes reais de `RemoteProtocol.*` e `ExternalToolLaunchRequest`/`ExternalToolTarget` contra `InspectorViewModel.OpenWinBoxAsync` (mesma origem) e ajustar se divergir. `ITerminalSession Create(SessionRequest)` — confirmar a assinatura real de `ITerminalSessionProvider` (o fake do teste deve casar).

- [ ] **Step 4: Rodar e ver passar** — mesmo filtro → 3 passed.

- [ ] **Step 5: Commit**
```bash
git add src/RemoteOps.Desktop/Sessions/SessionLauncher.cs tests/RemoteOps.UnitTests/Desktop/Sessions/SessionLauncherTests.cs
git commit -m "feat(gui): SessionLauncher — fonte unica de abrir host por protocolo"
```

---

### Task 2: `GroupCardViewModel`

**Files:**
- Create: `src/RemoteOps.Desktop/ViewModels/GroupCardViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/GroupCardViewModelTests.cs`

**Interfaces:**
- Produces: `GroupCardViewModel` — ctor(`string id`, `string name`, `int hostCount`); props `Id`, `Name`, `HostCount`, `HostCountLabel`.

- [ ] **Step 1: Teste que falha**
```csharp
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class GroupCardViewModelTests
{
    [Fact]
    public void HostCountLabel_FormatsCount()
    {
        Assert.Equal("1 host", new GroupCardViewModel("g1", "Innet", 1).HostCountLabel);
        Assert.Equal("10 hosts", new GroupCardViewModel("g2", "Serra", 10).HostCountLabel);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar**
```csharp
namespace RemoteOps.Desktop.ViewModels;

public sealed class GroupCardViewModel
{
    public GroupCardViewModel(string id, string name, int hostCount)
    {
        Id = id;
        Name = name;
        HostCount = hostCount;
    }

    public string Id { get; }
    public string Name { get; }
    public int HostCount { get; }
    public string HostCountLabel => HostCount == 1 ? "1 host" : $"{HostCount} hosts";
}
```
- [ ] **Step 4: Rodar e ver passar.**
- [ ] **Step 5: Commit** — `feat(gui): GroupCardViewModel`.

---

### Task 3: `HostsViewModel` (grid → drill → conectar)

**Files:**
- Create: `src/RemoteOps.Desktop/ViewModels/HostsViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/HostsViewModelTests.cs`

**Interfaces:**
- Consumes: `ILocalStore`, `SessionLauncher`, `GroupCardViewModel`, `AssetViewModel`, `RelayCommand`.
- Produces: `HostsViewModel(ILocalStore store, SessionLauncher launcher, string workspaceId)`; `ObservableCollection<GroupCardViewModel> Groups`; `ObservableCollection<AssetViewModel> Hosts`; `GroupCardViewModel? CurrentGroup`; `bool IsInGroup`; `string SearchText`; `AssetViewModel? SelectedHost`; commands `OpenGroupCommand`, `BackCommand`, `ConnectPrimaryCommand`, `ConnectCommand`(param protocolo), `NewGroupCommand`, `NewHostCommand`, `EditHostCommand`, `DeleteHostCommand`; events `NewHostRequested`, `EditHostRequested`(AssetViewModel), `NewGroupRequested`; `Task LoadAsync()`.

- [ ] **Step 1: Teste que falha** — `HostsViewModelTests.cs`:
```csharp
using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class HostsViewModelTests
{
    private static SessionLauncher Launcher(TabsViewModel tabs) => new(tabs, null, null, null, null, null, null);

    [Fact]
    public async Task LoadAsync_BuildsGroupCardsWithCounts()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Innet");
        await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-local", GroupId = g.Id, Name = "r1" });
        await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-local", GroupId = g.Id, Name = "r2" });
        var vm = new HostsViewModel(store, Launcher(new TabsViewModel()), "ws-local");

        await vm.LoadAsync();

        var card = vm.Groups.Single(c => c.Name == "Innet");
        Assert.Equal(2, card.HostCount);
        Assert.False(vm.IsInGroup);
    }

    [Fact]
    public async Task OpenGroup_LoadsHosts_AndBackReturns()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Innet");
        await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-local", GroupId = g.Id, Name = "r1" });
        var vm = new HostsViewModel(store, Launcher(new TabsViewModel()), "ws-local");
        await vm.LoadAsync();

        vm.OpenGroupCommand.Execute(vm.Groups.Single());
        await Task.Delay(20);
        Assert.True(vm.IsInGroup);
        Assert.Single(vm.Hosts);

        vm.BackCommand.Execute(null);
        Assert.False(vm.IsInGroup);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar `HostsViewModel.cs`:**
```csharp
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;

namespace RemoteOps.Desktop.ViewModels;

public sealed class HostsViewModel : BaseViewModel
{
    private readonly ILocalStore _store;
    private readonly SessionLauncher _launcher;
    private readonly string _workspaceId;
    private GroupCardViewModel? _currentGroup;
    private AssetViewModel? _selectedHost;
    private string _searchText = string.Empty;

    public HostsViewModel(ILocalStore store, SessionLauncher launcher, string workspaceId)
    {
        _store = store;
        _launcher = launcher;
        _workspaceId = workspaceId;

        OpenGroupCommand = new RelayCommand(obj => { if (obj is GroupCardViewModel g) _ = OpenGroupAsync(g); });
        BackCommand = new RelayCommand(() => CurrentGroup = null, () => IsInGroup);
        ConnectPrimaryCommand = new RelayCommand(obj => { if (obj is AssetViewModel a) _ = _launcher.LaunchAsync(a.Asset, _launcher.PrimaryProtocol(a.Asset)); });
        ConnectCommand = new RelayCommand(obj => { if (SelectedHost != null && obj is string p) _ = _launcher.LaunchAsync(SelectedHost.Asset, p); }, _ => SelectedHost != null);
        NewGroupCommand = new RelayCommand(() => NewGroupRequested?.Invoke(this, EventArgs.Empty));
        NewHostCommand = new RelayCommand(() => NewHostRequested?.Invoke(this, CurrentGroup?.Id));
        EditHostCommand = new RelayCommand(() => { if (SelectedHost != null) EditHostRequested?.Invoke(this, SelectedHost); }, _ => SelectedHost != null);
        DeleteHostCommand = new RelayCommand(() => _ = DeleteHostAsync(), _ => SelectedHost != null);
    }

    public ObservableCollection<GroupCardViewModel> Groups { get; } = [];
    public ObservableCollection<AssetViewModel> Hosts { get; } = [];

    public RelayCommand OpenGroupCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand ConnectPrimaryCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand NewGroupCommand { get; }
    public RelayCommand NewHostCommand { get; }
    public RelayCommand EditHostCommand { get; }
    public RelayCommand DeleteHostCommand { get; }

    public event EventHandler<string?>? NewHostRequested;
    public event EventHandler<AssetViewModel>? EditHostRequested;
    public event EventHandler? NewGroupRequested;

    public GroupCardViewModel? CurrentGroup
    {
        get => _currentGroup;
        private set
        {
            Set(ref _currentGroup, value);
            RaisePropertyChanged(nameof(IsInGroup));
            RaisePropertyChanged(nameof(BreadcrumbLabel));
            BackCommand.RaiseCanExecuteChanged();
            if (value is null) Hosts.Clear();
        }
    }

    public bool IsInGroup => _currentGroup != null;
    public string BreadcrumbLabel => _currentGroup is null ? "Grupos" : $"Grupos / {_currentGroup.Name}";

    public AssetViewModel? SelectedHost
    {
        get => _selectedHost;
        set
        {
            Set(ref _selectedHost, value);
            ConnectCommand.RaiseCanExecuteChanged();
            EditHostCommand.RaiseCanExecuteChanged();
            DeleteHostCommand.RaiseCanExecuteChanged();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set { Set(ref _searchText, value); _ = RefreshAsync(); }
    }

    public async Task LoadAsync()
    {
        CurrentGroup = null;
        Groups.Clear();
        var groups = await _store.GetGroupsAsync(_workspaceId);
        var filter = _searchText.Trim();
        foreach (var g in groups)
        {
            if (filter.Length > 0 && !g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var count = (await _store.GetAssetsAsync(_workspaceId, g.Id)).Count;
            Groups.Add(new GroupCardViewModel(g.Id, g.Name, count));
        }
    }

    private async Task OpenGroupAsync(GroupCardViewModel group)
    {
        CurrentGroup = group;
        await LoadHostsAsync();
    }

    private async Task LoadHostsAsync()
    {
        Hosts.Clear();
        if (_currentGroup is null) return;
        var assets = await _store.GetAssetsAsync(_workspaceId, _currentGroup.Id);
        var filter = _searchText.Trim();
        foreach (var a in assets)
        {
            if (filter.Length > 0 && !a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !string.Join(" ", a.Tags).Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Hosts.Add(new AssetViewModel(a));
        }
    }

    private Task RefreshAsync() => IsInGroup ? LoadHostsAsync() : LoadAsync();

    private async Task DeleteHostAsync()
    {
        if (SelectedHost is null) return;
        await _store.DeleteAssetAsync(SelectedHost.Id);
        Hosts.Remove(SelectedHost);
        SelectedHost = null;
    }

    public Task ReloadAfterEditAsync() => RefreshAsync();
}
```
- [ ] **Step 4: Rodar e ver passar.**
- [ ] **Step 5: Commit** — `feat(gui): HostsViewModel — grid de grupos, drill-down e conexao`.

---

### Task 4: `HostEditorViewModel` (criar/editar host + endpoints)

**Files:**
- Create: `src/RemoteOps.Desktop/ViewModels/HostEditorViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/HostEditorViewModelTests.cs`

**Interfaces:**
- Produces: `HostEditorViewModel(ILocalStore store, string workspaceId, Asset? existing, string? groupId)`; props `Name`, `NewEndpointProtocol`, `NewEndpointAddress`, `NewEndpointPort`, `ObservableCollection<Endpoint> Endpoints`; commands `AddEndpointCommand`, `RemoveEndpointCommand`, `SaveCommand`; event `Saved`; `Task SaveAsync()`.

- [ ] **Step 1: Teste que falha**
```csharp
using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class HostEditorViewModelTests
{
    [Fact]
    public async Task Save_CreatesHostWithEndpoint()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Innet");
        var vm = new HostEditorViewModel(store, "ws-local", existing: null, groupId: g.Id)
        {
            Name = "r1",
            NewEndpointProtocol = "ssh",
            NewEndpointAddress = "10.0.0.1",
            NewEndpointPort = 22,
        };
        vm.AddEndpointCommand.Execute(null);

        await vm.SaveAsync();

        var assets = await store.GetAssetsAsync("ws-local", g.Id);
        var created = assets.Single(a => a.Name == "r1");
        Assert.Single(created.Endpoints);
        Assert.Equal("ssh", created.Endpoints[0].Protocol);
    }
}
```
- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar** (reusa a lógica de `InspectorViewModel.AddEndpointAsync`/resolução de porta; persiste com `AddAssetAsync` + `AddEndpointAsync`, ou `UpdateAssetAsync` no modo edição). Código completo:
```csharp
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
        }
        AddEndpointCommand = new RelayCommand(AddEndpoint, () => !string.IsNullOrWhiteSpace(NewEndpointAddress));
        RemoveEndpointCommand = new RelayCommand(obj => { if (obj is Endpoint ep) Endpoints.Remove(ep); });
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !string.IsNullOrWhiteSpace(Name));
    }

    public bool IsEdit => _existing != null;
    public string Title => IsEdit ? "Editar host" : "Novo host";
    public ObservableCollection<Endpoint> Endpoints { get; } = [];

    public string Name { get => _name; set { Set(ref _name, value); SaveCommand.RaiseCanExecuteChanged(); } }
    public string NewEndpointProtocol { get => _newEndpointProtocol; set { Set(ref _newEndpointProtocol, value); NewEndpointPort = value switch { "ssh" => 22, "telnet" => 23, "rdp" => 3389, "mikrotik" => 8291, _ => NewEndpointPort }; } }
    public string NewEndpointAddress { get => _newEndpointAddress; set { Set(ref _newEndpointAddress, value); AddEndpointCommand.RaiseCanExecuteChanged(); } }
    public int NewEndpointPort { get => _newEndpointPort; set => Set(ref _newEndpointPort, value); }

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
        });
        NewEndpointAddress = string.Empty;
    }

    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name)) return;
        if (_existing is null)
        {
            var asset = await _store.AddAssetAsync(new AddAssetRequest { WorkspaceId = _workspaceId, GroupId = _groupId, Name = Name.Trim() });
            foreach (var ep in Endpoints)
                await _store.AddEndpointAsync(ep with { }); // ep é imutável; recria com AssetId real abaixo
        }
        else
        {
            await _store.UpdateAssetAsync(new Asset { Id = _existing.Id, WorkspaceId = _workspaceId, GroupId = _groupId, Name = Name.Trim(), Tags = _existing.Tags, Version = _existing.Version });
        }
        Saved?.Invoke(this, EventArgs.Empty);
    }
}
```
> `Endpoint` é `sealed` sem `record` — não tem `with`. No modo criar, recriar cada `Endpoint` com `AssetId = asset.Id` antes de `AddEndpointAsync` (ajustar no Step 3 conforme a assinatura real; o teste exige que o endpoint apareça no asset criado). No modo editar, reconciliar endpoints adicionados/removidos com `AddEndpointAsync`/`DeleteEndpointAsync`.

- [ ] **Step 4: Rodar e ver passar** (ajustar a criação de endpoint até o teste passar).
- [ ] **Step 5: Commit** — `feat(gui): HostEditorViewModel — criar/editar host e endpoints`.

---

### Task 5: `KeychainViewModel` (lista read-only)

**Files:**
- Create: `src/RemoteOps.Desktop/ViewModels/KeychainViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/KeychainViewModelTests.cs`

**Interfaces:**
- Produces: `KeychainViewModel(ILocalStore store, string workspaceId)`; `ObservableCollection<CredentialRef> Credentials`; `Task LoadAsync()`.

- [ ] **Step 1: Teste que falha**
```csharp
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class KeychainViewModelTests
{
    [Fact]
    public async Task LoadAsync_ListsCredentialRefs()
    {
        var store = new InMemoryLocalStore();
        await store.AddCredentialRefAsync(new CredentialRef { Id = "c1", Name = "root", Type = "password" });
        var vm = new KeychainViewModel(store, "ws-local");

        await vm.LoadAsync();

        Assert.Contains(vm.Credentials, c => c.Name == "root");
    }
}
```
- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar**
```csharp
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
```
- [ ] **Step 4: Rodar e ver passar.**
- [ ] **Step 5: Commit** — `feat(gui): KeychainViewModel — lista read-only de credenciais`.

---

### Task 6: `IUiLogSink` + `LogsViewModel`

**Files:**
- Create: `src/RemoteOps.Desktop/Infrastructure/IUiLogSink.cs`, `src/RemoteOps.Desktop/ViewModels/LogsViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/LogsViewModelTests.cs`

**Interfaces:**
- Produces: `IUiLogSink { void Emit(string line); }`; `LogsViewModel : IUiLogSink` — `ObservableCollection<string> Events`, `void Emit(string)`.

- [ ] **Step 1: Teste que falha**
```csharp
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class LogsViewModelTests
{
    [Fact]
    public void Emit_AppendsEvent()
    {
        var vm = new LogsViewModel();
        vm.Emit("sessão aberta: r1 (ssh)");
        Assert.Contains("sessão aberta: r1 (ssh)", vm.Events);
    }
}
```
- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar** `IUiLogSink.cs`:
```csharp
namespace RemoteOps.Desktop.Infrastructure;

public interface IUiLogSink
{
    void Emit(string line);
}
```
`LogsViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using RemoteOps.Desktop.Infrastructure;

namespace RemoteOps.Desktop.ViewModels;

public sealed class LogsViewModel : BaseViewModel, IUiLogSink
{
    public ObservableCollection<string> Events { get; } = [];

    public void Emit(string line)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.Invoke(() => Events.Insert(0, line));
        else
            Events.Insert(0, line);
    }
}
```
- [ ] **Step 4: Rodar e ver passar** (o teste roda sem Application.Current → cai no else).
- [ ] **Step 5: Commit** — `feat(gui): IUiLogSink + LogsViewModel (eventos da sessao)`.

---

### Task 7: `BrowserViewModel` (rail + seções + menu da conta)

**Files:**
- Create: `src/RemoteOps.Desktop/ViewModels/BrowserViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/BrowserViewModelTests.cs`

**Interfaces:**
- Consumes: `HostsViewModel`, `KeychainViewModel`, `LogsViewModel`.
- Produces: `enum BrowserSection { Hosts, Keychain, Logs }`; `BrowserViewModel(HostsViewModel hosts, KeychainViewModel keychain, LogsViewModel logs)`; `BrowserSection ActiveSection`; bools `IsHosts/IsKeychain/IsLogs`; commands `ShowHostsCommand/ShowKeychainCommand/ShowLogsCommand`, `OpenSettingsCommand/CheckUpdatesCommand/AboutCommand`; events `SettingsRequested/UpdatesRequested/AboutRequested`.

- [ ] **Step 1: Teste que falha**
```csharp
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class BrowserViewModelTests
{
    private static BrowserViewModel Build()
    {
        var store = new InMemoryLocalStore();
        var hosts = new HostsViewModel(store, new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null), "ws-local");
        return new BrowserViewModel(hosts, new KeychainViewModel(store, "ws-local"), new LogsViewModel());
    }

    [Fact]
    public void ShowKeychain_SwitchesSection()
    {
        var vm = Build();
        Assert.True(vm.IsHosts);
        vm.ShowKeychainCommand.Execute(null);
        Assert.True(vm.IsKeychain);
        Assert.False(vm.IsHosts);
    }

    [Fact]
    public void OpenSettings_RaisesEvent()
    {
        var vm = Build();
        bool raised = false;
        vm.SettingsRequested += (_, _) => raised = true;
        vm.OpenSettingsCommand.Execute(null);
        Assert.True(raised);
    }
}
```
- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar** `BrowserViewModel.cs`:
```csharp
using System;

namespace RemoteOps.Desktop.ViewModels;

public enum BrowserSection { Hosts, Keychain, Logs }

public sealed class BrowserViewModel : BaseViewModel
{
    private BrowserSection _activeSection = BrowserSection.Hosts;

    public BrowserViewModel(HostsViewModel hosts, KeychainViewModel keychain, LogsViewModel logs)
    {
        Hosts = hosts;
        Keychain = keychain;
        Logs = logs;
        ShowHostsCommand = new RelayCommand(() => ActiveSection = BrowserSection.Hosts);
        ShowKeychainCommand = new RelayCommand(() => { ActiveSection = BrowserSection.Keychain; _ = keychain.LoadAsync(); });
        ShowLogsCommand = new RelayCommand(() => ActiveSection = BrowserSection.Logs);
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        CheckUpdatesCommand = new RelayCommand(() => UpdatesRequested?.Invoke(this, EventArgs.Empty));
        AboutCommand = new RelayCommand(() => AboutRequested?.Invoke(this, EventArgs.Empty));
    }

    public HostsViewModel Hosts { get; }
    public KeychainViewModel Keychain { get; }
    public LogsViewModel Logs { get; }

    public BrowserSection ActiveSection
    {
        get => _activeSection;
        private set
        {
            Set(ref _activeSection, value);
            RaisePropertyChanged(nameof(IsHosts));
            RaisePropertyChanged(nameof(IsKeychain));
            RaisePropertyChanged(nameof(IsLogs));
        }
    }

    public bool IsHosts => _activeSection == BrowserSection.Hosts;
    public bool IsKeychain => _activeSection == BrowserSection.Keychain;
    public bool IsLogs => _activeSection == BrowserSection.Logs;

    public RelayCommand ShowHostsCommand { get; }
    public RelayCommand ShowKeychainCommand { get; }
    public RelayCommand ShowLogsCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand AboutCommand { get; }

    public event EventHandler? SettingsRequested;
    public event EventHandler? UpdatesRequested;
    public event EventHandler? AboutRequested;
}
```
- [ ] **Step 4: Rodar e ver passar.**
- [ ] **Step 5: Commit** — `feat(gui): BrowserViewModel — rail (Hosts/Keychain/Logs) + menu da conta`.

---

### Task 8: `WorkspaceViewModel` (browser + abas de sessão)

**Files:**
- Create: `src/RemoteOps.Desktop/ViewModels/WorkspaceViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/WorkspaceViewModelTests.cs`

**Interfaces:**
- Consumes: `BrowserViewModel`, `TabsViewModel`, `HostsViewModel`.
- Produces: `WorkspaceViewModel(BrowserViewModel browser, TabsViewModel tabs)`; props `Browser`, `Tabs`; `Task InitializeAsync()` (carrega os grupos).

- [ ] **Step 1: Teste que falha**
```csharp
using System.Threading.Tasks;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class WorkspaceViewModelTests
{
    [Fact]
    public async Task Initialize_LoadsGroups()
    {
        var store = new InMemoryLocalStore();
        await store.AddGroupAsync("ws-local", "Innet");
        var hosts = new HostsViewModel(store, new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null), "ws-local");
        var browser = new BrowserViewModel(hosts, new KeychainViewModel(store, "ws-local"), new LogsViewModel());
        var vm = new WorkspaceViewModel(browser, new TabsViewModel());

        await vm.InitializeAsync();

        Assert.NotEmpty(vm.Browser.Hosts.Groups);
    }
}
```
- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar**
```csharp
using System.Threading.Tasks;

namespace RemoteOps.Desktop.ViewModels;

public sealed class WorkspaceViewModel : BaseViewModel
{
    public WorkspaceViewModel(BrowserViewModel browser, TabsViewModel tabs)
    {
        Browser = browser;
        Tabs = tabs;
    }

    public BrowserViewModel Browser { get; }
    public TabsViewModel Tabs { get; }

    public Task InitializeAsync() => Browser.Hosts.LoadAsync();
}
```
- [ ] **Step 4: Rodar e ver passar.**
- [ ] **Step 5: Commit** — `feat(gui): WorkspaceViewModel — browser + abas de sessao`.

---

### Task 9: Views — HostsView, KeychainView, LogsView (conteúdo do rail)

**Files:**
- Create: `src/RemoteOps.Desktop/Views/HostsView.xaml(.cs)`, `KeychainView.xaml(.cs)`, `LogsView.xaml(.cs)`

Estas são views WPF novas, tematizadas com `DynamicResource` (tokens de `Themes/`). Sem lógica de negócio no code-behind (só `InitializeComponent` + o handler de duplo-clique da lista de hosts). Detalhe de cada:

- [ ] **Step 1: `HostsView.xaml`** — toolbar (busca ligada a `SearchText` + botão "Novo host" com dropdown "Novo grupo") + breadcrumb (`BreadcrumbLabel` + botão Voltar visível por `IsInGroup`) + conteúdo condicional: `ItemsControl` de cards (`Groups`, `WrapPanel`/`UniformGrid`, card = `OpenGroupCommand` no clique) quando `IsInGroup=False`; `DataGrid` de `Hosts` (`SelectedHost` TwoWay, duplo-clique → `ConnectPrimaryCommand`, `ContextMenu` com `ConnectCommand` ssh/telnet/rdp + WinBox + Editar + Excluir) quando `IsInGroup=True`. Estados vazios. (XAML completo tematizado; usar `BooleanToVisibilityConverter` para alternar por `IsInGroup`.)
- [ ] **Step 2: `HostsView.xaml.cs`** — handler `HostRow_MouseDoubleClick` que executa `ConnectPrimaryCommand` com o `AssetViewModel` da linha; `Row_PreviewMouseRightButtonDown` que seleciona a linha (mesmo padrão da HostListView atual).
- [ ] **Step 3: `KeychainView.xaml(.cs)`** — `DataGrid`/lista read-only de `Credentials` (Name, Type, Metadata.Username) + aviso "gerenciamento completo em breve".
- [ ] **Step 4: `LogsView.xaml(.cs)`** — `ItemsControl`/`ListBox` de `Events` (monoespaçado) + aviso "histórico persistente em breve".
- [ ] **Step 5: Build** — `dotnet build ... --nologo` → 0/0.
- [ ] **Step 6: Commit** — `feat(gui): views Hosts/Keychain/Logs (conteudo do rail)`.

> Nota de implementação: os XAML devem usar apenas tokens existentes (`Brush.Bg.*`, `Brush.Text.*`, `Brush.Border.*`, `Brush.Accent.*`, `Text.*`). Referência de estilo: o mockup aprovado e as views já tematizadas em `Views/`.

---

### Task 10: `HostEditorDialog` (diálogo)

**Files:**
- Create: `src/RemoteOps.Desktop/Views/HostEditorDialog.xaml(.cs)`

- [ ] **Step 1: `HostEditorDialog.xaml`** — `Window` modal tematizado: `Title` = `{Binding Title}`; campo Nome; lista de `Endpoints` (protocolo/endereço/porta + botão remover via `RemoveEndpointCommand`); linha "adicionar endpoint" (`NewEndpointProtocol` combo ssh/telnet/rdp/mikrotik + `NewEndpointAddress` + `NewEndpointPort` + `AddEndpointCommand`); botões Salvar (`SaveCommand`)/Cancelar.
- [ ] **Step 2: `HostEditorDialog.xaml.cs`** — ctor(`HostEditorViewModel`); `viewModel.Saved += (_,_) => { DialogResult = true; Close(); }`.
- [ ] **Step 3: Build** → 0/0.
- [ ] **Step 4: Commit** — `feat(gui): HostEditorDialog (novo/editar host)`.

---

### Task 11: `BrowserView` (rail + conteúdo + menu da conta)

**Files:**
- Create: `src/RemoteOps.Desktop/Views/BrowserView.xaml(.cs)`

- [ ] **Step 1: `BrowserView.xaml`** — `DockPanel`: rail à esquerda (largura ~70px, `Brush.Bg.Surface`): 3 itens (Hosts/Keychain/Logs) como `RadioButton`/`ToggleButton` ligados a `ShowHostsCommand`/`ShowKeychainCommand`/`ShowLogsCommand` (item ativo por `IsHosts/IsKeychain/IsLogs`), avatar no rodapé abrindo um `ContextMenu`/`Popup` com Configurações (`OpenSettingsCommand`), Verificar atualizações (`CheckUpdatesCommand`), Sobre (`AboutCommand`). Conteúdo à direita: alterna `HostsView`(DataContext=`Hosts`)/`KeychainView`(=`Keychain`)/`LogsView`(=`Logs`) por `Visibility` conforme a seção ativa.
- [ ] **Step 2: `BrowserView.xaml.cs`** — só `InitializeComponent`.
- [ ] **Step 3: Build** → 0/0.
- [ ] **Step 4: Commit** — `feat(gui): BrowserView (rail + menu da conta)`.

---

### Task 12: Shell — `MainWindow` vira TabControl (Hosts fixo + sessões) + fiação

**Files:**
- Modify: `src/RemoteOps.Desktop/MainWindow.xaml`, `MainWindow.xaml.cs`
- Modify: `src/RemoteOps.Desktop/Integration/AppCompositionRoot.cs`, `src/RemoteOps.Desktop/App.xaml.cs`

**Interfaces:**
- Consumes: `WorkspaceViewModel`, `BrowserView`, `TabsView`, `SessionLauncher`, `HostEditorDialog`, `HostEditorViewModel`, `SettingsWindow`.

- [ ] **Step 1: DI (`AppCompositionRoot`).** Registrar: `SessionLauncher` (resolvendo `TabsViewModel` singleton + providers/winbox/flags/rdpCred), `TabsViewModel` (singleton), `LogsViewModel` (singleton, também registrado como `IUiLogSink`), `HostsViewModel`/`KeychainViewModel`/`BrowserViewModel`/`WorkspaceViewModel` (singletons, workspaceId `"ws-local"`). Manter o registro de `MainViewModel` só até a Task 13 (ou já trocar por `WorkspaceViewModel` aqui).
- [ ] **Step 2: `MainWindow.xaml`** — substituir todo o corpo (DockPanel+Menu+Grid) por um `TabControl` cujo `ItemsSource`/estrutura tem: uma aba fixa "Hosts" (não fechável) com `BrowserView` (DataContext=`Browser`) + as abas de sessão de `Tabs.Tabs` (reusar o `TabsView`/templates existentes de aba). Remover `Window.InputBindings` do Ctrl+W dependente de `Tabs` só se necessário (manter se `Tabs` continuar exposto). Sem `Menu`.
- [ ] **Step 3: `MainWindow.xaml.cs`** — ctor(`WorkspaceViewModel`); `Loaded += async (_,_) => await vm.InitializeAsync();`. Assinar os eventos do browser: `SettingsRequested`→abrir `SettingsWindow(vm.CreateSettingsViewModel())` — mover `CreateSettingsViewModel()`/`AppVersionText` para `WorkspaceViewModel` (ou um serviço) já que `MainViewModel` sai na Task 13; `UpdatesRequested`/`AboutRequested` idem. Assinar `Browser.Hosts.NewHostRequested/EditHostRequested/NewGroupRequested` → abrir `HostEditorDialog`/diálogo de grupo e, no `Saved`, `await Browser.Hosts.ReloadAfterEditAsync()`.
- [ ] **Step 4: `App.xaml.cs`** — trocar `_serviceProvider.GetRequiredService<MainViewModel>()` + `new MainWindow(mainViewModel)` por `WorkspaceViewModel` + `new MainWindow(workspace)`. Manter o try/catch do OnStartup.
- [ ] **Step 5: Build** → 0/0. Smoke: app abre na aba Hosts com o grid; duplo-clique num host abre aba de sessão; menu da conta abre Configurações.
- [ ] **Step 6: Commit** — `feat(gui): shell com abas (Hosts fixo + sessoes) e fiacao do browser`.

---

### Task 13: Aposentar Sidebar/HostList/Inspector/MainViewModel + migrar testes

**Files:**
- Delete: `Views/SidebarView.*`, `Views/HostListView.*`, `Views/InspectorView.*`, `ViewModels/SidebarViewModel.cs`, `ViewModels/HostListViewModel.cs`, `ViewModels/InspectorViewModel.cs`, `ViewModels/MainViewModel.cs`
- Delete/migrar: `tests/.../HostListViewModelConnectTests.cs` (a cobertura de conexão agora vive em `SessionLauncherTests`); remover testes que referenciam os VMs deletados.
- Modify: qualquer referência remanescente (ex.: `OpenSessionRequest` — se estava em `InspectorViewModel.cs`, mover a `record`/`class` para `Sessions/OpenSessionRequest.cs` antes de deletar).

- [ ] **Step 1: Mover `OpenSessionRequest`** (se declarado em `InspectorViewModel.cs`) para `src/RemoteOps.Desktop/Sessions/OpenSessionRequest.cs`, ou remover se não for mais usado (o `SessionLauncher` usa `Asset`+protocolo, não `OpenSessionRequest`).
- [ ] **Step 2: Deletar** as views/VMs antigos listados.
- [ ] **Step 3: Remover/migrar testes** que referenciam os deletados (`HostListViewModelConnectTests`, e quaisquer `MainViewModel`/`InspectorViewModel`/`SidebarViewModel` tests). Confirmar que a cobertura equivalente existe (SessionLauncher/HostsViewModel/HostEditorViewModel).
- [ ] **Step 4: Build + suite completa** — `dotnet test ... --nologo` → 0/0, todos verdes.
- [ ] **Step 5: Commit** — `refactor(gui): remove Sidebar/HostList/Inspector/MainViewModel (migrado p/ browser)`.

---

### Task 14: Validação final + instalador

**Files:** nenhuma alteração de código.

- [ ] **Step 1: Suite completa** — `dotnet test "C:\dev\remoteops-termius\RemoteOps.sln" -c Debug --nologo` → 0/0, todos verdes.
- [ ] **Step 2: Smoke manual** — app abre na aba Hosts (grid de grupos com contagem); clicar num grupo lista hosts (breadcrumb + voltar); duplo-clique conecta (abre aba de sessão); clique-direito oferece SSH/Telnet/RDP/WinBox; Novo host/Novo grupo/editar/excluir funcionam; rail alterna Hosts/Keychain/Logs; menu da conta abre Configurações/Atualizações/Sobre; barra de menus e 3 painéis sumiram.
- [ ] **Step 3: Instalador** — `dotnet publish ...RemoteOps.Desktop.csproj -c Release -p:PublishProfile=win-x64-velopack -o publish\win-x64` + `vpk pack --packId RemoteOpsDesktop --packVersion 0.13.0 --packDir publish\win-x64 --mainExe RemoteOps.Desktop.exe --packTitle "RemoteOps Desktop" --icon src\RemoteOps.Desktop\assets\appicon.ico --outputDir Releases`.
- [ ] **Step 4: Commit** — `chore(gui): valida navegacao Termius (Fase 1) + instalador v0.13.0`.

---

## Self-Review (executada na escrita)

**Cobertura da spec:** §4.1 shell→Task 12 · §4.2 browser→Task 11 · §4.3 rail→Task 11 · §4.4 grid/drill/conectar→Tasks 2,3,9 · §4.5 novo/editar host→Tasks 4,10 · §4.6 Keychain→Tasks 5,9 · §4.7 Logs→Tasks 6,9 · §4.8 menu da conta→Tasks 7,11,12 · §4.9 aposentar/manter→Task 13 · §4.10 VMs/fluxo→Tasks 1-8 · §5 testes→cada task TDD + Task 14 · fonte única de sessão→Task 1.

**Placeholders:** os avisos "em breve" (Keychain/Logs) são copy intencional de features diferidas (Fase 1b), não placeholders de plano. Os pontos marcados "ajustar conforme a assinatura real" (SessionLauncher WinBox types; HostEditor recriação de Endpoint) apontam código concreto a confirmar contra arquivos existentes citados — não são TODOs vagos.

**Consistência de tipos:** `SessionLauncher`(Task 1) consumido por `HostsViewModel`(3) e Tasks 7,8,12 com a mesma assinatura; `GroupCardViewModel`(2)→`HostsViewModel`(3)/views(9); `HostsViewModel`/`KeychainViewModel`/`LogsViewModel`→`BrowserViewModel`(7)→`WorkspaceViewModel`(8)→shell(12); `IUiLogSink`(6) registrado no DI(12). `AssetViewModel`/`ILocalStore`/`Endpoint`/`CredentialRef` conferidos contra os arquivos reais.

## Execution Handoff
Ver a mensagem de handoff após salvar.
