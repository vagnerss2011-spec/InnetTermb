using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// Fase 2 do cloud sync: quando o sync baixa dados novos, a lista tem que recarregar SEM perder o
/// estado da UI. <see cref="HostsViewModel.ReconcileFromStoreAsync"/> re-busca do store e reconcilia
/// as coleções por id — estes testes fixam o que o operador NÃO pode perder: o grupo aberto, o host
/// selecionado (mesma instância → seleção do DataGrid preservada) e o chip de filtro ativo.
///
/// <para>Um <c>LoadAsync</c> cru zeraria tudo isso; por isso a reconciliação existe, e por isso ela
/// preserva a INSTÂNCIA (ReferenceEquals) em vez de recriar.</para>
/// </summary>
public sealed class HostsViewModelReconcileTests
{
    private static SessionLauncher Launcher() => new(new TabsViewModel(), null, null, null, null, null, null);

    private static HostsViewModel NewVm(ILocalStore store) => new(store, Launcher(), "ws-local");

    private static Task<Asset> AddHostAsync(
        InMemoryLocalStore store, string groupId, string name, string? role = null, params string[] tags)
        => store.AddAssetAsync(new AddAssetRequest
        {
            WorkspaceId = "ws-local",
            GroupId = groupId,
            Name = name,
            DeviceRole = role,
            Tags = [.. tags],
        });

    // ── Dentro de um grupo: adiciona/remove/atualiza preservando seleção + grupo ─────────────

    [Fact]
    public async Task Reconcile_AddsNewHost_PreservesOpenGroup_And_SelectedInstance()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Innet");
        await AddHostAsync(store, g.Id, "r1");
        await AddHostAsync(store, g.Id, "r2");
        var vm = NewVm(store);
        await vm.LoadAsync();
        vm.OpenGroupCommand.Execute(vm.Groups.Single());
        await Task.Delay(20);

        AssetViewModel selected = vm.Hosts.First(h => h.Name == "r1");
        vm.SelectedHost = selected;
        GroupCardViewModel openGroup = vm.CurrentGroup!;

        // Chega um host novo pelo sync.
        await AddHostAsync(store, g.Id, "r3");
        await vm.ReconcileFromStoreAsync();

        Assert.Equal(3, vm.Hosts.Count);
        Assert.Contains(vm.Hosts, h => h.Name == "r3");
        Assert.True(vm.IsInGroup);
        Assert.Same(openGroup, vm.CurrentGroup);           // MESMO grupo aberto
        Assert.Same(selected, vm.SelectedHost);            // MESMA instância selecionada
        Assert.Contains(selected, vm.Hosts);
    }

    [Fact]
    public async Task Reconcile_RemovesHost_GoneFromStore()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Innet");
        Asset a1 = await AddHostAsync(store, g.Id, "r1");
        await AddHostAsync(store, g.Id, "r2");
        var vm = NewVm(store);
        await vm.LoadAsync();
        vm.OpenGroupCommand.Execute(vm.Groups.Single());
        await Task.Delay(20);

        await store.DeleteAssetAsync(a1.Id);
        await vm.ReconcileFromStoreAsync();

        Assert.Single(vm.Hosts);
        Assert.DoesNotContain(vm.Hosts, h => h.Id == a1.Id);
    }

    [Fact]
    public async Task Reconcile_UpdatesChangedHost_InPlace_SameInstance()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Innet");
        Asset a1 = await AddHostAsync(store, g.Id, "r1");
        var vm = NewVm(store);
        await vm.LoadAsync();
        vm.OpenGroupCommand.Execute(vm.Groups.Single());
        await Task.Delay(20);
        AssetViewModel before = vm.Hosts.Single();

        // O sync reendereça/renomeia o mesmo host (mesmo id, versão nova).
        await store.UpdateAssetAsync(new Asset
        {
            Id = a1.Id,
            WorkspaceId = "ws-local",
            GroupId = g.Id,
            Name = "r1",
            Vendor = "Cisco",
            Version = 1,
        });
        await vm.ReconcileFromStoreAsync();

        AssetViewModel after = vm.Hosts.Single();
        Assert.Same(before, after);          // instância preservada (não recriada)
        Assert.Equal("Cisco", after.Vendor); // e o dado novo apareceu
    }

    [Fact]
    public async Task Reconcile_PreservesActiveFilterChip()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Innet");
        await AddHostAsync(store, g.Id, "router-1", DeviceRoles.Router);
        await AddHostAsync(store, g.Id, "switch-1", DeviceRoles.Switch);
        var vm = NewVm(store);
        await vm.LoadAsync();
        vm.OpenGroupCommand.Execute(vm.Groups.Single());
        await Task.Delay(20);

        // Filtra por "router".
        DeviceFilterChip routerChip = vm.DeviceFilters.First(f => f.Key == $"role:{DeviceRoles.Router}");
        vm.ApplyFilterCommand.Execute(routerChip);
        await Task.Delay(20);
        Assert.Single(vm.Hosts);
        Assert.Equal("router-1", vm.Hosts.Single().Name);

        // Chega OUTRO roteador pelo sync — o filtro ativo continua valendo.
        await AddHostAsync(store, g.Id, "router-2", DeviceRoles.Router);
        await vm.ReconcileFromStoreAsync();

        Assert.True(vm.DeviceFilters.First(f => f.Key == $"role:{DeviceRoles.Router}").IsActive);
        Assert.Equal(2, vm.Hosts.Count);                          // os dois roteadores
        Assert.All(vm.Hosts, h => Assert.Equal(DeviceRoles.Router, h.DeviceRole));
        Assert.DoesNotContain(vm.Hosts, h => h.Name == "switch-1"); // o switch segue filtrado
    }

    [Fact]
    public async Task Reconcile_WhenSelectedHostDeleted_ClearsSelection()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Innet");
        Asset a1 = await AddHostAsync(store, g.Id, "r1");
        var vm = NewVm(store);
        await vm.LoadAsync();
        vm.OpenGroupCommand.Execute(vm.Groups.Single());
        await Task.Delay(20);
        vm.SelectedHost = vm.Hosts.Single();

        await store.DeleteAssetAsync(a1.Id);
        await vm.ReconcileFromStoreAsync();

        Assert.Null(vm.SelectedHost);
        Assert.Empty(vm.Hosts);
    }

    // ── Nível de grupos ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_AtGroupList_AddsNewGroup()
    {
        var store = new InMemoryLocalStore();
        await store.AddGroupAsync("ws-local", "Innet");
        var vm = NewVm(store);
        await vm.LoadAsync();
        Assert.Single(vm.Groups);

        await store.AddGroupAsync("ws-local", "Serra");
        await vm.ReconcileFromStoreAsync();

        Assert.Equal(2, vm.Groups.Count);
        Assert.Contains(vm.Groups, c => c.Name == "Serra");
    }

    [Fact]
    public async Task Reconcile_UpdatesGroupHostCount_PreservingCardInstance()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Innet");
        await AddHostAsync(store, g.Id, "r1");
        var vm = NewVm(store);
        await vm.LoadAsync();
        GroupCardViewModel card = vm.Groups.Single();
        Assert.Equal(1, card.HostCount);

        await AddHostAsync(store, g.Id, "r2");
        await vm.ReconcileFromStoreAsync();

        Assert.Same(card, vm.Groups.Single()); // mesma instância do card
        Assert.Equal(2, card.HostCount);        // contagem atualizada in-place
    }

    [Fact]
    public async Task Reconcile_WhenOpenGroupDeleted_ReturnsToGroupList()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Temporário");
        await AddHostAsync(store, g.Id, "r1");
        var vm = NewVm(store);
        await vm.LoadAsync();
        vm.OpenGroupCommand.Execute(vm.Groups.Single());
        await Task.Delay(20);
        Assert.True(vm.IsInGroup);

        // Outro device apaga o grupo inteiro.
        await store.DeleteAssetAsync((await store.GetAssetsAsync("ws-local", g.Id)).Single().Id);
        await store.DeleteGroupAsync(g.Id);
        await vm.ReconcileFromStoreAsync();

        Assert.False(vm.IsInGroup);   // voltou pra lista de grupos, não travou "dentro" de um grupo morto
        Assert.Empty(vm.Groups);
    }

    // ── Offline / no-op: comportamento inalterado ────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_WithNoStoreChange_IsIdempotentNoOp()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Innet");
        await AddHostAsync(store, g.Id, "r1");
        await AddHostAsync(store, g.Id, "r2");
        var vm = NewVm(store);
        await vm.LoadAsync();
        vm.OpenGroupCommand.Execute(vm.Groups.Single());
        await Task.Delay(20);
        AssetViewModel[] before = [.. vm.Hosts];
        vm.SelectedHost = before[0];

        // Reconciliação sem NENHUMA mudança no store: nada some, nada duplica, seleção intacta.
        await vm.ReconcileFromStoreAsync();
        await vm.ReconcileFromStoreAsync();

        Assert.Equal(2, vm.Hosts.Count);
        Assert.Same(before[0], vm.Hosts[0]);
        Assert.Same(before[1], vm.Hosts[1]);
        Assert.Same(before[0], vm.SelectedHost);
    }
}
