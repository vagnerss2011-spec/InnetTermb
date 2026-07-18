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

    /// <summary>
    /// Entra numa conta existente (device novo ou reinstalação) e recupera a AMK.
    ///
    /// <para><paramref name="totpCode"/> é nulo no 1º intento; se a conta tiver 2FA ativa, a
    /// implementação lança <c>MfaRequiredException</c> e a UI reenvia com o código de 6 dígitos.</para>
    /// </summary>
    Task<AccountSession> LoginAsync(
        string email, char[] password, string? totpCode = null, CancellationToken ct = default);

    // ── Recuperação de senha por email (spec Fase 4) ──────────────────────────

    /// <summary>
    /// "Esqueci a senha": dispara o email de recuperação. Não sinaliza se a conta existe
    /// (anti-enumeração) — a UI mostra a MESMA mensagem em qualquer caso.
    /// </summary>
    Task RequestPasswordResetAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Conclui o reset: o token do email autoriza o ACESSO e a chave de recuperação reabre o COFRE. O
    /// núcleo desembrulha a AMK com a chave de recuperação e a re-embrulha sob a senha nova (a AMK não
    /// muda → segredos intactos). Lança <c>CryptographicException</c> se a chave de recuperação estiver
    /// errada e <c>CloudSyncException</c> se o token for inválido/expirado.
    ///
    /// <para>Não devolve sessão de propósito: o reset revoga todas as sessões no servidor; o operador
    /// entra de novo pela tela de login (que já trata 2FA). O <paramref name="newPassword"/> é do
    /// CHAMADOR, que o zera depois (a implementação NÃO toma posse).</para>
    /// </summary>
    Task ResetPasswordWithRecoveryKeyAsync(
        string token, string recoveryKey, char[] newPassword, CancellationToken ct = default);
}
