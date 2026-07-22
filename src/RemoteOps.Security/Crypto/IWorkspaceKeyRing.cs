namespace RemoteOps.Security.Crypto;

/// <summary>
/// Fornece a chave de dados de cada workspace. Existem TRÊS raízes, e a diferença entre elas é a
/// quem a chave está presa: a legada (<see cref="WorkspaceKeyRing"/> — aleatória protegida por
/// DPAPI, presa à MÁQUINA), a do E2EE (<c>AmkWorkspaceKeyRing</c> — derivada da AMK, presa à CONTA)
/// e a do time (<c>WkWorkspaceKeyRing</c> — aleatória, presa a NINGUÉM, e por isso a única que pode
/// ser entregue cifrada a outro membro).
///
/// <para><b>Repare no que NÃO existe aqui: o carimbo do esquema.</b> Ele mora no
/// <see cref="WorkspaceKey"/>, junto do material. Um chaveiro que atende VÁRIAS raízes
/// (<see cref="RoutedWorkspaceKeyRing"/>) não tem UM algoritmo para declarar: manter a pergunta
/// neste objeto obrigaria o roteador a mentir (devolver o carimbo do padrão) ou a estourar — dois
/// caminhos que só existem porque a pergunta está no objeto errado. Com o carimbo viajando com a
/// chave, "carimbo divergir da chave" deixa de ser um bug possível.</para>
///
/// <para><b>E o que também NÃO existe: um caminho para FUNDAR chave nova.</b> A raiz do time faz a
/// WK nascer pelo <c>WkWorkspaceKeyRing.MintWorkspaceKeyAsync</c>, que fica fora desta interface de
/// propósito: o <c>CredentialVault</c> só enxerga o que está aqui, então nenhum toque no cofre pode
/// sortear uma chave de time. O fail-closed é do TIPO, e não de uma bandeira de construtor que
/// alguém desliga por engano numa fiação de DI.</para>
/// </summary>
public interface IWorkspaceKeyRing
{
    /// <summary>
    /// Recupera a WDK do workspace; cria e persiste (protegida) na primeira vez, <b>quando essa raiz
    /// tem o direito de criar</b>. A raiz do TIME não tem: lá a chave nasce só pelo ato de fundar o
    /// time, e aqui ela recusa alto com um recado acionável. Lança também se a chave existe mas não
    /// pode ser desprotegida (outro usuário/máquina).
    /// </summary>
    Task<WorkspaceKey> GetOrCreateWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Recupera a WDK existente, ou <c>null</c> se o workspace ainda não tem uma. Ao contrário de
    /// <see cref="GetOrCreateWorkspaceKeyAsync"/>, NÃO cria nem persiste nada. A migração de raiz
    /// depende dessa distinção: criar uma WDK nova por baixo de segredos já selados mascararia a
    /// perda da chave original como se fosse um cofre vazio.
    /// </summary>
    Task<WorkspaceKey?> TryGetWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default);
}
