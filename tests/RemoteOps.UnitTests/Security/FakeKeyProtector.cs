using System.Security.Cryptography;
using System.Text;

using RemoteOps.Security.Crypto;

namespace RemoteOps.UnitTests.Security;

/// <summary>
/// Dublê de <see cref="ILocalKeyProtector"/> que modela a ligação usuário/máquina
/// do DPAPI de forma determinística e cross-platform. A chave de proteção deriva
/// de uma "identidade" (usuário@máquina) + entropia; uma identidade diferente
/// produz chave diferente e o tag AES-GCM falha no Unprotect — exatamente como o
/// DPAPI recusaria o blob de outro usuário/máquina.
/// </summary>
internal sealed class FakeKeyProtector : ILocalKeyProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _identity;

    public FakeKeyProtector(string identity) =>
        _identity = SHA256.HashData(Encoding.UTF8.GetBytes(identity));

    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
    {
        byte[] key = Derive(entropy);
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

    public byte[] Unprotect(ReadOnlySpan<byte> protectedBlob, ReadOnlySpan<byte> entropy)
    {
        byte[] key = Derive(entropy);
        try
        {
            ReadOnlySpan<byte> nonce = protectedBlob[..NonceSize];
            ReadOnlySpan<byte> tag = protectedBlob.Slice(NonceSize, TagSize);
            ReadOnlySpan<byte> ciphertext = protectedBlob[(NonceSize + TagSize)..];
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

    private byte[] Derive(ReadOnlySpan<byte> entropy)
    {
        byte[] buffer = new byte[_identity.Length + entropy.Length];
        _identity.CopyTo(buffer, 0);
        entropy.CopyTo(buffer.AsSpan(_identity.Length));
        try
        {
            return SHA256.HashData(buffer); // 32 bytes -> AES-256
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
