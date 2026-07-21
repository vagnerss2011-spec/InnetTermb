using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// O botão "Reenviar tudo para a nuvem" nas Configurações → Conta.
///
/// <para>O reenvio é uma operação de MASSA num acervo de centenas de equipamentos: por isso a
/// confirmação vem ANTES de qualquer coisa sair do PC, e por isso o operador vê progresso e um
/// resultado no fim. O que estes testes prendem é justamente isso — que clicar não dispara nada
/// sozinho, e que o fim da operação SEMPRE diz alguma coisa (sucesso, parcial ou falha).</para>
/// </summary>
public sealed class SettingsViewModelResyncTests
{
    private const string Ws = "ws-local";

    private sealed class FakeStore : ISettingsStore
    {
        private AppSettings _current = new();

        public AppSettings Load() => _current;

        public void Save(AppSettings settings) => _current = settings;
    }

    private sealed class FakeSyncController : ISyncController
    {
        public int Calls { get; private set; }

        public TaskCompletionSource? Gate { get; init; }

        public bool Fail { get; init; }

        // Espelha o controlador REAL: o orquestrador NÃO relança falha de rede (offline-first) — ele
        // a devolve como `false`. Um fake que lançasse aqui testaria um caminho de falha que a
        // produção nunca percorre, e o caminho real (false engolido em silêncio) ficaria sem teste.
        public async Task<bool> SyncNowAsync(CancellationToken ct = default)
        {
            Calls++;
            if (Gate is not null)
            {
                await Gate.Task;
            }

            return !Fail;
        }

        public Task<IReadOnlyList<SyncConflictItem>> GetConflictsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SyncConflictItem>>([]);

        public Task DismissConflictsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static async Task<InMemoryLocalStore> SeededStoreAsync()
    {
        var store = new InMemoryLocalStore();
        AssetGroup group = await store.AddGroupAsync(Ws, "POPs");
        Asset asset = await store.AddAssetAsync(new AddAssetRequest
        {
            WorkspaceId = Ws,
            Name = "olt-01",
            GroupId = group.Id,
        });
        await store.AddCredentialRefAsync(new CredentialRef
        {
            Id = "cred-1",
            Name = "admin NOC",
            Type = "password",
            Scope = Ws,
            SecretEnvelopeId = "env-1",
        });
        await store.AddEndpointAsync(new Endpoint
        {
            Id = "ep-1",
            AssetId = asset.Id,
            Protocol = "ssh",
            Ipv4 = "10.0.0.1",
            Port = 22,
            CredentialRefId = "cred-1",
        });
        return store;
    }

    private static async Task<(SettingsViewModel Vm, FakeSyncController Sync)> BuildAsync(
        bool withCloud = true, TaskCompletionSource? gate = null, bool failSync = false)
    {
        InMemoryLocalStore store = await SeededStoreAsync();
        var sync = new FakeSyncController { Gate = gate, Fail = failSync };
        var resync = new CloudResyncService(store, Ws, withCloud ? sync : null);
        return (new SettingsViewModel(new FakeStore(), resync: resync), sync);
    }

    [Fact]
    public async Task Without_Cloud_The_Button_Is_Off()
    {
        (SettingsViewModel vm, FakeSyncController sync) = await BuildAsync(withCloud: false);

        Assert.False(vm.CanResync);
        Assert.False(vm.ResyncCommand.CanExecute(null));
        Assert.Equal(0, sync.Calls);
    }

    /// <summary>
    /// Clicar NÃO reenvia: abre a confirmação. Um acervo inteiro subindo por um clique acidental
    /// seria exatamente o tipo de surpresa que este app não pode dar.
    /// </summary>
    [Fact]
    public async Task Clicking_Only_Asks_For_Confirmation()
    {
        (SettingsViewModel vm, FakeSyncController sync) = await BuildAsync();

        Assert.True(vm.ResyncCommand.CanExecute(null));
        vm.ResyncCommand.Execute(null);

        Assert.True(vm.IsResyncConfirmVisible);
        Assert.False(vm.IsResyncing);
        Assert.Equal(0, sync.Calls); // nada saiu do PC ainda
    }

    [Fact]
    public async Task Cancelling_Closes_The_Confirmation_And_Sends_Nothing()
    {
        (SettingsViewModel vm, FakeSyncController sync) = await BuildAsync();
        vm.ResyncCommand.Execute(null);

        vm.CancelResyncCommand.Execute(null);

        Assert.False(vm.IsResyncConfirmVisible);
        Assert.Equal(0, sync.Calls);
    }

    [Fact]
    public async Task Confirming_ReEmits_And_Reports_The_Result()
    {
        (SettingsViewModel vm, FakeSyncController sync) = await BuildAsync();
        vm.ResyncCommand.Execute(null);

        await vm.ResyncNowAsync();

        Assert.False(vm.IsResyncConfirmVisible); // a confirmação sai de cena ao começar
        Assert.False(vm.IsResyncing);
        Assert.Equal(2, sync.Calls); // pull antes + drenagem depois
        Assert.True(vm.HasResyncStatus);
        // 1 grupo + 1 ativo + 1 endpoint + 1 credencial
        Assert.Contains("4", vm.ResyncStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// Enquanto roda, o operador tem que ver que ESTÁ rodando — e não pode disparar um segundo
    /// reenvio por cima do primeiro.
    /// </summary>
    [Fact]
    public async Task While_Running_It_Says_So_And_Blocks_A_Second_Click()
    {
        var gate = new TaskCompletionSource();
        (SettingsViewModel vm, _) = await BuildAsync(gate: gate);
        vm.ResyncCommand.Execute(null);

        Task running = vm.ResyncNowAsync();

        Assert.True(vm.IsResyncing);
        Assert.False(vm.ResyncCommand.CanExecute(null));
        Assert.Contains("Reenviando", vm.ResyncStatus, StringComparison.Ordinal);

        gate.SetResult();
        await running;

        Assert.False(vm.IsResyncing);
        Assert.True(vm.ResyncCommand.CanExecute(null));
    }

    /// <summary>
    /// Rede fora no reenvio: a tela DIZ que não deu, em vez de voltar ao estado ocioso como se nada
    /// tivesse acontecido (falha silenciosa é a classe de defeito recorrente deste app). O fake
    /// devolve <c>false</c> — o MESMO sinal do controlador real, que não relança — e ainda assim a
    /// frase de falha tem que chegar na tela.
    /// </summary>
    [Fact]
    public async Task Failure_Is_Said_Out_Loud()
    {
        (SettingsViewModel vm, _) = await BuildAsync(failSync: true);
        vm.ResyncCommand.Execute(null);

        await vm.ResyncNowAsync();

        Assert.False(vm.IsResyncing);
        Assert.True(vm.HasResyncStatus);
        Assert.Contains("não foi possível", vm.ResyncStatus, StringComparison.OrdinalIgnoreCase);
    }
}
