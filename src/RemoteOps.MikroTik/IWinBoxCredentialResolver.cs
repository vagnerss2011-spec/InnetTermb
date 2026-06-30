namespace RemoteOps.MikroTik;

public interface IWinBoxCredentialResolver
{
    Task<string?> ResolvePasswordAsync(string credentialRefId, CancellationToken ct = default);
}
