using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop;

public sealed class HostListViewModelTests
{
    private static HostListViewModel Build(ILocalStore? store = null)
        => new(store ?? new InMemoryLocalStore(), "ws-test");

    [Fact]
    public async Task AddHost_AddsToCollection()
    {
        var vm = Build();
        vm.NewHostName = "router-core-01";

        await vm.AddHostAsync();

        Assert.Single(vm.Assets);
        Assert.Equal("router-core-01", vm.Assets[0].Name);
    }

    [Fact]
    public async Task AddHost_ClearsNameAfterAdd()
    {
        var vm = Build();
        vm.NewHostName = "sw-edge-01";

        await vm.AddHostAsync();

        Assert.Equal(string.Empty, vm.NewHostName);
    }

    [Fact]
    public async Task AddHost_EmptyName_DoesNotAdd()
    {
        var vm = Build();
        vm.NewHostName = "  ";

        await vm.AddHostAsync();

        Assert.Empty(vm.Assets);
    }

    [Fact]
    public async Task DeleteHost_RemovesSelected()
    {
        var vm = Build();
        vm.NewHostName = "fw-01";
        await vm.AddHostAsync();
        vm.SelectedAsset = vm.Assets[0];

        await vm.DeleteHostAsync();

        Assert.Empty(vm.Assets);
        Assert.Null(vm.SelectedAsset);
    }

    [Fact]
    public async Task LoadAsync_FiltersToGroup()
    {
        var store = new InMemoryLocalStore();
        var g1 = await store.AddGroupAsync("ws-test", "DC1");
        await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-test", GroupId = g1.Id, Name = "sw-dc1-01" });
        await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-test", Name = "sw-dmz-01" });

        var vm = Build(store);
        await vm.LoadAsync(g1.Id);

        Assert.Single(vm.Assets);
        Assert.Equal("sw-dc1-01", vm.Assets[0].Name);
    }

    [Fact]
    public async Task FilterText_FiltersResults()
    {
        var vm = Build();
        vm.NewHostName = "router-alfa";
        await vm.AddHostAsync();
        vm.NewHostName = "switch-beta";
        await vm.AddHostAsync();

        // Recarrega sem filtro para ter os dois
        await vm.LoadAsync();
        Assert.Equal(2, vm.Assets.Count);

        // Aplica filtro chamando LoadAsync diretamente (FilterText.set é fire-and-forget)
        vm.FilterText = "router";
        await vm.LoadAsync();

        Assert.Single(vm.Assets);
        Assert.Contains("router", vm.Assets[0].Name);
    }

    [Fact]
    public async Task AssetSelected_Event_FiredOnSelection()
    {
        var vm = Build();
        vm.NewHostName = "ntp-01";
        await vm.AddHostAsync();

        AssetViewModel? received = null;
        vm.AssetSelected += (_, a) => received = a;
        vm.SelectedAsset = vm.Assets[0];

        Assert.NotNull(received);
        Assert.Equal("ntp-01", received!.Name);
    }

    [Fact]
    public void DeleteCommand_WithoutSelection_CannotExecute()
    {
        var vm = Build();
        Assert.False(vm.DeleteHostCommand.CanExecute(null));
    }
}
