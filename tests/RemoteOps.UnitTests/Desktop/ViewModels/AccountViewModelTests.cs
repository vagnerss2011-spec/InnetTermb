using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// UI de conta (T7): validação, modos (Entrar/Criar conta/Chave de recuperação), mensagens de erro
/// em pt-BR acionáveis e higiene da senha (char[] zerado). A cripto real fica no autenticador
/// (E2eeAccountAuthenticatorTests) — aqui ele é fake, para o teste ser rápido e determinístico.
/// </summary>
public sealed class AccountViewModelTests
{
    private static readonly TokenSet Tokens = new("access", "refresh", DateTimeOffset.UtcNow.AddHours(1));

    private sealed class FakeAuthenticator : IAccountAuthenticator
    {
        public Exception? Throw;
        public string? LastEmail;
        public string? LastWorkspaceName;
        public string? LastTotpCode;
        public int RegisterCalls;
        public int LoginCalls;

        /// <summary>Se setado, os PRIMEIROS N logins lançam MfaRequiredException (simula 2FA ativa).</summary>
        public int MfaChallengesBeforeSuccess;

        public Task<AccountSession> RegisterAsync(
            string email, char[] password, string workspaceName, CancellationToken ct = default)
        {
            RegisterCalls++;
            LastEmail = email;
            LastWorkspaceName = workspaceName;
            return Throw is not null
                ? Task.FromException<AccountSession>(Throw)
                : Task.FromResult(new AccountSession(
                    email, "ws-1", new byte[32], Tokens,
                    new[] { new AccountWorkspace("ws-1", workspaceName, "Owner") },
                    "AAAA-BBBB-CCCC-DDDD-EEEE-FFFF-GGGG-HHHH"));
        }

        public Task<AccountSession> LoginAsync(
            string email, char[] password, string? totpCode = null, CancellationToken ct = default)
        {
            LoginCalls++;
            LastEmail = email;
            LastTotpCode = totpCode;

            if (Throw is not null)
            {
                return Task.FromException<AccountSession>(Throw);
            }

            if (MfaChallengesBeforeSuccess > 0)
            {
                MfaChallengesBeforeSuccess--;
                return Task.FromException<AccountSession>(new MfaRequiredException());
            }

            return Task.FromResult(new AccountSession(
                email, "ws-1", new byte[32], Tokens,
                new[] { new AccountWorkspace("ws-1", "NOC", "Owner") }));
        }

        // Recuperação (Fase 4): o AccountViewModel não a usa — a tela de reset é o PasswordRecoveryViewModel.
        public Task RequestPasswordResetAsync(string email, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ResetPasswordWithRecoveryKeyAsync(
            string token, string recoveryKey, char[] newPassword, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static AccountViewModel NewVm(FakeAuthenticator auth, Action<string>? copy = null)
        => new(auth, copy ?? (_ => { }));

    [Fact]
    public void StartsInLoginMode()
    {
        var vm = NewVm(new FakeAuthenticator());
        Assert.Equal(AccountMode.Login, vm.Mode);
        Assert.True(vm.IsLoginMode);
        Assert.False(vm.IsRegisterMode);
        Assert.False(vm.IsRecoveryMode);
    }

    [Fact]
    public void SwitchToRegister_ClearsError_AndChangesMode()
    {
        var vm = NewVm(new FakeAuthenticator());
        vm.SwitchToRegisterCommand.Execute(null);
        Assert.True(vm.IsRegisterMode);
        Assert.False(vm.HasError);

        vm.SwitchToLoginCommand.Execute(null);
        Assert.True(vm.IsLoginMode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sem-arroba")]
    public async Task Submit_WithInvalidEmail_ShowsPtBrError_AndDoesNotCallServer(string email)
    {
        var auth = new FakeAuthenticator();
        var vm = NewVm(auth);
        vm.Email = email;

        await vm.SubmitAsync("senha-forte-123".ToCharArray(), null);

        Assert.True(vm.HasError);
        Assert.Contains("e-mail", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, auth.LoginCalls);
    }

    [Fact]
    public async Task Submit_WithEmptyPassword_ShowsPtBrError()
    {
        var auth = new FakeAuthenticator();
        var vm = NewVm(auth);
        vm.Email = "op@innet.tec.br";

        await vm.SubmitAsync([], null);

        Assert.True(vm.HasError);
        Assert.Contains("senha", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, auth.LoginCalls);
    }

    [Fact]
    public async Task Submit_Register_WithMismatchedConfirmation_ShowsPtBrError()
    {
        var auth = new FakeAuthenticator();
        var vm = NewVm(auth);
        vm.SwitchToRegisterCommand.Execute(null);
        vm.Email = "op@innet.tec.br";
        vm.WorkspaceName = "NOC";

        await vm.SubmitAsync("senha-forte-123".ToCharArray(), "senha-forte-124".ToCharArray());

        Assert.True(vm.HasError);
        Assert.Contains("não conferem", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, auth.RegisterCalls);
    }

    [Fact]
    public async Task Submit_Register_WithShortPassword_ShowsPtBrError()
    {
        var auth = new FakeAuthenticator();
        var vm = NewVm(auth);
        vm.SwitchToRegisterCommand.Execute(null);
        vm.Email = "op@innet.tec.br";
        vm.WorkspaceName = "NOC";

        await vm.SubmitAsync("curta".ToCharArray(), "curta".ToCharArray());

        Assert.True(vm.HasError);
        Assert.Contains("8", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, auth.RegisterCalls);
    }

    [Fact]
    public async Task Submit_Register_WithoutWorkspaceName_ShowsPtBrError()
    {
        var auth = new FakeAuthenticator();
        var vm = NewVm(auth);
        vm.SwitchToRegisterCommand.Execute(null);
        vm.Email = "op@innet.tec.br";

        await vm.SubmitAsync("senha-forte-123".ToCharArray(), "senha-forte-123".ToCharArray());

        Assert.True(vm.HasError);
        Assert.Contains("workspace", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, auth.RegisterCalls);
    }

    [Fact]
    public async Task Submit_Login_Success_RaisesAuthenticated_AndKeepsSession()
    {
        var auth = new FakeAuthenticator();
        var vm = NewVm(auth);
        vm.Email = "  OP@innet.tec.br  ";
        bool authenticated = false;
        vm.Authenticated += (_, _) => authenticated = true;

        await vm.SubmitAsync("senha-forte-123".ToCharArray(), null);

        Assert.False(vm.HasError);
        Assert.True(authenticated);
        Assert.NotNull(vm.Session);
        // E-mail normalizado (trim) antes de ir pro servidor — o salt do Argon2 é por conta.
        Assert.Equal("op@innet.tec.br", auth.LastEmail);
    }

    // ── Desafio de 2FA no login ─────────────────────────────────────────────────

    /// <summary>Backend responde mfa_required → a UI entra no desafio, sem autenticar nem errar.</summary>
    [Fact]
    public async Task Submit_Login_WhenMfaRequired_EntersChallenge_WithoutAuthenticating()
    {
        var auth = new FakeAuthenticator { MfaChallengesBeforeSuccess = 1 };
        var vm = NewVm(auth);
        vm.Email = "op@innet.tec.br";
        bool authenticated = false;
        vm.Authenticated += (_, _) => authenticated = true;

        await vm.SubmitAsync("senha-forte-123".ToCharArray(), null);

        Assert.True(vm.IsMfaChallenge);
        Assert.False(authenticated);
        Assert.Null(vm.Session);
        Assert.False(vm.HasError);          // não é erro de credencial…
        Assert.NotEmpty(vm.StatusMessage);   // …é um pedido de código
        Assert.Null(auth.LastTotpCode);      // o 1º envio não mandou código
    }

    /// <summary>No desafio, o código de 6 dígitos é reenviado com a senha e o login conclui.</summary>
    [Fact]
    public async Task Submit_Login_InChallenge_WithValidCode_ResendsCode_AndAuthenticates()
    {
        var auth = new FakeAuthenticator { MfaChallengesBeforeSuccess = 1 };
        var vm = NewVm(auth);
        vm.Email = "op@innet.tec.br";
        bool authenticated = false;
        vm.Authenticated += (_, _) => authenticated = true;

        // 1º submit → desafio.
        await vm.SubmitAsync("senha-forte-123".ToCharArray(), null);
        Assert.True(vm.IsMfaChallenge);

        // Operador digita o código e reenvia.
        vm.TotpCode = "123456";
        await vm.SubmitAsync("senha-forte-123".ToCharArray(), null);

        Assert.True(authenticated);
        Assert.False(vm.IsMfaChallenge);
        Assert.NotNull(vm.Session);
        Assert.Equal("123456", auth.LastTotpCode); // o código chegou ao autenticador
    }

    /// <summary>Código errado: o backend devolve mfa_required de novo → segue no desafio, com erro.</summary>
    [Fact]
    public async Task Submit_Login_InChallenge_WithWrongCode_StaysInChallenge_WithError()
    {
        var auth = new FakeAuthenticator { MfaChallengesBeforeSuccess = 2 };
        var vm = NewVm(auth);
        vm.Email = "op@innet.tec.br";

        await vm.SubmitAsync("senha-forte-123".ToCharArray(), null); // → desafio
        vm.TotpCode = "000000";
        await vm.SubmitAsync("senha-forte-123".ToCharArray(), null); // código errado

        Assert.True(vm.IsMfaChallenge);
        Assert.True(vm.HasError);
        Assert.Contains("inválido", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(vm.TotpCode); // limpou pro operador redigitar
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("12ab56")]
    public async Task Submit_Login_InChallenge_WithMalformedCode_DoesNotCallServerAgain(string code)
    {
        var auth = new FakeAuthenticator { MfaChallengesBeforeSuccess = 1 };
        var vm = NewVm(auth);
        vm.Email = "op@innet.tec.br";

        await vm.SubmitAsync("senha-forte-123".ToCharArray(), null); // → desafio (1 chamada)
        int callsAfterChallenge = auth.LoginCalls;
        vm.TotpCode = code;
        await vm.SubmitAsync("senha-forte-123".ToCharArray(), null); // código malformado

        Assert.True(vm.IsMfaChallenge);
        Assert.True(vm.HasError);
        Assert.Equal(callsAfterChallenge, auth.LoginCalls); // não bateu no servidor de novo
    }

    /// <summary>Trocar pra "Criar conta" abandona um desafio de 2FA pendente (e limpa o código).</summary>
    [Fact]
    public async Task SwitchMode_ResetsMfaChallenge()
    {
        var auth = new FakeAuthenticator { MfaChallengesBeforeSuccess = 1 };
        var vm = NewVm(auth);
        vm.Email = "op@innet.tec.br";
        await vm.SubmitAsync("senha-forte-123".ToCharArray(), null);
        vm.TotpCode = "123456";
        Assert.True(vm.IsMfaChallenge);

        vm.SwitchToRegisterCommand.Execute(null);

        Assert.False(vm.IsMfaChallenge);
        Assert.Empty(vm.TotpCode);
    }

    /// <summary>
    /// Registro NÃO autentica direto: a chave de recuperação tem que ser exibida antes, senão o
    /// operador fica com um cofre sem plano B e nem sabe.
    /// </summary>
    [Fact]
    public async Task Submit_Register_Success_GoesToRecoveryMode_WithoutAuthenticating()
    {
        var auth = new FakeAuthenticator();
        var vm = NewVm(auth);
        vm.SwitchToRegisterCommand.Execute(null);
        vm.Email = "op@innet.tec.br";
        vm.WorkspaceName = "NOC";
        bool authenticated = false;
        vm.Authenticated += (_, _) => authenticated = true;

        await vm.SubmitAsync("senha-forte-123".ToCharArray(), "senha-forte-123".ToCharArray());

        Assert.False(vm.HasError);
        Assert.False(authenticated);
        Assert.True(vm.IsRecoveryMode);
        Assert.Equal("AAAA-BBBB-CCCC-DDDD-EEEE-FFFF-GGGG-HHHH", vm.RecoveryKey);
        Assert.Equal("NOC", auth.LastWorkspaceName);
    }

    /// <summary>O checkbox "guardei em local seguro" é obrigatório pra fechar a tela.</summary>
    [Fact]
    public async Task Recovery_Finish_IsBlocked_UntilAcknowledged()
    {
        var auth = new FakeAuthenticator();
        var vm = NewVm(auth);
        vm.SwitchToRegisterCommand.Execute(null);
        vm.Email = "op@innet.tec.br";
        vm.WorkspaceName = "NOC";
        await vm.SubmitAsync("senha-forte-123".ToCharArray(), "senha-forte-123".ToCharArray());

        Assert.False(vm.FinishCommand.CanExecute(null));

        bool authenticated = false;
        vm.Authenticated += (_, _) => authenticated = true;
        vm.RecoveryAcknowledged = true;

        Assert.True(vm.FinishCommand.CanExecute(null));
        vm.FinishCommand.Execute(null);
        Assert.True(authenticated);
    }

    [Fact]
    public async Task Recovery_Copy_PutsKeyOnClipboard()
    {
        string? copied = null;
        var auth = new FakeAuthenticator();
        var vm = NewVm(auth, text => copied = text);
        vm.SwitchToRegisterCommand.Execute(null);
        vm.Email = "op@innet.tec.br";
        vm.WorkspaceName = "NOC";
        await vm.SubmitAsync("senha-forte-123".ToCharArray(), "senha-forte-123".ToCharArray());

        vm.CopyRecoveryCommand.Execute(null);

        Assert.Equal("AAAA-BBBB-CCCC-DDDD-EEEE-FFFF-GGGG-HHHH", copied);
        Assert.NotEmpty(vm.StatusMessage);
    }

    /// <summary>Higiene: a senha digitada some da memória depois do submit (sucesso ou erro).</summary>
    [Fact]
    public async Task Submit_ZeroesPasswordBuffers_OnSuccess()
    {
        var vm = NewVm(new FakeAuthenticator());
        vm.SwitchToRegisterCommand.Execute(null);
        vm.Email = "op@innet.tec.br";
        vm.WorkspaceName = "NOC";
        char[] password = "senha-forte-123".ToCharArray(); // pragma: allowlist secret
        char[] confirm = "senha-forte-123".ToCharArray();

        await vm.SubmitAsync(password, confirm);

        Assert.True(password.All(c => c == '\0'));
        Assert.True(confirm.All(c => c == '\0'));
    }

    [Fact]
    public async Task Submit_ZeroesPasswordBuffers_OnFailure()
    {
        var auth = new FakeAuthenticator { Throw = new CloudSyncException(HttpStatusCode.Unauthorized) };
        var vm = NewVm(auth);
        vm.Email = "op@innet.tec.br";
        char[] password = "senha-forte-123".ToCharArray(); // pragma: allowlist secret

        await vm.SubmitAsync(password, null);

        Assert.True(password.All(c => c == '\0'));
        Assert.True(vm.HasError);
    }

    /// <summary>Validação que falha ANTES da rede também não pode deixar a senha viva.</summary>
    [Fact]
    public async Task Submit_ZeroesPasswordBuffers_WhenValidationFails()
    {
        var vm = NewVm(new FakeAuthenticator());
        vm.Email = "invalido";
        char[] password = "senha-forte-123".ToCharArray(); // pragma: allowlist secret

        await vm.SubmitAsync(password, null);

        Assert.True(password.All(c => c == '\0'));
    }

    public static TheoryData<Exception, string> ErrorCases() => new()
    {
        // Credencial inválida: 401 do servidor, ou o unwrap da AMK falhando (senha errada).
        { new CloudSyncException(HttpStatusCode.Unauthorized), "inválidos" },
        { new CryptographicException("tag mismatch"), "inválidos" },
        // Conta já existe.
        { new CloudSyncException(HttpStatusCode.Conflict), "já existe" },
        // Rate-limit do /auth/kdf e /auth/login (anti força-bruta).
        { new CloudSyncException(HttpStatusCode.TooManyRequests), "tentativas" },
        // Servidor fora.
        { new CloudSyncException(HttpStatusCode.InternalServerError), "servidor" },
        { new CloudSyncException(HttpStatusCode.ServiceUnavailable), "servidor" },
        // Sem rede / DNS / TLS.
        { new HttpRequestException("no such host"), "conexão" },
        // Timeout.
        { new TaskCanceledException("timeout"), "demorou" },
    };

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task Submit_MapsFailure_ToActionablePtBrMessage(Exception failure, string expected)
    {
        var auth = new FakeAuthenticator { Throw = failure };
        var vm = NewVm(auth);
        vm.Email = "op@innet.tec.br";

        await vm.SubmitAsync("senha-forte-123".ToCharArray(), null);

        Assert.True(vm.HasError);
        Assert.Contains(expected, vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.Session);
    }

    /// <summary>Janela fechada sem ninguém consumir a sessão → a AMK não fica viva na memória.</summary>
    [Fact]
    public async Task ClearSession_ZeroesAmk()
    {
        var vm = NewVm(new FakeAuthenticator());
        vm.Email = "op@innet.tec.br";
        await vm.SubmitAsync("senha-forte-123".ToCharArray(), null);
        byte[] amk = vm.Session!.Amk;
        amk[0] = 7;

        vm.ClearSession();

        Assert.True(amk.All(b => b == 0));
        Assert.Null(vm.Session);
    }

    /// <summary>Sessão consumida (T6) não pode ser zerada pelo fechamento da janela.</summary>
    [Fact]
    public async Task TakeSession_TransfersOwnership_SoClearSessionDoesNotWipeIt()
    {
        var vm = NewVm(new FakeAuthenticator());
        vm.Email = "op@innet.tec.br";
        await vm.SubmitAsync("senha-forte-123".ToCharArray(), null);

        AccountSession? taken = vm.TakeSession();
        taken!.Amk[0] = 7;
        vm.ClearSession();

        Assert.Equal(7, taken.Amk[0]);
        Assert.Null(vm.TakeSession());
    }
}
