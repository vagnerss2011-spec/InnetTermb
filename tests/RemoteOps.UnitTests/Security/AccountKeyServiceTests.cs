using System.Security.Cryptography;
using System.Text;
using RemoteOps.Security.Account;
using Xunit;

namespace RemoteOps.UnitTests.Security;

public class AccountKeyServiceTests
{
    /// <summary>
    /// A PROVA da Fase 1: um 2º device, com APENAS a senha + os blobs de escrow (salt, params,
    /// wrappedAmkPwd) — nunca a AMK nem a senha em claro pela rede — recupera a MESMA AMK e decifra
    /// um segredo selado sob ela no 1º device. É "logo no notebook e as senhas dos equipamentos
    /// abrem".
    /// </summary>
    [Fact]
    public void SecondDevice_WithPasswordOnly_RecoversSameAmk_AndDecryptsSecret()
    {
        var svc = new AccountKeyService();
        const string pwd = "s3nh4-mto-forte!"; // pragma: allowlist secret

        // Device A: cria a conta e sela uma "senha de equipamento" sob a AMK.
        AccountEnrollment enroll = svc.Enroll(pwd);
        byte[] secret = Encoding.UTF8.GetBytes("senha-do-roteador-huawei");
        byte[] sealedBlob = AccountKeyService.WrapKey(secret, enroll.Amk, "test|secret");

        // Transportado ao device B: SÓ salt, params, wrappedAmkPwd, sealedBlob (+ a senha digitada).
        byte[] amkB = svc.UnwrapAmkWithPassword(pwd, enroll.Argon2Salt, enroll.Params, enroll.WrappedAmkPwd);
        Assert.Equal(enroll.Amk, amkB);

        byte[] openedB = AccountKeyService.UnwrapKey(sealedBlob, amkB, "test|secret");
        Assert.Equal("senha-do-roteador-huawei", Encoding.UTF8.GetString(openedB));
    }

    [Fact]
    public void RecoveryKey_RecoversAmk()
    {
        var svc = new AccountKeyService();
        AccountEnrollment enroll = svc.Enroll("qualquer-senha");
        byte[] amk = svc.UnwrapAmkWithRecoveryKey(enroll.RecoveryKey, enroll.WrappedAmkRec);
        Assert.Equal(enroll.Amk, amk);
    }

    [Fact]
    public void WrongPassword_Throws()
    {
        var svc = new AccountKeyService();
        AccountEnrollment enroll = svc.Enroll("senha-certa");
        Assert.ThrowsAny<CryptographicException>(() =>
            svc.UnwrapAmkWithPassword("senha-ERRADA", enroll.Argon2Salt, enroll.Params, enroll.WrappedAmkPwd));
    }

    [Fact]
    public void WrongRecoveryKey_Throws()
    {
        var svc = new AccountKeyService();
        AccountEnrollment enroll = svc.Enroll("senha");
        string other = RecoveryKeyCodec.Generate();
        Assert.ThrowsAny<CryptographicException>(() =>
            svc.UnwrapAmkWithRecoveryKey(other, enroll.WrappedAmkRec));
    }

    [Fact]
    public void DomainSeparation_AuthHash_DiffersFrom_Kek()
    {
        var svc = new AccountKeyService();
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        AccountKeyMaterial m = svc.DeriveFromPassword("x", salt, Argon2Params.Default);
        Assert.NotEqual(m.AuthHash, m.Kek);
        Assert.Equal(32, m.AuthHash.Length);
        Assert.Equal(32, m.Kek.Length);
    }

    [Fact]
    public void ChangePassword_KeepsSameAmk_AndOldPasswordStopsWorking()
    {
        var svc = new AccountKeyService();
        AccountEnrollment enroll = svc.Enroll("senha-antiga");
        (byte[] salt, Argon2Params p, byte[] wrapped, byte[] _) = svc.RewrapForNewPassword(enroll.Amk, "senha-nova");

        byte[] amk = svc.UnwrapAmkWithPassword("senha-nova", salt, p, wrapped);
        Assert.Equal(enroll.Amk, amk);

        Assert.ThrowsAny<CryptographicException>(() =>
            svc.UnwrapAmkWithPassword("senha-antiga", salt, p, wrapped));
    }

    [Fact]
    public void RecoveryKey_RoundTrips_Base32_AndIsTolerant()
    {
        string k = RecoveryKeyCodec.Generate();
        byte[] raw = RecoveryKeyCodec.Parse(k);
        Assert.Equal(20, raw.Length);
        // tolera minúsculas, espaços e ausência de hífen
        byte[] raw2 = RecoveryKeyCodec.Parse(k.ToLowerInvariant().Replace("-", " "));
        Assert.Equal(raw, raw2);
    }

    // ── FIX A (higiene LOW #3): overloads char[] não podem mudar a derivação ──

    /// <summary>
    /// PROVA de que o overload <c>char[]</c> (o que a UI usa) deriva EXATAMENTE a mesma MasterKey
    /// (AuthHash + KEK) que o overload <c>string</c> (só-teste). Se divergissem, um device registrado
    /// por um caminho não abriria o cofre pelo outro. A senha inclui não-ASCII e um emoji (par
    /// substituto) pra garantir que a conversão char[]→UTF-8 casa byte a byte com string→UTF-8.
    /// </summary>
    [Fact]
    public void DeriveFromPassword_CharArray_ProducesSameKeyAsString()
    {
        var svc = new AccountKeyService();
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        const string pwd = "s3nh4-çãØ-🔒-forte"; // pragma: allowlist secret

        AccountKeyMaterial fromString = svc.DeriveFromPassword(pwd, salt, Argon2Params.Default);
        AccountKeyMaterial fromChars = svc.DeriveFromPassword(pwd.ToCharArray(), salt, Argon2Params.Default);

        Assert.Equal(fromString.AuthHash, fromChars.AuthHash);
        Assert.Equal(fromString.Kek, fromChars.Kek);
    }

    /// <summary>
    /// O serviço NÃO pode zerar o <c>char[]</c> do chamador: o login deriva duas vezes (auth e depois
    /// unwrap) com o MESMO buffer, e quem zera é a UI, uma vez, no fim. Se o serviço zerasse, a 2ª
    /// derivação usaria uma senha vazia e o cofre não abriria.
    /// </summary>
    [Fact]
    public void DeriveFromPassword_CharArray_DoesNotMutateCallerBuffer()
    {
        var svc = new AccountKeyService();
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        char[] pwd = "s3nh4-mto-forte!".ToCharArray(); // pragma: allowlist secret
        char[] original = (char[])pwd.Clone();

        svc.DeriveFromPassword(pwd, salt, Argon2Params.Default);

        Assert.Equal(original, pwd);
    }

    /// <summary>
    /// Fim a fim pelo caminho char[]: <c>Enroll(char[])</c> gera a AMK e <c>UnwrapAmkWithPassword</c>
    /// a recupera — e o overload string (só-teste) recupera a MESMA AMK, provando que o caminho da UI
    /// é interoperável com o caminho legado dos testes (a derivação não mudou).
    /// </summary>
    [Fact]
    public void Enroll_CharArray_RoundTrips_AndIsCrossCompatibleWithString()
    {
        var svc = new AccountKeyService();
        const string pwd = "s3nh4-mto-forte!"; // pragma: allowlist secret

        AccountEnrollment enroll = svc.Enroll(pwd.ToCharArray());

        byte[] amkChars = svc.UnwrapAmkWithPassword(
            pwd.ToCharArray(), enroll.Argon2Salt, enroll.Params, enroll.WrappedAmkPwd);
        Assert.Equal(enroll.Amk, amkChars);

        byte[] amkString = svc.UnwrapAmkWithPassword(
            pwd, enroll.Argon2Salt, enroll.Params, enroll.WrappedAmkPwd);
        Assert.Equal(enroll.Amk, amkString);
    }

    /// <summary>Troca de senha pelo overload char[] mantém a mesma AMK (segredos intactos).</summary>
    [Fact]
    public void RewrapForNewPassword_CharArray_KeepsSameAmk()
    {
        var svc = new AccountKeyService();
        AccountEnrollment enroll = svc.Enroll("senha-antiga".ToCharArray());
        (byte[] salt, Argon2Params p, byte[] wrapped, byte[] _) =
            svc.RewrapForNewPassword(enroll.Amk, "senha-nova".ToCharArray());

        byte[] amk = svc.UnwrapAmkWithPassword("senha-nova".ToCharArray(), salt, p, wrapped);
        Assert.Equal(enroll.Amk, amk);
    }
}
