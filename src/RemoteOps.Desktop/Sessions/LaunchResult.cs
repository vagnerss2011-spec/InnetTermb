namespace RemoteOps.Desktop.Sessions;

/// <summary>
/// Resultado de um lançamento de sessão. Antes disto, todo caminho de falha do
/// SessionLauncher era silencioso (return vazio ou aba morta) — o operador clicava
/// e "nada acontecia". Agora cada falha carrega uma mensagem acionável em pt-BR.
/// </summary>
public sealed record LaunchResult(bool Success, string? Error)
{
    public static LaunchResult Ok() => new(true, null);
    public static LaunchResult Fail(string error) => new(false, error);
}
