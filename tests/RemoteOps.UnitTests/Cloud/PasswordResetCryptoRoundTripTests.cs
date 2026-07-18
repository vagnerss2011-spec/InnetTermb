using System.Security.Cryptography;
using System.Text;
using RemoteOps.Cloud.Auth;
using Xunit;
using Sec = RemoteOps.Security.Account;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// A PROVA da Fase 4 com cripto REAL (AccountKeyService, não blobs opacos): o operador esquece a
/// senha, usa o token do email + a CHAVE DE RECUPERAÇÃO, define uma senha nova, e as senhas dos
/// equipamentos (seladas sob a AMK ANTES do reset) continuam decifráveis. E o servidor nunca vê a
/// AMK/senha/chave de recuperação — só material opaco.
/// </summary>
public sealed class PasswordResetCryptoRoundTripTests
{
    private static Argon2Params ToCloud(Sec.Argon2Params p) =>
        new(p.MemoryKib, p.Iterations, p.Parallelism, p.OutputBytes);

    private static string ExtractToken(string body) =>
        body.Split('\n')
            .Select(l => l.Trim())
            .First(l => l.Length >= 40 && l.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    private static async Task RegisterAsync(CloudTestContext ctx, string email, Sec.AccountEnrollment enroll) =>
        await ctx.Accounts.RegisterAsync(
            new RegisterRequest(
                Email: email,
                Argon2Salt: Convert.ToBase64String(enroll.Argon2Salt),
                Argon2Params: ToCloud(enroll.Params),
                AuthHash: Convert.ToBase64String(enroll.AuthHash),
                WrappedAmkPwd: Convert.ToBase64String(enroll.WrappedAmkPwd),
                WrappedAmkRec: Convert.ToBase64String(enroll.WrappedAmkRec),
                AmkKeyVersion: 1,
                DeviceId: Guid.NewGuid().ToString(),
                DeviceName: "Device A",
                WorkspaceName: "WS"),
            "1.2.3.4", default);

    [Fact]
    public async Task ForgotPassword_WithRecoveryKey_RecoversVault_AndOldPasswordStops()
    {
        using var ctx = new CloudTestContext();
        var svc = new Sec.AccountKeyService();

        const string oldPwd = "senha-antiga-forte!"; // pragma: allowlist secret
        const string newPwd = "senha-NOVA-mais-forte!"; // pragma: allowlist secret
        const string email = "operador@test.local";

        // Device A: cria a conta e sela uma "senha de equipamento" sob a AMK.
        Sec.AccountEnrollment enroll = svc.Enroll(oldPwd);
        byte[] secret = Encoding.UTF8.GetBytes("senha-do-roteador-huawei");
        byte[] sealedBlob = Sec.AccountKeyService.WrapKey(secret, enroll.Amk, "test|secret");
        await RegisterAsync(ctx, email, enroll);

        // Esqueceu a senha → pede reset → recebe o token no email.
        await ctx.PasswordReset.RequestAsync(email, default);
        var token = ExtractToken(ctx.Email.Last!.TextBody);

        // reset-context devolve o wrapped_amk_rec (igual ao do enroll).
        var recB64 = await ctx.PasswordReset.GetResetContextAsync(token, default);
        Assert.NotNull(recB64);
        var wrappedRec = Convert.FromBase64String(recB64!);
        Assert.Equal(enroll.WrappedAmkRec, wrappedRec);

        // Cliente abre a AMK com a CHAVE DE RECUPERAÇÃO (não com a senha esquecida) e re-embrulha sob a nova.
        byte[] amk = svc.UnwrapAmkWithRecoveryKey(enroll.RecoveryKey, wrappedRec);
        Assert.Equal(enroll.Amk, amk);
        var (newSalt, newParams, newWrappedPwd, newAuthHash) = svc.RewrapForNewPassword(amk, newPwd);

        // Conclui o reset no servidor com o material novo.
        var ok = await ctx.PasswordReset.ResetAsync(
            new ResetPasswordRequest(
                Token: token,
                NewAuthHash: Convert.ToBase64String(newAuthHash),
                NewArgon2Salt: Convert.ToBase64String(newSalt),
                NewArgon2Params: ToCloud(newParams),
                NewWrappedAmkPwd: Convert.ToBase64String(newWrappedPwd)),
            default);
        Assert.True(ok);

        // Login com a senha NOVA funciona e devolve o novo escrow.
        var login = await ctx.Tokens.LoginAsync(
            new LoginRequest(email, null, Guid.NewGuid().ToString(), "Device B")
            {
                AuthHash = Convert.ToBase64String(newAuthHash),
            }, "5.6.7.8", default);
        Assert.Equal(LoginOutcome.Success, login.Outcome);
        Assert.Equal(Convert.ToBase64String(newWrappedPwd), login.Response!.WrappedAmkPwd);

        // Device B: re-deriva a AMK pela senha NOVA e DECIFRA o segredo selado ANTES do reset. A PROVA.
        byte[] amkB = svc.UnwrapAmkWithPassword(newPwd, newSalt, newParams, newWrappedPwd);
        byte[] recovered = Sec.AccountKeyService.UnwrapKey(sealedBlob, amkB, "test|secret");
        Assert.Equal(secret, recovered);

        // A senha ANTIGA não loga mais.
        var oldLogin = await ctx.Tokens.LoginAsync(
            new LoginRequest(email, null, Guid.NewGuid().ToString(), "D")
            {
                AuthHash = Convert.ToBase64String(enroll.AuthHash),
            }, "1.2.3.4", default);
        Assert.Equal(LoginOutcome.InvalidCredentials, oldLogin.Outcome);
    }

    [Fact]
    public async Task ResetContext_IsUseless_WithoutTheRecoveryKey()
    {
        // Prova E2EE: quem tem o token do email mas NÃO a chave de recuperação não abre a AMK.
        // Email comprometido ≠ cofre comprometido.
        using var ctx = new CloudTestContext();
        var svc = new Sec.AccountKeyService();
        Sec.AccountEnrollment enroll = svc.Enroll("senha-forte!"); // pragma: allowlist secret
        await RegisterAsync(ctx, "op@test.local", enroll);

        await ctx.PasswordReset.RequestAsync("op@test.local", default);
        var token = ExtractToken(ctx.Email.Last!.TextBody);
        var wrappedRec = Convert.FromBase64String((await ctx.PasswordReset.GetResetContextAsync(token, default))!);

        // Uma chave de recuperação DIFERENTE não desembrulha a AMK (AES-GCM lança).
        var wrongRecovery = Sec.RecoveryKeyCodec.Generate();
        Assert.ThrowsAny<CryptographicException>(() => svc.UnwrapAmkWithRecoveryKey(wrongRecovery, wrappedRec));
    }
}
