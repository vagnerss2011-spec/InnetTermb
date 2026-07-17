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
}
