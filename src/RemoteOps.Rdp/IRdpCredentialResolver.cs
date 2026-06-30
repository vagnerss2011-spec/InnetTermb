namespace RemoteOps.Rdp;

/// <summary>
/// Resolve a senha de uma credencial via vault. Análogo a IWinBoxCredentialResolver:
/// retorna a senha em texto puro apenas para a fronteira que exige (ClearTextPassword
/// do MSTSCAX) — o chamador deve usar e descartar a string imediatamente.
/// </summary>
public interface IRdpCredentialResolver
{
    Task<string?> ResolvePasswordAsync(string credentialRefId, CancellationToken ct = default);
}
