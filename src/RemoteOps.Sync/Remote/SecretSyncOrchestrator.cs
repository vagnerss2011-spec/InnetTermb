using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Ciclo de sync dos <c>SecretEnvelope</c> (spec §5/§6): PUSH dos envelopes locais ainda não
/// enviados → PULL dos remotos desde o cursor → GRAVA no cofre local, opacos. A decifração acontece
/// DEPOIS, local, quando alguém pede o segredo ao cofre — este orquestrador nunca abre nada.
///
/// <para><b>Por que é uma classe separada do <see cref="SyncOrchestrator"/>, e ainda assim roda
/// DENTRO do ciclo dele:</b> separada porque é outro canal (endpoints próprios), outro cursor, outra
/// disciplina de conflito e outro material — o changelog RECUSA <c>SecretEnvelope</c> de propósito
/// (ADR-003), e fundir os dois apagaria justamente a fronteira que a arquitetura quer explícita.
/// Rodando dentro do ciclo do <see cref="SyncOrchestrator"/> porque as duas metades precisam ser
/// serializadas juntas (um refresh de token por vez), compartilhar a postura de erro
/// (offline-first: falhou, marca Error e tenta no próximo intervalo) e, principalmente, respeitar a
/// ORDEM: os metadados (<c>credential_ref</c>) primeiro, o blob do segredo depois — assim o device
/// que recebe nunca tem uma senha órfã de credencial.</para>
///
/// <para><b>Idempotente e retomável.</b> No pull, o envelope é gravado ANTES de o cursor avançar: uma
/// queda no meio faz o ciclo seguinte re-baixar a página (no-op, mesmo conteúdo) em vez de PULAR um
/// envelope pra sempre. No push, o ledger local (<c>secrets_pushed</c>) evita o POST repetido, e o
/// servidor recusa versão &lt;= atual sem avançar cursor — ou seja, mesmo um re-push é no-op.</para>
/// </summary>
public sealed class SecretSyncOrchestrator
{
    private readonly string _serverWorkspaceId;
    private readonly string _vaultWorkspaceId;
    private readonly IVaultMigrationStore _store;
    private readonly ISecretsApi _api;
    private readonly ISyncMetadataStore _metadata;
    private readonly int _amkKeyVersion;
    private readonly int _pageSize;

    // Entrada pública: além do ciclo de fundo, um "forçar sync" da UI pode chamar isto. Dois ciclos
    // concorrentes fariam read-modify-write não atômico do cursor e do ledger.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="serverWorkspaceId">Workspace no SERVIDOR (GUID) — o que vai no fio.</param>
    /// <param name="vaultWorkspaceId">
    /// Workspace do COFRE (ex.: "ws-local") — a identidade sob a qual os segredos estão selados.
    /// É o ESCOPO do que sobe: a chave do banco SQLCipher e os tokens de sessão também moram no
    /// cofre, em OUTROS workspaces (<c>AppRuntime.DbWorkspace</c> e o GUID do servidor), e não podem
    /// subir nunca. Escopar por aqui é o que os mantém em casa.
    /// </param>
    /// <param name="store">
    /// Cofre local. Precisa enumerar o workspace, daí o <see cref="IVaultMigrationStore"/> (e não o
    /// <c>ICredentialStore</c> puro, que só tem CRUD por id).
    /// </param>
    /// <param name="amkKeyVersion">Versão do esquema de embrulho da AMK da conta (spec §4.2).</param>
    public SecretSyncOrchestrator(
        string serverWorkspaceId,
        string vaultWorkspaceId,
        IVaultMigrationStore store,
        ISecretsApi api,
        ISyncMetadataStore metadata,
        int amkKeyVersion = 1,
        int pageSize = 200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverWorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultWorkspaceId);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(metadata);

        _serverWorkspaceId = serverWorkspaceId;
        _vaultWorkspaceId = vaultWorkspaceId;
        _store = store;
        _api = api;
        _metadata = metadata;
        _amkKeyVersion = amkKeyVersion;
        _pageSize = pageSize;
    }

    /// <summary>
    /// Um ciclo completo: push do que falta → pull do que chegou. RELANÇA em falha de rede/servidor
    /// — quem decide a postura é o <see cref="SyncOrchestrator"/>, que já trata isso como
    /// "Error, tenta no próximo intervalo" (offline-first, ADR-002).
    /// </summary>
    public async Task SyncOnceAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await PushLocalAsync(ct);
            await PullRemoteAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    // PUSH: envelopes do workspace do cofre que ainda não estão no servidor na versão local.
    private async Task PushLocalAsync(CancellationToken ct)
    {
        IReadOnlyList<SecretEnvelope> local = await _store.ListEnvelopesAsync(_vaultWorkspaceId, ct);
        IReadOnlyDictionary<string, int> pushed =
            await _metadata.GetPushedSecretsAsync(_serverWorkspaceId, ct);

        foreach (SecretEnvelope envelope in local)
        {
            ct.ThrowIfCancellationRequested();

            if (!SecretEnvelopeWireCodec.IsSyncable(envelope))
            {
                continue;
            }

            if (pushed.TryGetValue(envelope.EnvelopeId, out int sentVersion) && sentVersion >= envelope.Version)
            {
                continue; // o servidor já tem esta versão — nada a fazer.
            }

            SecretEnvelopeDto dto = SecretEnvelopeWireCodec.ToWire(envelope, _serverWorkspaceId, _amkKeyVersion);

            // Um envelope por chamada (e não o lote inteiro) porque assim a marca no ledger é gravada
            // logo depois do POST correspondente: uma queda no meio deixa o já-enviado marcado, e o
            // ciclo seguinte retoma de onde parou em vez de re-subir tudo.
            IReadOnlyList<SecretUpsertResult> results = await _api.PushAsync(_serverWorkspaceId, [dto], ct);
            if (results.Count == 0)
            {
                continue;
            }

            if (ShouldMarkPushed(results[0]))
            {
                await _metadata.MarkSecretPushedAsync(
                    _serverWorkspaceId, envelope.EnvelopeId, envelope.Version, ct);
            }
        }
    }

    /// <summary>
    /// "Já está no servidor?" — e só isso marca o ledger.
    ///
    /// <para><c>ok</c>: subiu agora. <c>version.conflict</c>: o servidor já tem esta versão ou uma
    /// mais nova, então insistir seria um POST inútil por ciclo, pra sempre.</para>
    ///
    /// <para>Os OUTROS conflitos NÃO marcam de propósito: <c>cursor.race-retry</c> é uma corrida que
    /// o servidor manda re-tentar, e <c>envelope.workspace-mismatch</c> é anomalia real (o id existe
    /// noutro workspace) — marcar como enviado esconderia um envelope que nunca subiu.</para>
    /// </summary>
    private static bool ShouldMarkPushed(SecretUpsertResult result) =>
        string.Equals(result.Status, "ok", StringComparison.Ordinal)
        || string.Equals(result.Reason, "version.conflict", StringComparison.Ordinal);

    // PULL: baixa as páginas e grava no cofre. Opacos: nada aqui abre envelope.
    private async Task PullRemoteAsync(CancellationToken ct)
    {
        long cursor = await _metadata.GetSecretsCursorAsync(_serverWorkspaceId, ct);

        while (true)
        {
            SecretsPullResponse response = await _api.PullAsync(_serverWorkspaceId, cursor, _pageSize, ct);

            foreach (SecretEnvelopeDto dto in response.Envelopes)
            {
                ct.ThrowIfCancellationRequested();
                await ApplyAsync(dto, ct);
            }

            // O cursor só avança DEPOIS de a página inteira estar gravada: se cair no meio, o ciclo
            // seguinte re-baixa a página (idempotente) em vez de pular envelope.
            cursor = response.NextCursor;
            await _metadata.SaveSecretsCursorAsync(_serverWorkspaceId, cursor, ct);

            if (!response.HasMore)
            {
                return;
            }
        }
    }

    private async Task ApplyAsync(SecretEnvelopeDto dto, CancellationToken ct)
    {
        SecretEnvelope incoming = SecretEnvelopeWireCodec.FromWire(dto, _vaultWorkspaceId);

        SecretEnvelope? existing = await _store.GetAsync(incoming.EnvelopeId, ct);
        if (existing is not null && existing.Version > incoming.Version)
        {
            return; // sem downgrade: versão local mais nova vence (monotônico por id).
        }

        await _store.SaveAsync(incoming, ct);

        // O que veio do servidor JÁ está no servidor: marca no ledger pra não devolver como push.
        // Sem isto, o device B re-subiria tudo que baixou → o servidor queimaria cursor → o A
        // re-baixaria → churn infinito entre dois devices parados.
        await _metadata.MarkSecretPushedAsync(
            _serverWorkspaceId, incoming.EnvelopeId, incoming.Version, ct);
    }
}
