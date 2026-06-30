using RemoteOps.Security;

namespace RemoteOps.Sync.Remote;

/// <summary>Configuração para montar uma <see cref="SyncSession"/> (caminho de produção, ADR-013).</summary>
public sealed record SyncSessionOptions
{
    /// <summary>Workspace SQLCipher local (outbox + local_entities + cursores/conflitos).</summary>
    public required WorkspaceContext Workspace { get; init; }

    /// <summary>Id do workspace NO SERVIDOR (GUID) — distinto da identidade local.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Base URL do Cloud (https). TLS é sempre validado.</summary>
    public required Uri CloudBaseUrl { get; init; }

    public required Guid DeviceId { get; init; }

    public required ICredentialVault Vault { get; init; }

    /// <summary>Arquivo que guarda apenas o envelopeId dos tokens (não o token).</summary>
    public required string TokenRefPath { get; init; }

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(2);

    public int PageSize { get; init; } = 200;
}

/// <summary>
/// Monta o grafo do cliente de sync (api HTTP + applier + metadata store + orquestrador + canal
/// SignalR) numa <see cref="SyncSession"/>. Chamado pelo Desktop apenas com a flag cloud.sync.enabled.
/// </summary>
public static class SyncSessionFactory
{
    public static SyncSession Create(SyncSessionOptions options)
    {
        // Invariante TLS-always (ADR-013): nunca falar com o Cloud por HTTP, senão o Bearer e o
        // refresh token trafegariam em claro. O hub SignalR herda o scheme de CloudBaseUrl.
        if (options.CloudBaseUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "CloudBaseUrl deve usar HTTPS — tokens nunca trafegam em claro (ADR-013).",
                nameof(options));
        }

        var tokenStore = new VaultTokenStore(options.Vault, options.WorkspaceId, options.TokenRefPath);

        // HttpClient de vida longa (reuso recomendado); BaseAddress https → TLS validado por padrão.
        var http = new HttpClient { BaseAddress = options.CloudBaseUrl };
        var api = new CloudSyncApiClient(http, options.DeviceId, tokenStore);

        var applier = new LocalEntitiesChangeApplier(options.Workspace);
        var metadata = new SqliteSyncMetadataStore(options.Workspace);
        var orchestrator = new SyncOrchestrator(
            options.WorkspaceId, options.Workspace.SyncClient, api, applier, metadata, options.PageSize);

        var hubUrl = new Uri(options.CloudBaseUrl, "/hubs/sync");
        var hints = new SignalRSyncHintChannel(
            hubUrl, async () => (await tokenStore.LoadAsync())?.AccessToken);

        return new SyncSession(orchestrator, hints, options.WorkspaceId, options.Interval);
    }
}
