namespace RemoteOps.MikroTik;

public sealed class WinBoxPolicyDecision
{
    public required bool Allowed { get; init; }
    public string? DenyReason { get; init; }

    // Senha via argumento desativada por padrão (Modo A).
    public bool PasswordArgumentAllowed { get; init; }
}

public interface IWinBoxPolicyProvider
{
    Task<WinBoxPolicyDecision> EvaluateAsync(
        string workspaceId,
        string? hostId,
        string actorUserId,
        CancellationToken ct = default);
}

public sealed class WinBoxPolicyConfig
{
    // Modo A por padrão — senha via argumento desativada para todos.
    public bool PasswordArgumentAllowed { get; init; }
    public HashSet<string> DeniedWorkspaceIds { get; init; } = [];
    public HashSet<string> DeniedHostIds { get; init; } = [];
}

public sealed class LocalWinBoxPolicyProvider : IWinBoxPolicyProvider
{
    private readonly WinBoxPolicyConfig _config;

    public LocalWinBoxPolicyProvider(WinBoxPolicyConfig config)
    {
        _config = config;
    }

    public Task<WinBoxPolicyDecision> EvaluateAsync(
        string workspaceId,
        string? hostId,
        string actorUserId,
        CancellationToken ct = default)
    {
        if (_config.DeniedWorkspaceIds.Contains(workspaceId))
            return Task.FromResult(new WinBoxPolicyDecision
            {
                Allowed = false,
                DenyReason = $"Workspace '{workspaceId}' não tem permissão para abrir WinBox.",
            });

        if (hostId is not null && _config.DeniedHostIds.Contains(hostId))
            return Task.FromResult(new WinBoxPolicyDecision
            {
                Allowed = false,
                DenyReason = $"Host '{hostId}' não tem permissão para abrir WinBox.",
            });

        return Task.FromResult(new WinBoxPolicyDecision
        {
            Allowed = true,
            PasswordArgumentAllowed = _config.PasswordArgumentAllowed,
        });
    }
}
