using System.Security.Cryptography;
using System.Text;

namespace RemoteOps.Security.Vault;

/// <summary>
/// Segredo descriptografado vivo em memória. Mantém o plaintext apenas pelo
/// tempo do <c>using</c> e zera o buffer no <see cref="Dispose"/>.
/// O <see cref="ToString"/> é redigido para evitar vazamento acidental em log.
/// </summary>
public sealed class VaultSecret : IDisposable
{
    private readonly byte[] _utf8;
    private bool _disposed;

    internal VaultSecret(byte[] utf8Plaintext) => _utf8 = utf8Plaintext;

    public int ByteLength
    {
        get
        {
            ThrowIfDisposed();
            return _utf8.Length;
        }
    }

    /// <summary>Bytes UTF-8 do segredo. Não copie nem logue.</summary>
    public ReadOnlySpan<byte> RevealUtf8()
    {
        ThrowIfDisposed();
        return _utf8;
    }

    /// <summary>
    /// Materializa o segredo como <see cref="string"/> para fronteiras que exigem
    /// (contrato cross-module). Evite: prefira <see cref="RevealUtf8"/>.
    /// </summary>
    public string RevealString()
    {
        ThrowIfDisposed();
        return Encoding.UTF8.GetString(_utf8);
    }

    public override string ToString() => "VaultSecret(***)";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_utf8);
        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
