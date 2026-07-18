using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using RemoteOps.Desktop.Account;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.Sync.Remote;
using RemoteOps.UnitTests.Desktop;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Render STA da janela de "Esqueci a senha" (Fase 4). Um erro de XAML (recurso ausente via
/// StaticResource, binding pra propriedade inexistente, enum inválido) passa pelo build e só explode
/// dentro de InitializeComponent(). Renderiza os DOIS passos — pedir código e redefinir — porque cada
/// um realiza uma parte diferente da árvore visual (o outro fica colapsado e não seria exercitado).
/// </summary>
public sealed class PasswordRecoveryWindowRenderTests
{
    private sealed class StubAuthenticator : IAccountAuthenticator
    {
        public Task RequestPasswordResetAsync(string email, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ResetPasswordWithRecoveryKeyAsync(
            string token, string recoveryKey, char[] newPassword, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<AccountSession> RegisterAsync(string email, char[] password, string workspaceName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<AccountSession> LoginAsync(string email, char[] password, string? totpCode = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static Exception? RenderWith(PasswordRecoveryViewModel vm) => StaThreadRunner.Run(() =>
    {
        var window = new PasswordRecoveryWindow(vm)
        {
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            WindowStartupLocation = WindowStartupLocation.Manual,
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
    public void Constructs_InRequestStep_WithoutThrowing()
    {
        var vm = new PasswordRecoveryViewModel(new StubAuthenticator());
        Assert.True(vm.IsRequestStep);

        Exception? captured = RenderWith(vm);

        Assert.True(captured is null, captured?.ToString());
    }

    [Fact]
    public async Task Constructs_InCodeStep_WithoutThrowing()
    {
        var vm = new PasswordRecoveryViewModel(new StubAuthenticator()) { Email = "op@innet.tec.br" };
        await vm.RequestResetAsync();
        Assert.True(vm.IsCodeStep);

        Exception? captured = RenderWith(vm);

        Assert.True(captured is null, captured?.ToString());
    }

    /// <summary>Modo com erro visível: o bloco de erro só é realizado quando há erro.</summary>
    [Fact]
    public async Task Constructs_WithErrorVisible_WithoutThrowing()
    {
        var vm = new PasswordRecoveryViewModel(new StubAuthenticator()) { Email = "invalido" };
        await vm.RequestResetAsync();
        Assert.True(vm.HasError);

        Exception? captured = RenderWith(vm);

        Assert.True(captured is null, captured?.ToString());
    }
}
