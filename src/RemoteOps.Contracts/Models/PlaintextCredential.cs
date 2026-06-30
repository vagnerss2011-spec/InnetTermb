using System.Runtime.InteropServices;

namespace RemoteOps.Contracts.Models;

/// <summary>
/// Short-lived credential container. Caller must Dispose() immediately after use
/// to zero sensitive bytes from managed heap.
/// </summary>
public sealed class PlaintextCredential : IDisposable
{
    private byte[]? _passwordBytes;
    private byte[]? _privateKeyPem;
    private bool _disposed;

    public required string Username { get; init; }

    public PasswordHandle? Password =>
        _passwordBytes is null ? null : new PasswordHandle(_passwordBytes);

    public PrivateKeyHandle? PrivateKey =>
        _privateKeyPem is null ? null : new PrivateKeyHandle(_privateKeyPem);

    public static PlaintextCredential WithPassword(string username, byte[] passwordBytes)
        => new() { Username = username, _passwordBytes = passwordBytes };

    public static PlaintextCredential WithPrivateKey(string username, byte[] privateKeyPem)
        => new() { Username = username, _privateKeyPem = privateKeyPem };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_passwordBytes is not null) { CryptographicOperations.ZeroMemory(_passwordBytes); _passwordBytes = null; }
        if (_privateKeyPem is not null) { CryptographicOperations.ZeroMemory(_privateKeyPem); _privateKeyPem = null; }
    }
}

public readonly ref struct PasswordHandle(byte[] bytes)
{
    private readonly byte[] _bytes = bytes;

    public ReadOnlySpan<byte> Span => _bytes;

    public string AsString() => System.Text.Encoding.UTF8.GetString(_bytes);
}

public readonly ref struct PrivateKeyHandle(byte[] pem)
{
    private readonly byte[] _pem = pem;

    public ReadOnlySpan<byte> Span => _pem;
}
