using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop;

public sealed class SidebarViewModelTests
{
    private static SidebarViewModel Build(ILocalStore? store = null)
        => new(store ?? new InMemoryLocalStore(), "ws-test");

    [Fact]
    public async Task AddGroup_AddsToCollection()
    {
        var vm = Build();
        vm.NewGroupName = "Servidores";

        await vm.AddGroupAsync();

        Assert.Single(vm.Groups);
        Assert.Equal("Servidores", vm.Groups[0].Name);
    }

    [Fact]
    public async Task AddGroup_ClearsNameAfterAdd()
    {
        var vm = Build();
        vm.NewGroupName = "Roteadores";

        await vm.AddGroupAsync();

        Assert.Equal(string.Empty, vm.NewGroupName);
    }

    [Fact]
    public async Task AddGroup_EmptyName_DoesNotAdd()
    {
        var vm = Build();
        vm.NewGroupName = "   ";

        await vm.AddGroupAsync();

        Assert.Empty(vm.Groups);
    }

    [Fact]
    public async Task DeleteGroup_RemovesSelected()
    {
        var vm = Build();
        vm.NewGroupName = "Switches";
        await vm.AddGroupAsync();
        vm.SelectedGroup = vm.Groups[0];

        await vm.DeleteGroupAsync();

        Assert.Empty(vm.Groups);
        Assert.Null(vm.SelectedGroup);
    }

    [Fact]
    public async Task LoadAsync_ReflectsStoredGroups()
    {
        var store = new InMemoryLocalStore();
        await store.AddGroupAsync("ws-test", "Core");
        await store.AddGroupAsync("ws-test", "Edge");

        var vm = Build(store);
        await vm.LoadAsync();

        Assert.Equal(2, vm.Groups.Count);
    }

    [Fact]
    public void DeleteCommand_WithoutSelection_CannotExecute()
    {
        var vm = Build();
        Assert.False(vm.DeleteGroupCommand.CanExecute(null));
    }

    [Fact]
    public async Task AddGroup_SelectsNewGroup()
    {
        var vm = Build();
        vm.NewGroupName = "DC1";

        await vm.AddGroupAsync();

        Assert.NotNull(vm.SelectedGroup);
        Assert.Equal("DC1", vm.SelectedGroup!.Name);
    }

    [Fact]
    public async Task GroupSelected_Event_FiredOnSelection()
    {
        var vm = Build();
        vm.NewGroupName = "DC1";
        await vm.AddGroupAsync();

        AssetGroupViewModel? received = null;
        vm.GroupSelected += (_, g) => received = g;
        vm.SelectedGroup = vm.Groups[0];

        Assert.NotNull(received);
        Assert.Equal("DC1", received!.Name);
    }
}
