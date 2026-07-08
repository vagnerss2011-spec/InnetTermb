using System;
using System.Threading;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Security.Vault;

namespace RemoteOps.Desktop.Credentials;

/// <summary>
/// Credenciais "inline": usuário/senha digitados no cadastro do DISPOSITIVO, guardados no MESMO
/// cofre das credenciais do Keychain (envelope encryption / DPAPI — nunca texto puro), porém
/// PRESAS ao endpoint e ESCONDIDAS do Keychain. A marcação é <c>CredentialRef.Scope =
/// "endpoint:&lt;endpointId&gt;"</c>: os stores excluem credenciais com escopo da lista do workspace
/// (<c>WHERE scope IS NULL OR scope = workspace</c>), então nem o Keychain nem o dropdown do editor
/// as mostram. O provider SSH/Telnet resolve por <c>CredentialRefId</c> (busca direta por id, sem
/// filtro de escopo), então o caminho de conexão não muda. Centraliza a parte sensível (cofre +
/// convenção de escopo) num lugar só, testável.
/// </summary>
public interface IInlineCredentialService
{
    /// <summary>
    /// Cria uma credencial de senha presa ao endpoint (escondida do Keychain) e devolve o id do
    /// <see cref="CredentialRef"/>. Zera <paramref name="password"/> após guardar no cofre.
    /// </summary>
    Task<string> CreateForEndpointAsync(
        string workspaceId, string endpointId, string username, char[] password, CancellationToken ct = default);

    /// <summary>
    /// Se o endpoint usa uma credencial INLINE, revoga o segredo no cofre e apaga o
    /// <see cref="CredentialRef"/>. No-op se for credencial do Keychain (compartilhada) ou sem credencial.
    /// </summary>
    Task DeleteForEndpointAsync(Endpoint endpoint, CancellationToken ct = default);

    /// <summary>true se o escopo indica uma credencial inline (presa a endpoint).</summary>
    bool IsInlineScope(string? scope);
}

public sealed class InlineCredentialService : IInlineCredentialService
{
    internal const string ScopePrefix = "endpoint:";
    private const string Actor = "local-user";

    private readonly ILocalStore _store;
    private readonly IVault _vault;

    public InlineCredentialService(ILocalStore store, IVault vault)
    {
        _store = store;
        _vault = vault;
    }

    /// <summary>Escopo de uma credencial inline presa ao endpoint informado.</summary>
    public static string ScopeFor(string endpointId) => ScopePrefix + endpointId;

    public bool IsInlineScope(string? scope) =>
        scope is not null && scope.StartsWith(ScopePrefix, StringComparison.Ordinal);

    public async Task<string> CreateForEndpointAsync(
        string workspaceId, string endpointId, string username, char[] password, CancellationToken ct = default)
    {
        string credId = Guid.NewGuid().ToString("n");
        try
        {
            SecretEnvelope env = await _vault.StoreAsync(
                new VaultStoreRequest
                {
                    WorkspaceId = workspaceId,
                    CredentialId = credId,
                    Type = CredentialTypes.Password,
                    ActorUserId = Actor,
                },
                password,
                ct);

            await _store.AddCredentialRefAsync(new CredentialRef
            {
                Id = credId,
                Name = "(senha do dispositivo)",
                Type = CredentialTypes.Password,
                Scope = ScopeFor(endpointId),
                Metadata = new CredentialMetadata { Username = username.Trim() },
                SecretEnvelopeId = env.EnvelopeId,
            }, ct);
        }
        finally
        {
            Array.Clear(password); // zera a cópia gerenciada da senha assim que possível
        }
        return credId;
    }

    public async Task DeleteForEndpointAsync(Endpoint endpoint, CancellationToken ct = default)
    {
        if (endpoint.CredentialRefId is not { } credId)
        {
            return;
        }
        CredentialRef? cred = await _store.GetCredentialRefAsync(credId, ct);
        if (cred is null || !IsInlineScope(cred.Scope))
        {
            return; // credencial do Keychain (compartilhada): NÃO apaga junto com o device
        }
        if (cred.SecretEnvelopeId is { } envId)
        {
            await _vault.RevokeAsync(envId, new VaultAccessContext { ActorUserId = Actor }, ct);
        }
        await _store.DeleteCredentialRefAsync(credId, ct);
    }
}
