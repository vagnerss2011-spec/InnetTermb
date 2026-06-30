using System.Security.Cryptography;

using RemoteOps.Security.Vault;

namespace RemoteOps.Security.Crypto;

/// <summary>
/// Núcleo de envelope encryption. Cada segredo recebe uma CEK aleatória
/// (AES-256-GCM); a CEK é embrulhada pela Workspace Data Key (também GCM).
/// O Associated Data (AAD) liga cada ciphertext à identidade do envelope,
/// impedindo troca/replay entre envelopes ou workspaces.
/// </summary>
internal static class EnvelopeCipher
{
    internal const string AlgorithmId = "AES-256-GCM;CEK-wrap;DPAPI-CurrentUser";

    private const int KeySize = 32;   // AES-256
    private const int NonceSize = 12; // tamanho padrão recomendado para GCM
    private const int TagSize = 16;

    internal static SealedSecret Seal(
        ReadOnlySpan<byte> workspaceKey,
        ReadOnlySpan<byte> plaintext,
        byte[] aad,
        byte[] wrapAad)
    {
        Span<byte> cek = stackalloc byte[KeySize];
        RandomNumberGenerator.Fill(cek);
        try
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] tag = new byte[TagSize];
            byte[] ciphertext = new byte[plaintext.Length];
            using (var gcm = new AesGcm(cek, TagSize))
            {
                gcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            }

            byte[] cekNonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] cekTag = new byte[TagSize];
            byte[] wrappedCek = new byte[KeySize];
            using (var gcm = new AesGcm(workspaceKey, TagSize))
            {
                gcm.Encrypt(cekNonce, cek, wrappedCek, cekTag, wrapAad);
            }

            return new SealedSecret(wrappedCek, cekNonce, cekTag, ciphertext, nonce, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cek);
        }
    }

    internal static byte[] Open(
        ReadOnlySpan<byte> workspaceKey,
        SecretEnvelope envelope,
        byte[] aad,
        byte[] wrapAad)
    {
        Span<byte> cek = stackalloc byte[KeySize];
        try
        {
            // Desembrulha a CEK. Chave de workspace errada (ex.: outro usuário/máquina)
            // ou material adulterado faz o tag GCM falhar -> CryptographicException.
            using (var gcm = new AesGcm(workspaceKey, TagSize))
            {
                gcm.Decrypt(envelope.CekNonce, envelope.WrappedCek, envelope.CekTag, cek, wrapAad);
            }

            byte[] plaintext = new byte[envelope.Ciphertext.Length];
            try
            {
                using var gcm = new AesGcm(cek, TagSize);
                gcm.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, aad);
                return plaintext;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cek);
        }
    }

    internal readonly record struct SealedSecret(
        byte[] WrappedCek,
        byte[] CekNonce,
        byte[] CekTag,
        byte[] Ciphertext,
        byte[] Nonce,
        byte[] Tag);
}
