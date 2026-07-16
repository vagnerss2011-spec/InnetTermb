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
/// Erro de XAML (valor de enum inválido, recurso ausente, binding pra propriedade inexistente) passa
/// pelo build e só explode em runtime, dentro de InitializeComponent() — foi assim que o editor de
/// hosts quebrou na v1.1.2 (ver HostEditorDialogRenderTests). A AccountWindow é a PRIMEIRA janela que
/// o operador vê: se ela não abrir, não há como entrar na conta. Renderiza de verdade (thread STA +
/// tema real) nos três modos — Entrar, Criar conta e a tela da chave de recuperação — porque cada um
/// realiza uma parte diferente da árvore visual (os outros ficam colapsados e não seriam exercitados).
/// </summary>
public sealed class AccountWindowRenderTests
{
    private sealed class StubAuthenticator : IAccountAuthenticator
    {
        public Task<AccountSession> RegisterAsync(
            string email, char[] password, string workspaceName, CancellationToken ct = default)
            => Task.FromResult(new AccountSession(
                email, "ws-1", new byte[32],
                new TokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)),
                new[] { new AccountWorkspace("ws-1", workspaceName, "Owner") },
                "ABCD-EFGH-IJKL-MNOP-QRST-UVWX-YZ23-4567"));

        public Task<AccountSession> LoginAsync(string email, char[] password, CancellationToken ct = default)
            => Task.FromResult(new AccountSession(
                email, "ws-1", new byte[32],
                new TokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)),
                new[] { new AccountWorkspace("ws-1", "NOC", "Owner") }));
    }

    private static Exception? RenderWith(AccountViewModel vm) => StaThreadRunner.Run(() =>
    {
        var window = new AccountWindow(vm)
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
    public void Constructs_InLoginMode_WithoutThrowing()
    {
        var vm = new AccountViewModel(new StubAuthenticator(), _ => { });
        Assert.True(vm.IsLoginMode);

        Exception? captured = RenderWith(vm);

        Assert.True(captured is null, captured?.ToString());
    }

    [Fact]
    public void Constructs_InRegisterMode_WithoutThrowing()
    {
        var vm = new AccountViewModel(new StubAuthenticator(), _ => { });
        vm.SwitchToRegisterCommand.Execute(null);
        Assert.True(vm.IsRegisterMode);

        Exception? captured = RenderWith(vm);

        Assert.True(captured is null, captured?.ToString());
    }

    /// <summary>
    /// Modo com a mensagem de erro visível: o bloco de erro fica colapsado nos casos felizes e só
    /// é realizado (medido/arranjado) quando há erro — exatamente o momento em que o operador
    /// depende dele.
    /// </summary>
    [Fact]
    public async Task Constructs_WithErrorVisible_WithoutThrowing()
    {
        var vm = new AccountViewModel(new StubAuthenticator(), _ => { });
        vm.Email = "invalido";
        await vm.SubmitAsync("senha-forte-123".ToCharArray(), null);
        Assert.True(vm.HasError);

        Exception? captured = RenderWith(vm);

        Assert.True(captured is null, captured?.ToString());
    }

    [Fact]
    public async Task Constructs_InRecoveryKeyMode_WithoutThrowing()
    {
        var vm = new AccountViewModel(new StubAuthenticator(), _ => { });
        vm.SwitchToRegisterCommand.Execute(null);
        vm.Email = "op@innet.tec.br";
        vm.WorkspaceName = "NOC";
        await vm.SubmitAsync("senha-forte-123".ToCharArray(), "senha-forte-123".ToCharArray());
        Assert.True(vm.IsRecoveryMode);

        Exception? captured = RenderWith(vm);

        Assert.True(captured is null, captured?.ToString());
    }
}
