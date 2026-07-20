using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.ViewModels;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// O aviso de conflito na barra tem de ser HONESTO e ACIONÁVEL. Antes: um número embutido no status
/// ("Sincronizado (18 conflito(s))"), cumulativo, sem tela, sem ação e sem jamais voltar a zero.
/// </summary>
public sealed class SyncStatusConflictTests
{
    private sealed class FakeController : ISyncController
    {
        public List<SyncConflictItem> Items { get; } = [];
        public int Dismissals { get; private set; }
        public bool ThrowOnLoad { get; set; }
        public bool ThrowOnDismiss { get; set; }

        public Task SyncNowAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SyncConflictItem>> GetConflictsAsync(int limit, CancellationToken ct = default)
            => ThrowOnLoad
                ? throw new InvalidOperationException("banco fora")
                : Task.FromResult<IReadOnlyList<SyncConflictItem>>(Items);

        public Task DismissConflictsAsync(CancellationToken ct = default)
        {
            if (ThrowOnDismiss) throw new InvalidOperationException("banco fora");
            Dismissals++;
            Items.Clear();
            return Task.CompletedTask;
        }
    }

    private static SyncConflictItem Item(string id, string reason = "version_mismatch")
        => new("asset", id, DateTimeOffset.Now, reason);

    [Fact]
    public void No_Conflicts_Means_No_Warning()
    {
        var vm = new SyncStatusViewModel(new FakeController());
        vm.Apply(new SyncStatus(SyncState.Synced, 0));

        Assert.False(vm.HasConflicts);
        Assert.Equal(string.Empty, vm.ConflictText);
    }

    // O texto fala do EFEITO, não do jargão: "conflito" não diz a ninguém que uma edição se perdeu.
    [Theory]
    [InlineData(1, "1 alteração não subiu")]
    [InlineData(18, "18 alterações não subiram")]
    public void Warning_Explains_The_Effect(int count, string expected)
    {
        var vm = new SyncStatusViewModel(new FakeController());
        vm.Apply(new SyncStatus(SyncState.Synced, count));

        Assert.True(vm.HasConflicts);
        Assert.Equal(expected, vm.ConflictText);
    }

    // A contagem saiu do status principal — lá ela virava jargão e sugeria pendência sem ação.
    [Fact]
    public void Status_Text_No_Longer_Carries_The_Count()
    {
        var vm = new SyncStatusViewModel(new FakeController());
        vm.Apply(new SyncStatus(SyncState.Synced, 18));

        Assert.Equal("Sincronizado", vm.StatusText);
        Assert.DoesNotContain("18", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dismissing_Zeroes_The_Indicator()
    {
        var controller = new FakeController();
        var vm = new SyncStatusViewModel(controller);
        vm.Apply(new SyncStatus(SyncState.Synced, 18));

        await vm.DismissConflictsAsync();

        Assert.False(vm.HasConflicts);
        Assert.Equal(string.Empty, vm.ConflictText);
        Assert.Equal(1, controller.Dismissals);
    }

    // Falhar ao limpar não pode mentir zero: melhor continuar mostrando o que existe.
    [Fact]
    public async Task Failed_Dismiss_Keeps_The_Indicator()
    {
        var controller = new FakeController { ThrowOnDismiss = true };
        var vm = new SyncStatusViewModel(controller);
        vm.Apply(new SyncStatus(SyncState.Synced, 5));

        await vm.DismissConflictsAsync();

        Assert.True(vm.HasConflicts);
    }

    [Fact]
    public async Task Loading_Returns_The_Items()
    {
        var controller = new FakeController();
        controller.Items.Add(Item("host-a"));
        var vm = new SyncStatusViewModel(controller);

        IReadOnlyList<SyncConflictItem> items = await vm.LoadConflictsAsync();

        Assert.Single(items);
        Assert.Equal("host-a", items[0].EntityId);
    }

    [Fact]
    public async Task Loading_Never_Throws_To_The_UI()
    {
        var vm = new SyncStatusViewModel(new FakeController { ThrowOnLoad = true });

        Assert.Empty(await vm.LoadConflictsAsync());
    }

    [Fact]
    public async Task Without_Cloud_There_Is_Nothing_To_Load_Or_Dismiss()
    {
        var vm = new SyncStatusViewModel(controller: null);

        Assert.Empty(await vm.LoadConflictsAsync());
        await vm.DismissConflictsAsync(); // não pode lançar
    }

    // O texto do item é o que o operador realmente lê: precisa dizer o que aconteceu, em português.
    [Fact]
    public void Item_Explains_In_Plain_Portuguese()
    {
        var item = Item("host-a");

        Assert.Equal("Equipamento", item.TipoTexto);
        Assert.Contains("outro computador", item.MotivoTexto, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("não subiu", item.MotivoTexto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Secret_Conflict_Says_The_App_Never_Merges_Passwords()
    {
        var item = new SyncConflictItem("secret_envelope", "env-1", DateTimeOffset.Now, "secret_envelope");

        Assert.Equal("Senha", item.TipoTexto);
        Assert.Contains("nunca mescla", item.MotivoTexto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_Reason_Still_Says_Something_Useful()
    {
        var item = Item("h", reason: "algo_novo_do_servidor");

        Assert.Contains("não subiu", item.MotivoTexto, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("algo_novo_do_servidor", item.MotivoTexto, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_Timestamp_Does_Not_Show_A_Fake_Date()
    {
        var item = new SyncConflictItem("asset", "h", DateTimeOffset.MinValue, "version_mismatch");

        Assert.Equal("data desconhecida", item.QuandoTexto);
    }
}
