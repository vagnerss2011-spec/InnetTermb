using System.Security.Cryptography;

namespace RemoteOps.Security.Crypto;

/// <summary>
/// Workspace Data Key (WDK) viva em memória. Zera o buffer no <see cref="Dispose"/>
/// para minimizar o lifetime do material de chave.
/// </summary>
public sealed class WorkspaceKey : IDisposable
{
    private readonly byte[] _key;
    private readonly string _algorithmId;
    private bool _disposed;

    /// <param name="algorithmId">
    /// O esquema que ESTA chave produz (ver <see cref="Vault.VaultAlgorithms"/>). Vem junto do
    /// material de propósito: com o cofre atendendo duas raízes ao mesmo tempo, um carimbo pedido em
    /// separado pode vir da OUTRA raiz — e um envelope selado sob a WK declarando <c>AmkRootedV1</c>
    /// carrega uma mentira sobre a própria chave. O erro não aparece no dia em que é cometido:
    /// aparece meses depois, no computador do colega, como "a senha não abre". Preso ao material, o
    /// carimbo não tem como divergir da chave — a impossibilidade é do TIPO, e não uma concordância
    /// feliz entre duas consultas.
    /// </param>
    internal WorkspaceKey(byte[] key, string algorithmId)
    {
        _key = key;
        _algorithmId = algorithmId;
    }

    public ReadOnlyMemory<byte> Key
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _key;
        }
    }

    /// <summary>
    /// O esquema sob o qual esta chave sela — é ele que vai em <c>SecretEnvelope.Algorithm</c>.
    /// Continua legível depois do <see cref="Dispose"/>: o carimbo não é segredo, e um
    /// <c>ObjectDisposedException</c> aqui só transformaria uma leitura inócua em falha.
    /// </summary>
    public string AlgorithmId => _algorithmId;

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
