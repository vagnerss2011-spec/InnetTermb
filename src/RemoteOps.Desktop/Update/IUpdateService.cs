namespace RemoteOps.Desktop.Update;

/// <summary>
/// Verificação e aplicação de atualização (ADR-019). <see cref="CheckForUpdatesAsync"/>
/// nunca baixa nada — apenas reporta o que há disponível e se a política de update
/// forçado exige atualizar. Download/aplicação só ocorrem via <see cref="ApplyUpdateAsync"/>,
/// chamado explicitamente pela camada de apresentação (ação do operador, ou o prompt
/// obrigatório quando <see cref="UpdatePolicyResult.MustUpdate"/> é true).
/// </summary>
public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default);

    Task ApplyUpdateAsync(UpdateCheckResult update, CancellationToken ct = default);
}
