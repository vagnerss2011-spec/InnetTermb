namespace RemoteOps.Desktop.Account;

/// <summary>
/// As identidades locais que o cofre usa. Ficam num lugar só porque a Fase 1 depende de a migração
/// de raiz (DPAPI → AMK) cobrir TODAS elas: um workspace esquecido não degrada, TRAVA o app.
/// </summary>
internal static class AppRuntime
{
    /// <summary>
    /// Workspace das credenciais do operador (hosts, keychain, senhas inline). Espelha o
    /// <c>AppCompositionRoot.DefaultWorkspaceId</c> / <c>WorkspaceViewModel.WorkspaceId</c>.
    /// </summary>
    internal const string CredentialsWorkspace = "ws-local";

    /// <summary>
    /// Workspace sob o qual mora a CHAVE DO BANCO SQLCipher. Sim, a chave do banco é ela própria um
    /// segredo do cofre (<c>VaultDbKeyProvider</c> guarda o hex dela num envelope) — e o id vem do
    /// <c>OpenWorkspaceAsync("local")</c>, que é diferente do workspace das credenciais.
    /// </summary>
    internal const string DbWorkspace = "local";

    /// <summary>
    /// Tudo que a migração de raiz precisa cobrir. Se a chave do banco ficasse na raiz DPAPI
    /// enquanto o cofre passa pra AMK, o <c>RetrieveSecretAsync</c> lançaria CryptographicException
    /// no startup e o app não abriria — o operador veria só "Não foi possível iniciar".
    /// </summary>
    internal static readonly string[] VaultWorkspaces = [CredentialsWorkspace, DbWorkspace];

    /// <summary>
    /// Identidade LOCAL do cofre de um time (Fatia 1). Prefixada porque as três identidades convivem
    /// no MESMO arquivo de cofre sob raízes diferentes: se o cofre do time reusasse
    /// <see cref="CredentialsWorkspace"/>, as senhas do cliente e as do operador se misturariam; se
    /// colidisse com um id de chave de outra raiz, seria perda de chave — não erro de leitura.
    ///
    /// <para>O argumento é o workspace do SERVIDOR (GUID). Um time por workspace, uma identidade de
    /// cofre por time.</para>
    /// </summary>
    internal static string TeamVaultWorkspace(string serverWorkspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverWorkspaceId);
        return TeamVaultPrefix + serverWorkspaceId;
    }

    /// <summary>Prefixo da identidade do cofre de time. Nunca reusado por outra raiz.</summary>
    internal const string TeamVaultPrefix = "time:";

    /// <summary>
    /// ⚠️ <b>Boot.</b> A lista que a ativação da conta percorre. Um workspace de fora dela não
    /// degrada: TRAVA o app na abertura (já mordeu duas vezes nesta base). Por isso o cofre do time
    /// entra AQUI, e não numa segunda lista que alguém esqueceria de atualizar.
    ///
    /// <para>Sem time (<c>null</c>) a lista é EXATAMENTE a de hoje — quem nunca vai ter time não
    /// muda de comportamento em nada.</para>
    /// </summary>
    /// <param name="baseWorkspaces">
    /// A lista de partida. Existe para o ativador usar a lista que ELE recebeu (injetada nos testes)
    /// em vez da estática — assim há UM lugar que decide o que entra, e não dois divergindo.
    /// </param>
    internal static IReadOnlyList<string> VaultWorkspacesFor(
        string? teamServerWorkspaceId, IReadOnlyList<string>? baseWorkspaces = null)
    {
        IReadOnlyList<string> start = baseWorkspaces ?? VaultWorkspaces;
        if (string.IsNullOrWhiteSpace(teamServerWorkspaceId))
        {
            return start;
        }

        string team = TeamVaultWorkspace(teamServerWorkspaceId);
        return start.Contains(team, StringComparer.Ordinal) ? start : [.. start, team];
    }

    /// <summary>
    /// Este workspace do cofre é de TIME? Quem pergunta é a ativação: um cofre de time é
    /// WK-rooted (chave aleatória compartilhada) e NÃO pode passar pela migração DPAPI→AMK, que o
    /// carimbaria como derivado da conta — o carimbo errado é o começo de um cofre que não abre.
    /// </summary>
    internal static bool IsTeamVaultWorkspace(string vaultWorkspaceId)
        => vaultWorkspaceId.StartsWith(TeamVaultPrefix, StringComparison.Ordinal);
}
