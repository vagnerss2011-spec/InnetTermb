namespace RemoteOps.Desktop.Account;

/// <summary>
/// Criar conta / entrar, do ponto de vista da UI. Existe pra que o <c>AccountViewModel</c> não
/// conheça cripto nem HTTP — e pra que os testes de VM não paguem o Argon2id (64 MiB × 3 iterações
/// por chamada, de propósito).
///
/// Contrato da senha: o chamador é o DONO do <c>char[]</c> e o zera depois (a implementação NÃO
/// toma posse) — ver <c>AccountViewModel.SubmitAsync</c>, que zera no <c>finally</c>.
/// </summary>
public interface IAccountAuthenticator
{
    /// <summary>
    /// Cria a conta + o primeiro workspace. A sessão devolvida traz a chave de recuperação, que a UI
    /// tem a OBRIGAÇÃO de exibir uma vez (sem ela + sem a senha, o cofre é irrecuperável por design).
    /// </summary>
    Task<AccountSession> RegisterAsync(
        string email, char[] password, string workspaceName, CancellationToken ct = default);

    /// <summary>Entra numa conta existente (device novo ou reinstalação) e recupera a AMK.</summary>
    Task<AccountSession> LoginAsync(string email, char[] password, CancellationToken ct = default);
}
