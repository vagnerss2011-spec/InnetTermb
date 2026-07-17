using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using RemoteOps.Cloud.Auth;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// O segredo TOTP é um segredo do SERVIDOR (o servidor valida os códigos), então é guardado cifrado
/// em repouso: um dump do banco, sem a chave de assinatura do deploy, não revela os segredos 2FA.
/// </summary>
public sealed class MfaSecretProtectorTests
{
    private static IConfiguration Config(string signingKey) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = signingKey,
            })
            .Build();

    [Fact]
    public void Protect_Then_Unprotect_RoundTrips()
    {
        var protector = new MfaSecretProtector(Config("remoteops-test-signing-key-32bytes!!"));
        byte[] secret = TotpService.GenerateSecret();

        byte[] atRest = protector.Protect(secret);
        byte[] recovered = protector.Unprotect(atRest);

        Assert.Equal(secret, recovered);
    }

    [Fact]
    public void Protect_DoesNotStorePlaintext()
    {
        var protector = new MfaSecretProtector(Config("remoteops-test-signing-key-32bytes!!"));
        byte[] secret = TotpService.GenerateSecret();

        byte[] atRest = protector.Protect(secret);

        // O ciphertext não pode conter os bytes do segredo em claro.
        Assert.False(ContainsSequence(atRest, secret));
        // nonce(12) + tag(16) + ct(20) = 48 bytes.
        Assert.Equal(12 + 16 + secret.Length, atRest.Length);
    }

    [Fact]
    public void Protect_UsesFreshNonce_SoOutputDiffersEachTime()
    {
        var protector = new MfaSecretProtector(Config("remoteops-test-signing-key-32bytes!!"));
        byte[] secret = TotpService.GenerateSecret();

        Assert.NotEqual(protector.Protect(secret), protector.Protect(secret));
    }

    [Fact]
    public void Unprotect_Fails_WithDifferentSigningKey()
    {
        var a = new MfaSecretProtector(Config("remoteops-test-signing-key-32bytes!!"));
        var b = new MfaSecretProtector(Config("outra-chave-de-assinatura-totalmente!!"));
        byte[] atRest = a.Protect(TotpService.GenerateSecret());

        // Chave diferente → a tag GCM não fecha → lança (não devolve lixo silenciosamente).
        // ThrowsAny: AuthenticationTagMismatchException deriva de CryptographicException.
        Assert.ThrowsAny<CryptographicException>(() => b.Unprotect(atRest));
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            }
            if (hit) return true;
        }
        return false;
    }
}
