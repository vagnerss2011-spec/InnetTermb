using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

using RemoteOps.Security.Account;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Isolamento POR ITEM do canal de segredos. Antes disto o ciclo era tudo-ou-nada: um único
/// envelope malformado (id fora do formato GUID gravado por uma versão antiga, cabeçalho de outro
/// cliente) estourava no meio do laço e derrubava o push E o pull — o cursor só avança depois da
/// página inteira, então o device parava de subir e de RECEBER segredo, para sempre, com um "Erro"
/// genérico como único sinal.
///
/// <para>Estes testes prendem as três decisões: item ruim é PULADO (não trava os sadios), a página
/// AVANÇA mesmo assim, e o pull roda MESMO com o push no chão.</para>
/// </summary>
public sealed class SecretChannelResilienceTests : IDisposable
{
    private const string VaultWorkspace = "ws-local";
    private static readonly string ServerWorkspace = Guid.NewGuid().ToString();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"remoteops-canal-{Guid.NewGuid():n}");
    private readonly AmkWorkspaceKeyRing _keyRing = new(RandomNumberGenerator.GetBytes(32));

    public void Dispose()
    {
        _keyRing.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ── Push ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Um envelope venenoso não pode impedir os SADIOS de subir. O veneno aqui é um
    /// <c>EnvelopeId</c> fora do formato GUID — exatamente o que o codec barra (o backend faz
    /// <c>Guid.TryParse</c>) e o que uma versão antiga do app conseguia gravar no cofre.
    /// </summary>
    [Fact]
    public async Task EnvelopeVenenoso_NaoTravaOsSadiosNoPush()
    {
        (FileVaultStore store, CredentialVault vault) = NewVault("push");
        SecretEnvelope sadio = await SealAsync(vault, "c1", "segredo-1");
        await store.SaveAsync(sadio with { EnvelopeId = "nao-e-um-guid", CredentialId = "c2" });
        await SealAsync(vault, "c3", "segredo-3");

        // Não-vacuidade: o veneno tem que estar NO MEIO da enumeração. Se fosse o último, o teste
        // passaria mesmo com o laço abortando no primeiro erro — provando nada.
        IReadOnlyList<SecretEnvelope> ordem = await store.ListEnvelopesAsync(VaultWorkspace);
        Assert.Equal("nao-e-um-guid", ordem[1].EnvelopeId);

        var api = new FakeSecretsApi();
        var orch = new SecretSyncOrchestrator(
            ServerWorkspace, VaultWorkspace, store, api, new FakeSyncMetadataStore());

        SecretSyncReport report = await orch.SyncOnceAsync();

        Assert.Equal(2, api.Accepted.Count);

        // O canal tem VOZ: diz qual item ficou para trás — e só isso (ADR-013: id + tipo do erro,
        // nunca a mensagem do servidor nem campo do envelope).
        SecretSyncSkip pulado = Assert.Single(report.Skipped);
        Assert.Equal("nao-e-um-guid", pulado.EnvelopeId);
        Assert.Equal(SecretSyncPhase.Push, pulado.Phase);
        Assert.Equal(nameof(CloudSyncException), pulado.ErrorType);
    }

    // ── Pull ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// No pull o estrago seria pior: o cursor só avança depois da página, então um dto malformado
    /// no servidor congelaria o cursor e o device nunca mais receberia NADA. Item ruim é pulado e a
    /// página avança.
    /// </summary>
    [Fact]
    public async Task DtoVenenosoNoPull_EhPulado_EAPaginaAvanca()
    {
        (_, CredentialVault origem) = NewVault("origem");
        SecretEnvelope e1 = await SealAsync(origem, "c1", "segredo-1");
        SecretEnvelope e2 = await SealAsync(origem, "c2", "segredo-2");
        SecretEnvelope e3 = await SealAsync(origem, "c3", "segredo-3");

        // Veneno: keyVersion fora do contrato "<amkKeyVersion>|<type>|<credentialId>" — o que um
        // cliente que não fala este esquema produziria. O codec recusa (e tem que recusar: sem Type
        // certo o envelope nunca abriria).
        SecretEnvelopeDto venenoso =
            SecretEnvelopeWireCodec.ToWire(e2, ServerWorkspace, 1) with { KeyVersion = "lixo" };

        var api = new PageSecretsApi(
        [
            SecretEnvelopeWireCodec.ToWire(e1, ServerWorkspace, 1),
            venenoso,
            SecretEnvelopeWireCodec.ToWire(e3, ServerWorkspace, 1),
        ]);

        (FileVaultStore destino, _) = NewVault("destino");
        var metadata = new FakeSyncMetadataStore();
        var orch = new SecretSyncOrchestrator(ServerWorkspace, VaultWorkspace, destino, api, metadata);

        SecretSyncReport report = await orch.SyncOnceAsync();

        Assert.Equal(2, (await destino.ListEnvelopesAsync(VaultWorkspace)).Count);
        Assert.Equal(3, metadata.SecretsCursor); // a página avançou apesar do item ruim

        SecretSyncSkip pulado = Assert.Single(report.Skipped);
        Assert.Equal(e2.EnvelopeId, pulado.EnvelopeId);
        Assert.Equal(SecretSyncPhase.Pull, pulado.Phase);
        Assert.Equal(nameof(CloudSyncException), pulado.ErrorType);
    }

    /// <summary>
    /// <b>O que NÃO pode virar "pulado".</b> Falha de ARMAZENAMENTO (disco cheio, processo morto) tem
    /// que continuar estourando e SEGURANDO o cursor: pular ali deixaria o envelope atrás do cursor,
    /// perdido em silêncio. Só falha de CONTRATO (o dto não vira envelope) é pulável.
    /// </summary>
    [Fact]
    public async Task FalhaDeDisco_NaoEhPulada_ESeguraOCursor()
    {
        (_, CredentialVault origem) = NewVault("origem-disco");
        SecretEnvelope e1 = await SealAsync(origem, "c1", "segredo-1");
        SecretEnvelope e2 = await SealAsync(origem, "c2", "segredo-2");

        var api = new PageSecretsApi(
        [
            SecretEnvelopeWireCodec.ToWire(e1, ServerWorkspace, 1),
            SecretEnvelopeWireCodec.ToWire(e2, ServerWorkspace, 1),
        ]);

        var inner = new FileVaultStore(Path.Combine(_dir, "disco", "vault.json"));
        var flaky = new FlakyEnvelopeStore(inner) { FailSaveAfter = 1 };
        var metadata = new FakeSyncMetadataStore();
        var orch = new SecretSyncOrchestrator(ServerWorkspace, VaultWorkspace, flaky, api, metadata);

        await Assert.ThrowsAsync<IOException>(() => orch.SyncOnceAsync());

        Assert.Equal(0, metadata.SecretsCursor);
    }

    /// <summary>
    /// <b>Guarda do escopo do catch por item.</b> Um 5xx não é envelope ruim — é o servidor no chão.
    /// Se o catch por item fosse largo demais (qualquer <see cref="CloudSyncException"/>), uma queda
    /// do backend viraria "N itens pulados": o canal reportaria "degradado" e seguiria em frente
    /// enquanto NADA sobe, escondendo uma falha total atrás de uma lista de itens sadios.
    /// </summary>
    [Fact]
    public async Task FalhaDeTransporteNoPush_NaoViraItemPulado()
    {
        (FileVaultStore store, CredentialVault vault) = NewVault("transporte");
        await SealAsync(vault, "c1", "segredo-1");

        var api = new PageSecretsApi([], pushErro: new CloudSyncException(HttpStatusCode.InternalServerError));
        var orch = new SecretSyncOrchestrator(
            ServerWorkspace, VaultWorkspace, store, api, new FakeSyncMetadataStore());

        await Assert.ThrowsAsync<CloudSyncException>(() => orch.SyncOnceAsync());
    }

    // ── Push caído não pode calar o pull ──────────────────────────────────────────────────

    /// <summary>
    /// O pull tem de rodar mesmo se o push falhar — senão o device B, que só RECEBE, para de receber
    /// por causa de um problema de SUBIDA. A falha do push ainda é relançada no fim (o
    /// <see cref="SyncOrchestrator"/> é quem decide a postura), só que depois de o pull ter feito o
    /// trabalho dele.
    /// </summary>
    [Fact]
    public async Task PullRodaMesmoQuandoOPushFalha()
    {
        (_, CredentialVault origem) = NewVault("origem-push-caido");
        SecretEnvelope e1 = await SealAsync(origem, "c1", "segredo-1");
        SecretEnvelope e2 = await SealAsync(origem, "c2", "segredo-2");

        var api = new PageSecretsApi(
            [
                SecretEnvelopeWireCodec.ToWire(e1, ServerWorkspace, 1),
                SecretEnvelopeWireCodec.ToWire(e2, ServerWorkspace, 1),
            ],
            pushErro: new HttpRequestException("servidor de segredos fora (simulado)"));

        (FileVaultStore destino, CredentialVault destinoVault) = NewVault("destino-push-caido");
        await SealAsync(destinoVault, "c-local", "segredo-local"); // dá o que empurrar

        var orch = new SecretSyncOrchestrator(
            ServerWorkspace, VaultWorkspace, destino, api, new FakeSyncMetadataStore());

        await Assert.ThrowsAsync<HttpRequestException>(() => orch.SyncOnceAsync());

        // 1 local + 2 baixados: o pull rodou mesmo com o push no chão.
        Assert.Equal(3, (await destino.ListEnvelopesAsync(VaultWorkspace)).Count);
    }

    // ── Apoio ────────────────────────────────────────────────────────────────────────────

    private (FileVaultStore Store, CredentialVault Vault) NewVault(string name)
    {
        var store = new FileVaultStore(Path.Combine(_dir, name, "vault.json"));
        return (store, new CredentialVault(store, _keyRing, NullVaultAuditSink.Instance));
    }

    private static Task<SecretEnvelope> SealAsync(CredentialVault vault, string credentialId, string secret) =>
        vault.StoreAsync(
            new VaultStoreRequest
            {
                WorkspaceId = VaultWorkspace,
                CredentialId = credentialId,
                Type = "password",
                ActorUserId = "op",
            },
            secret.AsMemory());

    /// <summary>
    /// Servidor de segredos com uma página FIXA (dtos montados à mão, inclusive os malformados que o
    /// <see cref="FakeSecretsApi"/> recusaria na entrada) e, opcionalmente, um push que estoura.
    /// </summary>
    private sealed class PageSecretsApi(IReadOnlyList<SecretEnvelopeDto> page, Exception? pushErro = null)
        : ISecretsApi
    {
        /// <summary>Ids que chegaram no push, na ordem — mede quem passou apesar do veneno.</summary>
        public List<string> Pushed { get; } = [];

        public Task<IReadOnlyList<SecretUpsertResult>> PushAsync(
            string workspaceId, IReadOnlyList<SecretEnvelopeDto> envelopes, CancellationToken ct = default)
        {
            if (pushErro is not null)
            {
                throw pushErro;
            }

            var results = new List<SecretUpsertResult>(envelopes.Count);
            foreach (SecretEnvelopeDto dto in envelopes)
            {
                Pushed.Add(dto.Id);
                results.Add(new SecretUpsertResult("ok", Pushed.Count, dto.Version));
            }

            return Task.FromResult<IReadOnlyList<SecretUpsertResult>>(results);
        }

        public Task<SecretsPullResponse> PullAsync(
            string workspaceId, long since, int pageSize, CancellationToken ct = default)
        {
            IReadOnlyList<SecretEnvelopeDto> pendentes = since >= page.Count
                ? []
                : page.Skip((int)since).ToList();

            return Task.FromResult(new SecretsPullResponse(pendentes, page.Count, false));
        }
    }
}
