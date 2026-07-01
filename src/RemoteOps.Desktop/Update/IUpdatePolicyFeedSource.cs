namespace RemoteOps.Desktop.Update;

/// <summary>
/// Fonte da versão mínima exigida (ADR-019 §3), independente do feed de releases do
/// Velopack — o releases.json do Velopack não carrega esse campo.
/// </summary>
public interface IUpdatePolicyFeedSource
{
    /// <summary>
    /// Retorna a versão mínima exigida, ou null se a política não estiver configurada
    /// ou não puder ser lida agora. Falha de rede/parse nunca bloqueia o app (fail-open):
    /// sem conseguir confirmar a política, não há gate de atualização forçada.
    /// </summary>
    Task<AppVersion?> GetMinimumRequiredVersionAsync(CancellationToken ct = default);
}
