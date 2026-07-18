using System;
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
/// UI de "Esqueci a senha" (Fase 4): dois passos (pedir código → redefinir), validação, anti-enumeração
/// na mensagem, mensagens pt-BR acionáveis e higiene da senha (char[] zerado). A cripto real fica no
/// autenticador (E2eeAccountAuthenticatorTests) — aqui ele é fake, determinístico.
/// </summary>
public sealed class PasswordRecoveryViewModelTests
{
    private sealed class FakeAuthenticator : IAccountAuthenticator
    {
        public Exception? ThrowOnRequest;
        public Exception? ThrowOnReset;
        public string? LastRequestEmail;
        public string? LastToken;
        public string? LastRecoveryKey;
        public char[]? LastNewPassword;
        public int ResetCalls;

        public Task RequestPasswordResetAsync(string email, CancellationToken ct = default)
        {
            LastRequestEmail = email;
            return ThrowOnRequest is not null ? Task.FromException(ThrowOnRequest) : Task.CompletedTask;
        }

        public Task ResetPasswordWithRecoveryKeyAsync(
            string token, string recoveryKey, char[] newPassword, CancellationToken ct = default)
        {
            ResetCalls++;
            LastToken = token;
            LastRecoveryKey = recoveryKey;
            LastNewPassword = (char[])newPassword.Clone(); // copia ANTES de o VM zerar
            return ThrowOnReset is not null ? Task.FromException(ThrowOnReset) : Task.CompletedTask;
        }

        // Não usados aqui.
        public Task<AccountSession> RegisterAsync(string email, char[] password, string workspaceName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<AccountSession> LoginAsync(string email, char[] password, string? totpCode = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }


    // ── Passo 1: pedir o código ────────────────────────────────────────────────

    [Fact]
    public async Task RequestReset_InvalidEmail_ShowsError_NoServerCall()
    {
        var auth = new FakeAuthenticator();
        var vm = new PasswordRecoveryViewModel(auth) { Email = "não-é-email" };

        await vm.RequestResetAsync();

        Assert.True(vm.HasError);
        Assert.True(vm.IsRequestStep);
        Assert.Null(auth.LastRequestEmail);
    }

    [Fact]
    public async Task RequestReset_Valid_MovesToCodeStep_NeutralStatus()
    {
        var auth = new FakeAuthenticator();
        var vm = new PasswordRecoveryViewModel(auth) { Email = "  Operador@Test.Local " };

        await vm.RequestResetAsync();

        Assert.False(vm.HasError);
        Assert.True(vm.IsCodeStep);
        Assert.True(vm.HasStatus);
        // Normaliza o e-mail (registro/login/kdf casam em minúsculas).
        Assert.Equal("operador@test.local", auth.LastRequestEmail);
    }

    [Fact]
    public async Task RequestReset_ServerDown_ShowsError_StaysOnStep1()
    {
        var auth = new FakeAuthenticator { ThrowOnRequest = new HttpRequestException() };
        var vm = new PasswordRecoveryViewModel(auth) { Email = "op@test.local" };

        await vm.RequestResetAsync();

        Assert.True(vm.HasError);
        Assert.True(vm.IsRequestStep);
    }

    // ── Passo 2: redefinir ──────────────────────────────────────────────────────

    private static PasswordRecoveryViewModel ReadyToReset(FakeAuthenticator auth) =>
        new(auth) { Email = "op@test.local", Token = " code-123 ", RecoveryKey = " AAAA-BBBB " };

    [Fact]
    public async Task SubmitReset_MissingToken_ShowsError_NoCall()
    {
        var auth = new FakeAuthenticator();
        var vm = new PasswordRecoveryViewModel(auth) { RecoveryKey = "AAAA" };

        await vm.SubmitResetAsync("novasenha1".ToCharArray(), "novasenha1".ToCharArray());

        Assert.True(vm.HasError);
        Assert.Equal(0, auth.ResetCalls);
    }

    [Fact]
    public async Task SubmitReset_MissingRecoveryKey_ShowsError_NoCall()
    {
        var auth = new FakeAuthenticator();
        var vm = new PasswordRecoveryViewModel(auth) { Token = "code-123" };

        await vm.SubmitResetAsync("novasenha1".ToCharArray(), "novasenha1".ToCharArray());

        Assert.True(vm.HasError);
        Assert.Equal(0, auth.ResetCalls);
    }

    [Fact]
    public async Task SubmitReset_ShortPassword_ShowsError_NoCall()
    {
        var auth = new FakeAuthenticator();
        var vm = ReadyToReset(auth);

        await vm.SubmitResetAsync("curta".ToCharArray(), "curta".ToCharArray());

        Assert.True(vm.HasError);
        Assert.Equal(0, auth.ResetCalls);
    }

    [Fact]
    public async Task SubmitReset_Mismatch_ShowsError_NoCall()
    {
        var auth = new FakeAuthenticator();
        var vm = ReadyToReset(auth);

        await vm.SubmitResetAsync("novasenha1".ToCharArray(), "novasenha2".ToCharArray());

        Assert.True(vm.HasError);
        Assert.Equal(0, auth.ResetCalls);
    }

    [Fact]
    public async Task SubmitReset_Valid_TrimsInputs_RaisesCompleted()
    {
        var auth = new FakeAuthenticator();
        var vm = ReadyToReset(auth);
        var completed = false;
        vm.ResetCompleted += (_, _) => completed = true;

        await vm.SubmitResetAsync("novasenha1".ToCharArray(), "novasenha1".ToCharArray());

        Assert.False(vm.HasError);
        Assert.True(completed);
        Assert.Equal(1, auth.ResetCalls);
        Assert.Equal("code-123", auth.LastToken);   // trimado
        Assert.Equal("AAAA-BBBB", auth.LastRecoveryKey); // trimado
        Assert.Equal("novasenha1", new string(auth.LastNewPassword!));
    }

    [Fact]
    public async Task SubmitReset_WrongRecoveryKey_ShowsRecoveryKeyError()
    {
        var auth = new FakeAuthenticator { ThrowOnReset = new CryptographicException() };
        var vm = ReadyToReset(auth);

        await vm.SubmitResetAsync("novasenha1".ToCharArray(), "novasenha1".ToCharArray());

        Assert.Contains("recuperação", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitReset_InvalidToken_ShowsCodeError()
    {
        var auth = new FakeAuthenticator { ThrowOnReset = new CloudSyncException(HttpStatusCode.BadRequest) };
        var vm = ReadyToReset(auth);

        await vm.SubmitResetAsync("novasenha1".ToCharArray(), "novasenha1".ToCharArray());

        Assert.Contains("código", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitReset_ZeroesPassword_EvenOnValidationError()
    {
        var auth = new FakeAuthenticator();
        var vm = new PasswordRecoveryViewModel(auth); // Token/RecoveryKey vazios → falha de validação
        var pwd = "novasenha1".ToCharArray();

        await vm.SubmitResetAsync(pwd, "novasenha1".ToCharArray());

        Assert.All(pwd, c => Assert.Equal('\0', c));
    }
}
