using RemoteOps.MikroTik.Models;

namespace RemoteOps.MikroTik;

public sealed record WinBoxPolicyDecision(bool Allowed, bool AllowPasswordArgument, string? DenyReason);

public interface IWinBoxPolicyProvider
{
    /// <summary>
    /// Evaluates whether the launch request is permitted by tenant/group policy.
    /// </summary>
    Task<WinBoxPolicyDecision> EvaluateAsync(WinBoxLaunchRequest request, CancellationToken ct = default);
}

/// <summary>
/// Simple policy provider for MVP: reads the AllowPasswordArgument flag from the host profile.
/// Replace with a real policy engine (RBAC, tenant settings) when available.
/// </summary>
public sealed class LocalWinBoxPolicyProvider(bool globalAllowPasswordArgument = false) : IWinBoxPolicyProvider
{
    public Task<WinBoxPolicyDecision> EvaluateAsync(WinBoxLaunchRequest request, CancellationToken ct = default)
    {
        var allowPassword = globalAllowPasswordArgument && request.IncludePasswordArgument;

        return Task.FromResult(new WinBoxPolicyDecision(
            Allowed: true,
            AllowPasswordArgument: allowPassword,
            DenyReason: null
        ));
    }
}
