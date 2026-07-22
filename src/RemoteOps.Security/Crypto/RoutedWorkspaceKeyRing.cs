namespace RemoteOps.Security.Crypto;

/// <summary>
/// UM cofre, VÁRIAS raízes. Responde "qual raiz serve este workspace?" por PREFIXO do id — lei de
/// nomenclatura, decidida sem I/O e idêntica em todo device — e não por estado de sessão: uma
/// resolução que variasse entre duas chamadas selaria com a chave de uma raiz e o envelope só
/// falharia ao ser aberto, meses depois.
///
/// <para><b>Por que isto existe:</b> o app precisa, na mesma sessão, do cofre do TIME (WK aleatória
/// compartilhada) e de segredos que são da MÁQUINA — a chave do banco SQLCipher (<c>local</c>) e os
/// tokens de sessão (sob o GUID do servidor). Trocar a raiz do <c>CredentialVault</c> inteiro para a
/// WK faria o app procurar a chave do banco no cofre do time, não achar, criar outra — e o banco
/// local ficaria ilegível. Era essa a objeção do estágio 1d, e o roteamento a dissolve: nenhum id do
/// app começa com o prefixo do time, então nada que é da máquina é procurado no cofre compartilhado.</para>
///
/// <para><b>NÃO é <see cref="IDisposable"/> de propósito:</b> ele não é dono dos anéis roteados.
/// Quem os descarta é quem os construiu (o <c>VaultRootActivator</c>) — o mesmo anel do time serve
/// também o fluxo de convite, e zerar a AMK dele por aqui derrubaria o convite sem relação aparente
/// com o cofre.</para>
/// </summary>
public sealed class RoutedWorkspaceKeyRing : IWorkspaceKeyRing
{
    private readonly IWorkspaceKeyRing _padrao;
    private readonly IReadOnlyList<(string Prefixo, IWorkspaceKeyRing Anel)> _rotas;

    /// <param name="padrao">
    /// A raiz de quem não casa com rota nenhuma. No app é a da AMK — o que mantém o boot pessoal
    /// byte a byte igual ao de hoje, porque <c>ws-local</c>, <c>local</c> e o GUID dos tokens não
    /// casam com prefixo nenhum.
    /// </param>
    /// <param name="rotas">
    /// Prefixo → raiz, avaliadas na ORDEM em que vêm. Ordem, e não "o prefixo mais longo": um
    /// critério implícito é uma regra que ninguém lê, e aqui a regra errada não dá erro — sela com a
    /// chave errada.
    /// </param>
    public RoutedWorkspaceKeyRing(
        IWorkspaceKeyRing padrao, IReadOnlyList<(string Prefixo, IWorkspaceKeyRing Anel)> rotas)
    {
        ArgumentNullException.ThrowIfNull(padrao);
        ArgumentNullException.ThrowIfNull(rotas);

        foreach ((string prefixo, IWorkspaceKeyRing anel) in rotas)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prefixo, nameof(rotas));
            ArgumentNullException.ThrowIfNull(anel, nameof(rotas));
        }

        _padrao = padrao;
        _rotas = rotas;
    }

    public Task<WorkspaceKey> GetOrCreateWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        return Route(workspaceId).GetOrCreateWorkspaceKeyAsync(workspaceId, ct);
    }

    public Task<WorkspaceKey?> TryGetWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        return Route(workspaceId).TryGetWorkspaceKeyAsync(workspaceId, ct);
    }

    /// <summary>
    /// Puramente sintático: só o id decide. Sem I/O, sem estado de sessão, sem cache — a mesma
    /// pergunta dá a mesma resposta em qualquer device e em qualquer instante. É essa estabilidade
    /// que impede um envelope de ser selado com a chave de uma raiz e aberto com a de outra.
    /// </summary>
    private IWorkspaceKeyRing Route(string workspaceId)
    {
        foreach ((string prefixo, IWorkspaceKeyRing anel) in _rotas)
        {
            if (workspaceId.StartsWith(prefixo, StringComparison.Ordinal))
            {
                return anel;
            }
        }

        return _padrao;
    }
}
