using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Rdp;
using RemoteOps.Security.Vault;
using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Integration;

/// <summary>Análogo a StoreWinBoxCredentialResolver — resolve a senha via vault, lifetime mínimo.</summary>
internal sealed class RdpCredentialResolver : IRdpCredentialResolver
{
    private readonly ILocalStore _store;
    private readonly IVault _vault;
    private readonly ITerminalSecurityContext _securityContext;

    public RdpCredentialResolver(ILocalStore store, IVault vault, ITerminalSecurityContext securityContext)
    {
        _store = store;
        _vault = vault;
        _securityContext = securityContext;
    }

    public async Task<string?> ResolvePasswordAsync(string credentialRefId, CancellationToken ct = default)
    {
        var credRef = await _store.GetCredentialRefAsync(credentialRefId, ct).ConfigureAwait(false);
        if (credRef?.SecretEnvelopeId is null) return null;

        var vaultCtx = new VaultAccessContext
        {
            ActorUserId = _securityContext.ActorUserId,
            DeviceId = _securityContext.DeviceId,
        };

        // Lifetime mínimo: `using` zera o buffer imediatamente após RevealString (ADR-009 §FIX-3).
        using var secret = await _vault.RetrieveAsync(credRef.SecretEnvelopeId, vaultCtx, ct).ConfigureAwait(false);
        return secret.RevealString();
    }
}
