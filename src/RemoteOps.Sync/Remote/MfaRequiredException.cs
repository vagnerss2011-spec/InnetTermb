namespace RemoteOps.Sync.Remote;

/// <summary>
/// O login provou a senha (AuthHash), mas a conta tem 2FA ativa e falta (ou está errado) o código
/// TOTP. Distinta de <see cref="CloudSyncException"/> de propósito: a UI reage pedindo o código de 6
/// dígitos e reenviando o login — não é "credencial inválida".
///
/// <para>O backend sinaliza isto com um 401 cujo corpo carrega <c>error: "mfa_required"</c>
/// (ProblemDetails estruturado). Só quem já provou a senha chega a receber este sinal, então ele não
/// vira oráculo de enumeração.</para>
/// </summary>
public sealed class MfaRequiredException : Exception
{
    public MfaRequiredException()
        : base("A conta exige verificação em duas etapas: informe o código do aplicativo autenticador.")
    {
    }
}
