using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.ViewModels;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// Fase 2, item B: o indicador de sync + "Sincronizar agora". Offline-first — sem controlador (sem
/// nuvem), o comando fica DESABILITADO; com controlador, dispara um push+pull e reflete o estado.
/// </summary>
public sealed class SyncStatusViewModelTests
{
    private sealed class FakeSyncController : ISyncController
    {
        public int Calls { get; private set; }
        public TaskCompletionSource? Gate { get; init; }
        public bool Throw { get; init; }

        public async Task SyncNowAsync(CancellationToken ct = default)
        {
            Calls++;
            if (Gate is not null)
            {
                await Gate.Task;
            }

            if (Throw)
            {
                throw new InvalidOperationException("falha simulada");
            }
        }
    }

    [Fact]
    public void Offline_Is_The_Default_And_Disables_The_Command()
    {
        var vm = new SyncStatusViewModel();

        Assert.False(vm.HasCloud);
        Assert.Equal("Offline", vm.StatusText);
        Assert.True(vm.IsOffline);
        Assert.False(vm.SyncNowCommand.CanExecute(null)); // sem nuvem → desabilitado
    }

    [Theory]
    [InlineData(SyncState.Offline, 0, "Offline")]
    [InlineData(SyncState.Syncing, 0, "Sincronizando…")]
    [InlineData(SyncState.Synced, 0, "Sincronizado")]
    [InlineData(SyncState.Synced, 3, "Sincronizado (3 conflito(s))")]
    [InlineData(SyncState.Error, 0, "Erro de sincronização")]
    public void Apply_Maps_State_To_PtBr_Text(SyncState state, int conflicts, string expected)
    {
        var vm = new SyncStatusViewModel();

        vm.Apply(new SyncStatus(state, conflicts));

        Assert.Equal(expected, vm.StatusText);
    }

    [Fact]
    public void State_Flags_Are_Mutually_Exclusive()
    {
        var vm = new SyncStatusViewModel();

        vm.Apply(new SyncStatus(SyncState.Error));

        Assert.True(vm.IsError);
        Assert.False(vm.IsOffline);
        Assert.False(vm.IsSynced);
        Assert.False(vm.IsSyncing);
    }

    [Fact]
    public void AttachController_Enables_The_Command()
    {
        var vm = new SyncStatusViewModel();
        bool raised = false;
        vm.SyncNowCommand.CanExecuteChanged += (_, _) => raised = true;

        vm.AttachController(new FakeSyncController());

        Assert.True(vm.HasCloud);
        Assert.True(vm.SyncNowCommand.CanExecute(null));
        Assert.True(raised); // a UI foi avisada pra reavaliar o botão
    }

    [Fact]
    public async Task SyncNow_Calls_The_Controller()
    {
        var controller = new FakeSyncController();
        var vm = new SyncStatusViewModel(controller);

        vm.SyncNowCommand.Execute(null);
        await WaitUntil(() => !vm.IsBusy);

        Assert.Equal(1, controller.Calls);
    }

    [Fact]
    public async Task SyncNow_Marks_Busy_And_Disables_While_Running()
    {
        var gate = new TaskCompletionSource();
        var controller = new FakeSyncController { Gate = gate };
        var vm = new SyncStatusViewModel(controller);

        vm.SyncNowCommand.Execute(null); // fica preso no gate

        Assert.True(vm.IsBusy);
        Assert.Equal("Sincronizando…", vm.StatusText);
        Assert.False(vm.SyncNowCommand.CanExecute(null)); // não deixa clicar de novo

        gate.SetResult();
        await WaitUntil(() => !vm.IsBusy);

        Assert.False(vm.IsBusy);
        Assert.True(vm.SyncNowCommand.CanExecute(null)); // reabilitado
    }

    [Fact]
    public async Task SyncNow_Swallows_Controller_Failure_And_Reenables()
    {
        var controller = new FakeSyncController { Throw = true };
        var vm = new SyncStatusViewModel(controller);

        vm.SyncNowCommand.Execute(null); // não deve derrubar nada
        await WaitUntil(() => !vm.IsBusy);

        Assert.Equal(1, controller.Calls);
        Assert.True(vm.SyncNowCommand.CanExecute(null)); // dá pra tentar de novo
    }

    [Fact]
    public void SyncNow_Without_Controller_Is_A_Noop()
    {
        var vm = new SyncStatusViewModel();

        vm.SyncNowCommand.Execute(null); // sem nuvem: não faz nada, não lança

        Assert.False(vm.IsBusy);
    }

    // ── Canal: tempo real x periódico ────────────────────────────────────────────────────

    // O operador em campo não tem outro jeito de saber que a rede derrubou o WebSocket: a URL do hub
    // carrega o JWT e não pode ir pro log (ADR-013). Sem este texto, "Sincronizado" parece igual nos
    // dois mundos — e o teto de staleness muda de segundos pra 45s sem nenhum aviso.
    [Fact]
    public void ChannelText_Reflects_RealTime()
    {
        var vm = new SyncStatusViewModel(new FakeSyncController());

        // Começa em "Periódico" de propósito: enquanto o canal não confirmou que subiu, prometer tempo
        // real seria mentir justo no caso que o operador precisa enxergar.
        Assert.Equal("Periódico", vm.ChannelText);

        vm.SetRealTime(true);
        Assert.Equal("Tempo real", vm.ChannelText);

        vm.SetRealTime(false);
        Assert.Equal("Periódico", vm.ChannelText);
    }

    [Fact]
    public void SetRealTime_Notifies_The_Binding()
    {
        var vm = new SyncStatusViewModel(new FakeSyncController());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SetRealTime(true);

        // ChannelText é derivado — sem o raise explícito o texto ficaria congelado na tela.
        Assert.Contains(nameof(SyncStatusViewModel.ChannelText), changed);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condição não atingida no tempo esperado");
    }
}
