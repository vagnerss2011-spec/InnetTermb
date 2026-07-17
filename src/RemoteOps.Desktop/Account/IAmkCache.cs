using System.Security.Cryptography;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// A conta em repouso neste device: identidade + a AMK desembrulhada. Classe (não record) pelo mesmo
/// motivo do <see cref="AccountSession"/> — <see cref="Amk"/> é material de chave vivo, e a
/// igualdade estrutural/ToString() de um record convidariam a comparar ou logar a raiz do cofre.
/// </summary>
public sealed class CachedAccount : IDisposable
{
    private const int AmkSize = 32;

    private bool _disposed;

    /// <param name="amk">A AMK (32B). O <see cref="CachedAccount"/> toma posse e zera no Dispose.</param>
    public CachedAccount(string email, string workspaceId, int amkKeyVersion, byte[] amk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(amk);
        if (amk.Length != AmkSize)
        {
            throw new ArgumentException($"A AMK precisa ter {AmkSize} bytes (recebidos {amk.Length}).", nameof(amk));
        }

        Email = email;
        WorkspaceId = workspaceId;
        AmkKeyVersion = amkKeyVersion;
        Amk = amk;
    }

    public string Email { get; }

    public string WorkspaceId { get; }

    /// <summary>Versão do esquema de embrulho da AMK (spec §4.2 <c>amk_key_version</c>).</summary>
    public int AmkKeyVersion { get; }

    /// <summary>Raiz portável do cofre (32B). Nunca serializar em claro, logar ou mandar pra rede.</summary>
    public byte[] Amk { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(Amk);
        _disposed = true;
    }
}

/// <summary>
/// Cache local da AMK (spec §4.3). Depois do login/unlock a AMK fica em repouso protegida por DPAPI
/// CurrentUser, pra o app reabrir sem pedir a senha; a KEK e a MasterKey NUNCA são cacheadas — a
/// portabilidade entre devices vem do escrow no servidor, o DPAPI protege só esta cópia local.
///
/// <para>Abstraído pra que o <see cref="AccountSyncCoordinator"/> seja testável sem tocar disco nem
/// DPAPI, no mesmo espírito do <c>ILocalKeyProtector</c>.</para>
/// </summary>
public interface IAmkCache
{
    /// <summary>A conta cacheada, ou <c>null</c> se não há (primeiro uso, logout, cache corrompido).</summary>
    Task<CachedAccount?> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(CachedAccount account, CancellationToken ct = default);

    /// <summary>Apaga o cache (logout/trocar conta). Idempotente.</summary>
    Task ClearAsync(CancellationToken ct = default);
}
