using RemoteOps.Security;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

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

    /// <summary>
    /// Teto de atraso quando o canal de hints está morto (rede que bloqueia WebSocket). Era 2 min, o
    /// que fazia o operador concluir que o sync tinha travado e reiniciar o app. 45s é rede de
    /// segurança, não o caminho principal — o tempo real vem dos hints. Opção de CÓDIGO: não vai à
    /// tela de Configurações, porque ninguém tem como escolher isto melhor que o default.
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(45);

    public int PageSize { get; init; } = 200;

    // ── Canal de segredos (spec §5). Opcional: sem os dois, a sessão sincroniza só metadados. ──

    /// <summary>
    /// Cofre local, para ENUMERAR os envelopes a subir e GRAVAR os que descem. <c>null</c> desliga o
    /// transporte de segredos (ex.: cofre ainda enraizado em DPAPI, ou build sem conta E2EE).
    /// </summary>
    public IVaultMigrationStore? EnvelopeStore { get; init; }

    /// <summary>
    /// Workspace do COFRE cujos segredos sincronizam (ex.: <c>AppRuntime.CredentialsWorkspace</c>).
    /// Distinto do <see cref="WorkspaceId"/> (servidor) e ESCOPO do que sobe: a chave do banco e os
    /// tokens moram noutros workspaces do cofre justamente pra nunca entrarem aqui.
    /// </summary>
    public string? VaultWorkspaceId { get; init; }

    /// <summary>Versão do esquema de embrulho da AMK da conta (spec §4.2).</summary>
    public int AmkKeyVersion { get; init; } = 1;

    /// <summary>
    /// RAIZ das senhas desta sessão (<c>VaultAlgorithms.*</c>). Só entra em jogo quando o fio NÃO
    /// ecoa <c>algorithm</c> — registro gravado antes do campo existir, ou servidor antigo: ali,
    /// assumir AMK num cofre de TIME grava um envelope que monta o AAD errado e nunca abre, sem
    /// erro na hora. O default preserva o cofre pessoal, byte a byte.
    ///
    /// <para>Antes desta fatia o parâmetro existia no <c>SecretSyncOrchestrator</c> e esta fábrica
    /// simplesmente não o informava — o valor certo existia e não chegava a quem precisava dele.</para>
    /// </summary>
    public string VaultAlgorithm { get; init; } = VaultAlgorithms.AmkRootedV1;
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

        // UM canal de auth para os dois clientes: o backend rotaciona o refresh token, então dois
        // caches independentes brigariam pelo mesmo refresh e derrubariam a sessão (ver CloudAuthChannel).
        var channel = new CloudAuthChannel(http, options.DeviceId, tokenStore);
        var api = new CloudSyncApiClient(channel);

        var applier = new LocalEntitiesChangeApplier(options.Workspace);
        var metadata = new SqliteSyncMetadataStore(options.Workspace);
        SecretSyncOrchestrator? secrets = CreateSecretSync(options, channel, metadata);
        var orchestrator = new SyncOrchestrator(
            options.WorkspaceId, options.Workspace.SyncClient, api, applier, metadata, options.PageSize,
            secrets);

        var hubUrl = new Uri(options.CloudBaseUrl, "/hubs/sync");
        var hints = new SignalRSyncHintChannel(
            hubUrl, async () => (await tokenStore.LoadAsync())?.AccessToken);

        // Fase 2, item A: liga o push-ao-mudar à MESMA fonte que o orquestrador drena (o outbox do
        // workspace). Uma edição local levanta LocalChangePushed nesse ISyncClient → a sessão debounça
        // e sincroniza — sem esperar o tick do laço.
        return new SyncSession(
            orchestrator, hints, options.WorkspaceId, options.Interval,
            localChanges: options.Workspace.SyncClient);
    }

    /// <summary>
    /// Monta o canal de segredos, ou <c>null</c> se a sessão não pediu. Os dois campos andam juntos
    /// de propósito: sem saber QUAL workspace do cofre sincronizar, o transporte não tem escopo — e
    /// um escopo errado significaria subir a chave do banco ou os tokens (que também moram no cofre).
    /// </summary>
    private static SecretSyncOrchestrator? CreateSecretSync(
        SyncSessionOptions options, CloudAuthChannel channel, ISyncMetadataStore metadata)
    {
        if (options.EnvelopeStore is null && options.VaultWorkspaceId is null)
        {
            return null; // sessão só de metadados — comportamento pré-Fase 1.
        }

        if (options.EnvelopeStore is null || string.IsNullOrWhiteSpace(options.VaultWorkspaceId))
        {
            throw new ArgumentException(
                "Para sincronizar segredos informe EnvelopeStore E VaultWorkspaceId — um sem o outro " +
                "deixaria o transporte sem cofre ou sem escopo.",
                nameof(options));
        }

        return new SecretSyncOrchestrator(
            options.WorkspaceId,
            options.VaultWorkspaceId,
            options.EnvelopeStore,
            new SecretsApiClient(channel),
            metadata,
            options.AmkKeyVersion,
            options.PageSize,
            options.VaultAlgorithm);
    }
}
