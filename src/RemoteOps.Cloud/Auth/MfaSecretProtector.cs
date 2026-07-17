using System.Security.Cryptography;
using System.Text;
using RemoteOps.Cloud.Configuration;

namespace RemoteOps.Cloud.Auth;

/// <summary>
/// Cifra o segredo TOTP EM REPOUSO (defesa em profundidade). Diferente do material E2EE — que é opaco
/// porque o cliente o cifra — o segredo TOTP é do SERVIDOR: ele precisa dele em claro para validar os
/// códigos. Para que um dump do banco sozinho não os revele, embrulhamos com AES-256-GCM sob uma
/// chave DERIVADA do segredo de assinatura do deploy (HKDF com info dedicada — não reusa a chave de
/// assinatura crua para outro fim).
///
/// <para><b>Trade-off documentado:</b> a proteção vale contra "vazou só o banco". Não vale contra
/// "vazou banco + chave de assinatura juntos" (aí o atacante já forja JWT, então 2FA no login não o
/// segura mesmo). E rotacionar <c>Jwt__SecretKeyBase64</c> torna os segredos TOTP existentes
/// indecifráveis → os usuários re-inscrevem o 2FA (mesmo custo de rotação que já derruba os JWTs
/// emitidos; o disable por admin/reset continua disponível). Aceitável para a Fase 3.</para>
/// </summary>
public sealed class MfaSecretProtector(IConfiguration config)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("remoteops:mfa-secret-protection:v1");

    /// <summary>Embrulha o segredo em claro → blob em repouso (nonce||tag||ciphertext).</summary>
    public byte[] Protect(byte[] plaintext)
    {
        byte[] key = DeriveKey();
        try
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] tag = new byte[TagSize];
            byte[] ciphertext = new byte[plaintext.Length];
            using (var gcm = new AesGcm(key, TagSize))
            {
                gcm.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            byte[] blob = new byte[NonceSize + TagSize + ciphertext.Length];
            nonce.CopyTo(blob, 0);
            tag.CopyTo(blob, NonceSize);
            ciphertext.CopyTo(blob, NonceSize + TagSize);
            return blob;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Desembrulha o blob em repouso → segredo em claro. Lança se a chave/blob não fecharem.</summary>
    public byte[] Unprotect(byte[] blob)
    {
        if (blob.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Blob do segredo TOTP inválido (curto demais).");
        }

        byte[] key = DeriveKey();
        try
        {
            ReadOnlySpan<byte> nonce = blob.AsSpan(0, NonceSize);
            ReadOnlySpan<byte> tag = blob.AsSpan(NonceSize, TagSize);
            ReadOnlySpan<byte> ciphertext = blob.AsSpan(NonceSize + TagSize);
            byte[] plaintext = new byte[ciphertext.Length];
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] DeriveKey()
    {
        byte[] signing = DeploymentConfig.ResolveJwtSigningKey(config);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, signing, KeySize, salt: null, info: Info);
    }
}
