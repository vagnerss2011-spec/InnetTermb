using System;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.Update;
using RemoteOps.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// O aviso de atualização precisa ser HONESTO em três situações: tem versão nova, não tem, e não deu
/// pra verificar. Antes desta VM, as três produziam a mesma tela (nada), então "silêncio" queria dizer
/// tanto "está atualizado" quanto "a rede falhou" — ver o spec de 2026-07-20.
/// </summary>
public sealed class UpdateNotificationViewModelTests
{
    private sealed class FakeUpdateService : IUpdateService
    {
        public UpdateCheckResult? Result { get; set; }
        public Exception? ThrowOnCheck { get; set; }
        public int Checks { get; private set; }
        public int Applies { get; private set; }

        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
        {
            Checks++;
            if (ThrowOnCheck is not null)
            {
                throw ThrowOnCheck;
            }

            return Task.FromResult(Result ?? NoUpdate());
        }

        public Task ApplyUpdateAsync(UpdateCheckResult update, CancellationToken ct = default)
        {
            Applies++;
            return Task.CompletedTask;
        }
    }

    private static AppVersion V(string value) => AppVersion.Parse(value);

    private static UpdateCheckResult NoUpdate()
        => UpdateCheckResultFactory.Create(V("1.4.1"), V("1.4.1"), minimumRequiredVersion: null);

    private static UpdateCheckResult WithUpdate()
        => UpdateCheckResultFactory.Create(V("1.4.1"), V("1.4.2"), minimumRequiredVersion: null);

    [Fact]
    public void Starts_Hidden()
    {
        var vm = new UpdateNotificationViewModel(new FakeUpdateService());

        Assert.False(vm.HasUpdate);
    }

    [Fact]
    public async Task No_Update_Keeps_Indicator_Hidden()
    {
        var svc = new FakeUpdateService { Result = NoUpdate() };
        var vm = new UpdateNotificationViewModel(svc);

        await vm.CheckAsync();

        Assert.False(vm.HasUpdate);
        Assert.Equal(1, svc.Checks);
    }

    [Fact]
    public async Task Update_Available_Shows_Version_In_Text()
    {
        var svc = new FakeUpdateService { Result = WithUpdate() };
        var vm = new UpdateNotificationViewModel(svc);

        await vm.CheckAsync();

        Assert.True(vm.HasUpdate);
        Assert.Contains("1.4.2", vm.UpdateText, StringComparison.Ordinal);
    }

    // O agravante nº 1 do spec: hoje uma falha de rede vira "return null" e o app fica
    // indistinguível de "está tudo atualizado". Falhar NÃO pode apagar um aviso já mostrado.
    [Fact]
    public async Task Failed_Check_Does_Not_Erase_A_Known_Update()
    {
        var svc = new FakeUpdateService { Result = WithUpdate() };
        var vm = new UpdateNotificationViewModel(svc);
        await vm.CheckAsync();

        svc.ThrowOnCheck = new InvalidOperationException("rede fora");
        await vm.CheckAsync();

        Assert.True(vm.HasUpdate);
        Assert.Contains("1.4.2", vm.UpdateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_Check_Never_Throws_To_The_Caller()
    {
        var svc = new FakeUpdateService { ThrowOnCheck = new InvalidOperationException("rede fora") };
        var vm = new UpdateNotificationViewModel(svc);

        await vm.CheckAsync(); // o timer chama isto; uma exceção aqui mataria a verificação periódica

        Assert.False(vm.HasUpdate);
    }

    // O tooltip é o que desfaz a ambiguidade: sem ele, "nada na tela" significa duas coisas.
    [Fact]
    public async Task Tooltip_Reports_Last_Successful_Check()
    {
        var svc = new FakeUpdateService { Result = NoUpdate() };
        var vm = new UpdateNotificationViewModel(svc);

        Assert.Contains("ainda não", vm.LastCheckText, StringComparison.OrdinalIgnoreCase);

        await vm.CheckAsync();

        Assert.DoesNotContain("ainda não", vm.LastCheckText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_Check_Keeps_The_Previous_Successful_Timestamp()
    {
        var svc = new FakeUpdateService { Result = NoUpdate() };
        var vm = new UpdateNotificationViewModel(svc);
        await vm.CheckAsync();
        string afterSuccess = vm.LastCheckText;

        svc.ThrowOnCheck = new InvalidOperationException("rede fora");
        await vm.CheckAsync();

        Assert.Equal(afterSuccess, vm.LastCheckText);
    }

    [Fact]
    public async Task Repeated_Checks_Do_Not_Duplicate_State()
    {
        var svc = new FakeUpdateService { Result = WithUpdate() };
        var vm = new UpdateNotificationViewModel(svc);

        await vm.CheckAsync();
        await vm.CheckAsync();

        Assert.True(vm.HasUpdate);
        Assert.Equal(2, svc.Checks);
    }

    // Aplicar é SEMPRE por iniciativa do operador (o clique no indicador) — nunca automático.
    [Fact]
    public async Task Apply_Is_Requested_Not_Performed_By_The_ViewModel()
    {
        var svc = new FakeUpdateService { Result = WithUpdate() };
        var vm = new UpdateNotificationViewModel(svc);
        await vm.CheckAsync();

        UpdateCheckResult? pedido = null;
        vm.ApplyRequested += (_, check) => pedido = check;

        vm.ApplyCommand.Execute(null);

        Assert.NotNull(pedido);
        Assert.Equal(0, svc.Applies); // a VM não baixa nada sozinha; quem confirma é a janela
    }

    [Fact]
    public void Apply_Is_Disabled_Without_An_Update()
    {
        var vm = new UpdateNotificationViewModel(new FakeUpdateService());

        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void Without_Update_Service_Stays_Silent()
    {
        // Build sem serviço de update (ex.: rodando fora do pacote instalado): nada quebra, nada aparece.
        var vm = new UpdateNotificationViewModel(updateService: null);

        Assert.False(vm.HasUpdate);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }
}
