using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Security.Account;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Account;

/// <summary>
/// Prova, na camada que a UI de conta usa (T7), o fluxo E2EE de ponta a ponta: o registro monta os
/// escrows e o login num device NOVO recupera a MESMA AMK só com a senha + o que o servidor guarda.
/// O núcleo de cripto já tem sua própria prova (AccountKeyServiceTests); aqui o alvo é o
/// ORQUESTRADOR — que ele mande ao servidor exatamente o que a spec §4.2 permite e nada mais.
/// </summary>
public sealed class E2eeAccountAuthenticatorTests
{
    private const string Password = "senha-forte-123"; // pragma: allowlist secret
    private static readonly Guid DeviceA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DeviceB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>
    /// Servidor fake que guarda EXATAMENTE o que a spec §4.2 autoriza (salt/params públicos,
    /// escrows cifrados, authHash) — se o orquestrador dependesse de algo além disso, o teste
    /// nem compilaria. Valida o authHash como o backend real faria (compara o que foi registrado).
    /// </summary>
    private sealed class FakeAccountServer : IAccountApi
    {
        public RegisterAccountRequest? Stored { get; private set; }

        /// <summary>Se setado, o login exige este código TOTP; sem ele (ou errado) → MfaRequiredException.</summary>
        public string? RequiredTotp { get; set; }

        /// <summary>Último código TOTP recebido no login — prova que o orquestrador o repassa.</summary>
        public string? LastTotp { get; private set; }

        public Task<RegisterAccountResponse> RegisterAsync(
            RegisterAccountRequest request, CancellationToken ct = default)
        {
            Stored = request;
            return Task.FromResult(new RegisterAccountResponse(
                "access", "refresh", DateTimeOffset.UtcNow.AddHours(1),
                WorkspaceId: "ws-1",
                WrappedAmkPwd: request.WrappedAmkPwd,
                AmkKeyVersion: request.AmkKeyVersion,
                Workspaces: new[] { new AccountWorkspace("ws-1", request.WorkspaceName, "Owner") }));
        }

        public Task<KdfResponse> GetKdfAsync(string email, CancellationToken ct = default)
            => Stored is null
                ? throw new CloudSyncException(HttpStatusCode.NotFound)
                : Task.FromResult(new KdfResponse(Stored.Argon2Salt, Stored.Argon2Params));

        public Task<E2eeLoginResponse> LoginAsync(
            E2eeLoginRequest request, CancellationToken ct = default)
        {
            if (Stored is null || !request.AuthHash.SequenceEqual(Stored.AuthHash))
            {
                throw new CloudSyncException(HttpStatusCode.Unauthorized);
            }

            LastTotp = request.TotpCode;
            // 2FA: só depois da senha (AuthHash) validar — exatamente como o backend real.
            if (RequiredTotp is not null && request.TotpCode != RequiredTotp)
            {
                throw new MfaRequiredException();
            }

            return Task.FromResult(new E2eeLoginResponse(
                "access", "refresh", DateTimeOffset.UtcNow.AddHours(1),
                Stored.WrappedAmkPwd, Stored.AmkKeyVersion,
                new[] { new AccountWorkspace("ws-1", Stored.WorkspaceName, "Owner") }));
        }

        // ── Recuperação (Fase 4) ──────────────────────────────────────────────
        // O token real chega por email; aqui é uma constante que o fake aceita.
        public const string ResetToken = "reset-token-fake";

        public string? LastForgotEmail { get; private set; }
        public ResetPasswordRequest? Reset { get; private set; }

        public Task ForgotPasswordAsync(string email, CancellationToken ct = default)
        {
            LastForgotEmail = email;
            return Task.CompletedTask;
        }

        public Task<byte[]> GetResetContextAsync(string token, CancellationToken ct = default)
            => Stored is null || token != ResetToken
                ? throw new CloudSyncException(HttpStatusCode.BadRequest)
                // reset-context devolve o escrow de RECUPERAÇÃO (nunca muda no reset).
                : Task.FromResult(Stored.WrappedAmkRec);

        public Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default)
        {
            if (Stored is null || request.Token != ResetToken)
            {
                throw new CloudSyncException(HttpStatusCode.BadRequest);
            }

            Reset = request;
            // Aplica no "servidor": a senha nova passa a valer (re-embrulha só o escrow por senha).
            Stored = Stored with
            {
                AuthHash = request.NewAuthHash,
                Argon2Salt = request.NewArgon2Salt,
                Argon2Params = request.NewArgon2Params,
                WrappedAmkPwd = request.NewWrappedAmkPwd,
            };
            return Task.CompletedTask;
        }
    }

    /// <summary>A PROVA: device B, só com a senha, recupera a AMK criada no device A.</summary>
    [Fact]
    public async Task Register_ThenLoginOnAnotherDevice_RecoversSameAmk()
    {
        var server = new FakeAccountServer();
        var deviceA = new E2eeAccountAuthenticator(server, DeviceA, "PC-A");
        var deviceB = new E2eeAccountAuthenticator(server, DeviceB, "PC-B");

        AccountSession registered = await deviceA.RegisterAsync(
            "op@innet.tec.br", Password.ToCharArray(), "NOC");
        AccountSession loggedIn = await deviceB.LoginAsync("op@innet.tec.br", Password.ToCharArray());

        Assert.Equal(registered.Amk, loggedIn.Amk);
        Assert.Equal(32, loggedIn.Amk.Length);
    }

    /// <summary>
    /// REGRA DE OURO: o que trafega no registro não pode conter a senha nem material de chave em
    /// claro. Serializa a request como ela iria pro fio e procura os bytes proibidos.
    /// </summary>
    [Fact]
    public async Task Register_NeverSendsPasswordOrKeyMaterialToServer()
    {
        var server = new FakeAccountServer();
        var auth = new E2eeAccountAuthenticator(server, DeviceA, "PC-A");

        AccountSession session = await auth.RegisterAsync("op@innet.tec.br", Password.ToCharArray(), "NOC");

        string wire = JsonSerializer.Serialize(server.Stored, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(Password, wire, StringComparison.OrdinalIgnoreCase);
        // A AMK e a chave de recuperação também não podem aparecer (nem em base64).
        Assert.DoesNotContain(Convert.ToBase64String(session.Amk), wire, StringComparison.Ordinal);
        Assert.DoesNotContain(session.RecoveryKey!, wire, StringComparison.OrdinalIgnoreCase);
        // O que o servidor PODE ter: salt/params públicos + escrows cifrados + authHash.
        Assert.NotNull(server.Stored);
        Assert.Equal(32, server.Stored!.AuthHash.Length);
        Assert.NotEmpty(server.Stored.WrappedAmkPwd);
        Assert.NotEmpty(server.Stored.WrappedAmkRec);
    }

    /// <summary>
    /// O registro identifica o DEVICE. O backend cria a conta e emite a sessão na mesma chamada, e
    /// um refresh token só existe amarrado a um device — sem estes campos o /auth/register real
    /// rejeitaria a request (foi a divergência que a T6 encontrou contra o contrato da T4).
    /// </summary>
    [Fact]
    public async Task Register_SendsDeviceIdentityAndWorkspaceName()
    {
        var server = new FakeAccountServer();
        var auth = new E2eeAccountAuthenticator(server, DeviceA, "PC-A");

        AccountSession session = await auth.RegisterAsync("op@innet.tec.br", Password.ToCharArray(), "NOC");

        Assert.Equal(DeviceA.ToString(), server.Stored!.DeviceId);
        Assert.Equal("PC-A", server.Stored.DeviceName);
        Assert.Equal("NOC", server.Stored.WorkspaceName);
        // O workspaceId autoritativo do registro é o do servidor — é ele que o sync vai usar.
        Assert.Equal("ws-1", session.WorkspaceId);
    }

    /// <summary>O login manda o authHash — nunca a senha (o backend não teria como recebê-la).</summary>
    [Fact]
    public async Task Login_SendsAuthHash_NotPassword()
    {
        var server = new FakeAccountServer();
        var auth = new E2eeAccountAuthenticator(server, DeviceA, "PC-A");
        await auth.RegisterAsync("op@innet.tec.br", Password.ToCharArray(), "NOC");

        await auth.LoginAsync("op@innet.tec.br", Password.ToCharArray());

        var login = new E2eeLoginRequest("op@innet.tec.br", server.Stored!.AuthHash, DeviceA.ToString(), "PC-A");
        string wire = JsonSerializer.Serialize(login, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(Password, wire, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Senha errada: o servidor recusa o authHash (401) — o cofre nem chega a ser tocado.</summary>
    [Fact]
    public async Task Login_WithWrongPassword_Throws()
    {
        var server = new FakeAccountServer();
        var auth = new E2eeAccountAuthenticator(server, DeviceA, "PC-A");
        await auth.RegisterAsync("op@innet.tec.br", Password.ToCharArray(), "NOC");

        await Assert.ThrowsAsync<CloudSyncException>(
            () => auth.LoginAsync("op@innet.tec.br", "senha-errada-123".ToCharArray()));
    }

    /// <summary>
    /// A chave de recuperação exibida na tela pós-registro abre o segundo escrow de verdade —
    /// se este teste cair, o operador guardaria um papel que não recupera nada.
    /// </summary>
    [Fact]
    public async Task Register_RecoveryKey_UnwrapsTheAmk()
    {
        var server = new FakeAccountServer();
        var auth = new E2eeAccountAuthenticator(server, DeviceA, "PC-A");

        AccountSession session = await auth.RegisterAsync("op@innet.tec.br", Password.ToCharArray(), "NOC");

        byte[] recovered = new AccountKeyService()
            .UnwrapAmkWithRecoveryKey(session.RecoveryKey!, server.Stored!.WrappedAmkRec);
        Assert.Equal(session.Amk, recovered);
    }

    /// <summary>
    /// Conta com 2FA: o login sem código lança MfaRequiredException (a KEK é zerada e o cofre nem é
    /// tocado) e, com o código certo, recupera a MESMA AMK. Prova que o orquestrador REPASSA o código.
    /// </summary>
    [Fact]
    public async Task Login_With2fa_RequiresCode_ThenRecoversAmk()
    {
        var server = new FakeAccountServer();
        var deviceA = new E2eeAccountAuthenticator(server, DeviceA, "PC-A");
        AccountSession registered = await deviceA.RegisterAsync("op@innet.tec.br", Password.ToCharArray(), "NOC");

        // Operador ativou 2FA no servidor.
        server.RequiredTotp = "424242";
        var deviceB = new E2eeAccountAuthenticator(server, DeviceB, "PC-B");

        // Sem código → desafio.
        await Assert.ThrowsAsync<MfaRequiredException>(
            () => deviceB.LoginAsync("op@innet.tec.br", Password.ToCharArray()));

        // Com o código → recupera a AMK (e o servidor recebeu o código).
        AccountSession loggedIn = await deviceB.LoginAsync("op@innet.tec.br", Password.ToCharArray(), "424242");
        Assert.Equal("424242", server.LastTotp);
        Assert.Equal(registered.Amk, loggedIn.Amk);
    }

    /// <summary>O login não devolve chave de recuperação — ela só existe uma vez, no registro.</summary>
    [Fact]
    public async Task Login_DoesNotReturnRecoveryKey()
    {
        var server = new FakeAccountServer();
        var auth = new E2eeAccountAuthenticator(server, DeviceA, "PC-A");
        await auth.RegisterAsync("op@innet.tec.br", Password.ToCharArray(), "NOC");

        AccountSession session = await auth.LoginAsync("op@innet.tec.br", Password.ToCharArray());

        Assert.Null(session.RecoveryKey);
        Assert.Equal("ws-1", Assert.Single(session.Workspaces).Id);
    }

    /// <summary>
    /// PROVA da Fase 4 na camada do orquestrador: esqueci a senha → reset com a chave de recuperação
    /// (exibida no registro) + senha nova → a senha NOVA recupera a MESMA AMK e a ANTIGA para de logar.
    /// O cofre está intacto (a AMK não mudou) e o servidor nunca viu a AMK.
    /// </summary>
    [Fact]
    public async Task ResetWithRecoveryKey_RewrapsAmk_NewPasswordRecoversSameAmk_OldFails()
    {
        var server = new FakeAccountServer();
        var deviceA = new E2eeAccountAuthenticator(server, DeviceA, "PC-A");
        AccountSession registered = await deviceA.RegisterAsync("op@innet.tec.br", Password.ToCharArray(), "NOC");

        const string newPwd = "senha-nova-999"; // pragma: allowlist secret
        var deviceB = new E2eeAccountAuthenticator(server, DeviceB, "PC-B");

        await deviceB.RequestPasswordResetAsync("op@innet.tec.br");
        Assert.Equal("op@innet.tec.br", server.LastForgotEmail);

        await deviceB.ResetPasswordWithRecoveryKeyAsync(
            FakeAccountServer.ResetToken, registered.RecoveryKey!, newPwd.ToCharArray());

        // A senha NOVA loga e recupera a MESMA AMK — o cofre continua decifrável.
        AccountSession loggedIn = await deviceB.LoginAsync("op@innet.tec.br", newPwd.ToCharArray());
        Assert.Equal(registered.Amk, loggedIn.Amk);

        // A senha ANTIGA não loga mais.
        await Assert.ThrowsAsync<CloudSyncException>(
            () => deviceB.LoginAsync("op@innet.tec.br", Password.ToCharArray()));
    }

    /// <summary>Chave de recuperação errada: a AMK não abre (o núcleo lança) — o reset nem sobe.</summary>
    [Fact]
    public async Task ResetWithWrongRecoveryKey_Throws_AndDoesNotReset()
    {
        var server = new FakeAccountServer();
        var deviceA = new E2eeAccountAuthenticator(server, DeviceA, "PC-A");
        await deviceA.RegisterAsync("op@innet.tec.br", Password.ToCharArray(), "NOC");

        var deviceB = new E2eeAccountAuthenticator(server, DeviceB, "PC-B");
        string wrongRecovery = RecoveryKeyCodec.Generate();

        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            deviceB.ResetPasswordWithRecoveryKeyAsync(
                FakeAccountServer.ResetToken, wrongRecovery, "qualquer-senha-8".ToCharArray()));

        Assert.Null(server.Reset); // nada foi enviado ao servidor
    }

    /// <summary>Sessão descartada (janela fechada sem consumir) → a AMK sai da memória zerada.</summary>
    [Fact]
    public async Task ZeroAmk_WipesTheKeyMaterial()
    {
        var server = new FakeAccountServer();
        var auth = new E2eeAccountAuthenticator(server, DeviceA, "PC-A");
        AccountSession session = await auth.RegisterAsync("op@innet.tec.br", Password.ToCharArray(), "NOC");

        session.ZeroAmk();

        Assert.True(session.Amk.All(b => b == 0));
    }
}
