using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.ViewModels;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// "Sincronizar agora" tem que DIZER o que aconteceu.
///
/// <para><b>O bug que originou estes testes:</b> o clique sempre rodou o ciclo real (push+pull), mas a
/// VM DESCARTAVA o <c>bool</c> devolvido pelo controlador e o <c>catch</c> engolia a exceção. Quando o
/// indicador já estava em "Sincronizado", o ciclo rodava, terminava "Sincronizado" — e NADA mudava na
/// tela. Indistinguível de "o botão não fez nada", que foi exatamente a dúvida do operador em campo.</para>
///
/// <para>O caso do meio destes testes (<c>Already_Synced</c>) é o próprio bug: estado inicial igual ao
/// final. Se ele voltar a passar sem carimbo novo, a regressão voltou.</para>
/// </summary>
public sealed class SyncNowFeedbackTests
{
    private sealed class FakeSyncController : ISyncController
    {
        /// <summary>O que o ciclo devolve: <c>true</c> = terminou Sincronizado; <c>false</c> = não deu.</summary>
        public bool Result { get; init; } = true;

        /// <summary>Estoura em vez de devolver — prova que a exceção não vaza e ainda vira aviso.</summary>
        public bool Throw { get; init; }

        public int Calls { get; private set; }

        public Task<bool> SyncNowAsync(CancellationToken ct = default)
        {
            Calls++;

            // Sem async/await de propósito: um método async sem await quebraria o build
            // (TreatWarningsAsErrors + CS1998).
            return Throw
                ? Task.FromException<bool>(new InvalidOperationException("falha simulada"))
                : Task.FromResult(Result);
        }

        public Task<IReadOnlyList<SyncConflictItem>> GetConflictsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SyncConflictItem>>([]);

        public Task DismissConflictsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Relógio fixo e avançável. Sem ele, provar que o carimbo MUDA a cada clique seria uma corrida com
    /// o relógio real: dois cliques no mesmo segundo produziriam o mesmo texto e o teste ficaria
    /// intermitente. <c>LocalTimeZone</c> fixado em UTC pra a hora exibida não depender da máquina.
    /// </summary>
    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 7, 21, 14, 32, 7, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public void Before_The_First_Click_There_Is_No_Stamp()
    {
        var vm = new SyncStatusViewModel(new FakeSyncController());

        // Nada de carimbo inventado na abertura: ele só significa alguma coisa depois de um clique.
        Assert.False(vm.HasSyncOutcome);
        Assert.Equal(string.Empty, vm.SyncOutcomeText);
        Assert.False(vm.SyncOutcomeFailed);
    }

    [Fact]
    public async Task Success_Stamps_The_Time_Of_The_Cycle()
    {
        var clock = new FixedClock();
        var vm = new SyncStatusViewModel(new FakeSyncController { Result = true }, clock);

        vm.SyncNowCommand.Execute(null);
        await WaitUntil(() => !vm.IsBusy);

        Assert.True(vm.HasSyncOutcome);
        Assert.Equal("Última sincronização: 14:32:07", vm.SyncOutcomeText);
        Assert.False(vm.SyncOutcomeFailed);
    }

    [Fact]
    public async Task Failure_Says_It_Did_Not_Sync_And_Never_Claims_Success()
    {
        var clock = new FixedClock();
        var vm = new SyncStatusViewModel(new FakeSyncController { Result = false }, clock);

        vm.SyncNowCommand.Execute(null);
        await WaitUntil(() => !vm.IsBusy);

        Assert.True(vm.HasSyncOutcome);   // silêncio é o que causou a dúvida: falha TEM que aparecer
        Assert.True(vm.SyncOutcomeFailed);
        Assert.Equal("Não sincronizou às 14:32:07", vm.SyncOutcomeText);
        Assert.DoesNotContain("Última sincronização", vm.SyncOutcomeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exception_Is_Reported_As_Failure_And_Does_Not_Reach_The_Ui()
    {
        var controller = new FakeSyncController { Throw = true };
        var vm = new SyncStatusViewModel(controller, new FixedClock());

        vm.SyncNowCommand.Execute(null); // fire-and-forget: se vazasse, viraria crash do app
        await WaitUntil(() => !vm.IsBusy);

        Assert.Equal(1, controller.Calls);
        Assert.True(vm.SyncOutcomeFailed);
        Assert.Equal("Não sincronizou às 14:32:07", vm.SyncOutcomeText);
        Assert.True(vm.SyncNowCommand.CanExecute(null)); // dá pra tentar de novo
    }

    [Fact]
    public async Task Clicking_While_Already_Synced_Still_Produces_A_Visible_Signal()
    {
        // ESTE é o caso que originou a dúvida do operador: estado inicial e final iguais.
        var clock = new FixedClock();
        var controller = new FakeSyncController { Result = true };
        var vm = new SyncStatusViewModel(controller, clock);
        vm.Apply(new SyncStatus(SyncState.Synced));

        vm.SyncNowCommand.Execute(null);
        await WaitUntil(() => !vm.IsBusy);
        string first = vm.SyncOutcomeText;

        clock.Now = clock.Now.AddSeconds(12); // o operador clicou de novo 12 segundos depois
        vm.SyncNowCommand.Execute(null);
        await WaitUntil(() => !vm.IsBusy);
        string second = vm.SyncOutcomeText;

        Assert.Equal(2, controller.Calls);            // os dois cliques rodaram ciclo de verdade
        Assert.Equal("Sincronizado", vm.StatusText);  // o estado NÃO mudou — era esse o problema
        Assert.NotEqual(first, second);               // …mas a tela mudou: o carimbo é a prova visível
        Assert.Equal("Última sincronização: 14:32:19", second);
    }

    [Fact]
    public async Task The_Stamp_Notifies_The_Bindings()
    {
        var vm = new SyncStatusViewModel(new FakeSyncController(), new FixedClock());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SyncNowCommand.Execute(null);
        await WaitUntil(() => !vm.IsBusy);

        // Sem os raises o carimbo existiria só no objeto e a barra continuaria muda — o mesmo sintoma.
        Assert.Contains(nameof(SyncStatusViewModel.HasSyncOutcome), changed);
        Assert.Contains(nameof(SyncStatusViewModel.SyncOutcomeText), changed);
        Assert.Contains(nameof(SyncStatusViewModel.SyncOutcomeDetail), changed);
        Assert.Contains(nameof(SyncStatusViewModel.SyncOutcomeFailed), changed);
    }

    [Fact]
    public async Task The_Tooltip_Orients_The_Operator_On_Failure()
    {
        var vm = new SyncStatusViewModel(new FakeSyncController { Result = false }, new FixedClock());

        vm.SyncNowCommand.Execute(null);
        await WaitUntil(() => !vm.IsBusy);

        // Sem modal: a orientação de "o que fazer agora" mora no tooltip, não num diálogo que rouba foco.
        Assert.Contains("conexão", vm.SyncOutcomeDetail, StringComparison.Ordinal);
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
