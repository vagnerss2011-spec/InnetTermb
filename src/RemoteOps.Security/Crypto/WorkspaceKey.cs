using System.Security.Cryptography;

namespace RemoteOps.Security.Crypto;

/// <summary>
/// Workspace Data Key (WDK) viva em memória. Zera o buffer no <see cref="Dispose"/>
/// para minimizar o lifetime do material de chave.
/// </summary>
public sealed class WorkspaceKey : IDisposable
{
    private readonly byte[] _key;
    private bool _disposed;

    internal WorkspaceKey(byte[] key) => _key = key;

    public ReadOnlyMemory<byte> Key
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _key;
        }
    }

    public override string ToString() => "WorkspaceKey(***)";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_key);
        _disposed = true;
    }
}
