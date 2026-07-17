using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.Sync.Remote;
using RemoteOps.UnitTests.Desktop;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Erro de XAML (recurso ausente, binding pra propriedade inexistente) passa pelo build e só explode
/// em runtime dentro de InitializeComponent(). Renderiza a janela de 2FA nos dois estados que têm
/// árvore visual própria — Início e "mostra o segredo" —, cada um com controles diferentes que só são
/// realizados quando visíveis.
/// </summary>
public sealed class MfaEnrollmentWindowRenderTests
{
    private sealed class StubMfaApi : IMfaApi
    {
        public Task<MfaEnrollResponse> EnrollAsync(CancellationToken ct = default)
            => Task.FromResult(new MfaEnrollResponse(
                "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", "otpauth://totp/RemoteOps:op@x?secret=GEZ..."));

        public Task ConfirmAsync(MfaConfirmRequest request, CancellationToken ct = default) => Task.CompletedTask;

        public Task DisableAsync(MfaDisableRequest request, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static Exception? RenderWith(MfaEnrollmentViewModel vm) => StaThreadRunner.Run(() =>
    {
        var window = new MfaEnrollmentWindow(vm)
        {
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Constructs_InIntro_WithoutThrowing()
    {
        var vm = new MfaEnrollmentViewModel(new StubMfaApi(), _ => { });
        Assert.True(vm.IsIntro);

        Exception? captured = RenderWith(vm);

        Assert.True(captured is null, captured?.ToString());
    }

    [Fact]
    public async Task Constructs_InShowSecret_WithoutThrowing()
    {
        var vm = new MfaEnrollmentViewModel(new StubMfaApi(), _ => { });
        await vm.BeginEnrollAsync();
        Assert.True(vm.IsShowSecret);

        Exception? captured = RenderWith(vm);

        Assert.True(captured is null, captured?.ToString());
    }
}
