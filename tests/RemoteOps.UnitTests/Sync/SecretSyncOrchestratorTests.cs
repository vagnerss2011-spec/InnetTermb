using System.IO;
using System.Linq;
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
/// Disciplina do ciclo de segredos: cursor monotônico, push idempotente, retomada após queda e
/// isolamento do que é LOCAL-ONLY. Complementa o <see cref="DeviceToDeviceSecretSyncTests"/>, que
/// prova a cripto ponta a ponta; aqui a lupa é no transporte.
/// </summary>
public sealed class SecretSyncOrchestratorTests
{
    private static readonly byte[] Amk = RandomNumberGenerator.GetBytes(32);
    private static string NewServerWorkspace() => Guid.NewGuid().ToString();

    // ── Cursor ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O cursor de segredos avança e o que já foi aplicado não volta: o segundo ciclo não reprocessa
    /// nada. Sem isso, cada sync reescreveria o cofre inteiro por cima.
    /// </summary>
    [Fact]
    public async Task CursorAvanca_ESegundoCicloNaoReprocessa()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();

        using var deviceA = new SecretSyncDevice("A", Amk, api, ws);
        await deviceA.SealAsync("c1", "segredo-1");
        await deviceA.SealAsync("c2", "segredo-2");
        await deviceA.Secrets.SyncOnceAsync();

        using var deviceB = new SecretSyncDevice("B", Amk, api, ws);
        await deviceB.Secrets.SyncOnceAsync();

        long cursorDepoisDoPrimeiro = deviceB.Metadata.SecretsCursor;
        Assert.True(cursorDepoisDoPrimeiro > 0, "o cursor tem que avançar depois de baixar envelopes");
        Assert.Equal(2, (await deviceB.ListAsync()).Count);

        int pullsAntes = api.PullCalls;
        await deviceB.Secrets.SyncOnceAsync();

        // Cursor parado (nada novo no servidor) e nenhum envelope duplicado.
        Assert.Equal(cursorDepoisDoPrimeiro, deviceB.Metadata.SecretsCursor);
        Assert.Equal(2, (await deviceB.ListAsync()).Count);
        Assert.True(api.PullCalls > pullsAntes, "o ciclo seguinte ainda consulta o servidor");
    }

    /// <summary>O cursor nunca regride: um save tardio com valor menor não desfaz o progresso.</summary>
    [Fact]
    public async Task CursorNaoRegride()
    {
        string ws = NewServerWorkspace();
        var metadata = new FakeSyncMetadataStore();

        await metadata.SaveSecretsCursorAsync(ws, 10);
        await metadata.SaveSecretsCursorAsync(ws, 4);

        Assert.Equal(10, await metadata.GetSecretsCursorAsync(ws));
    }

    // ── Push idempotente ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// O MESMO envelope não sobe duas vezes. O ledger local (<c>secrets_pushed</c>) evita o POST
    /// repetido; e mesmo se subisse, o servidor recusaria por versão — a asserção cobre os dois.
    /// </summary>
    [Fact]
    public async Task PushDoMesmoEnvelopeDuasVezes_NaoDuplica()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();

        using var deviceA = new SecretSyncDevice("A", Amk, api, ws);
        await deviceA.SealAsync("c1", "segredo-1");

        await deviceA.Secrets.SyncOnceAsync();
        await deviceA.Secrets.SyncOnceAsync();
        await deviceA.Secrets.SyncOnceAsync();

        Assert.Single(api.Accepted);
        // E nem tentou: o ledger poupa o round-trip inútil a cada ciclo.
        Assert.Single(api.UpsertAttempts);
    }

    /// <summary>
    /// O device NÃO devolve como push o que acabou de PUXAR. Sem isso, B re-subiria tudo que baixou,
    /// queimando cursor no servidor, o que faria A re-baixar, que re-subiria... — churn infinito
    /// entre dois devices ociosos.
    /// </summary>
    [Fact]
    public async Task OQueFoiPuxado_NaoVoltaComoEco()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();

        using var deviceA = new SecretSyncDevice("A", Amk, api, ws);
        await deviceA.SealAsync("c1", "segredo-1");
        await deviceA.Secrets.SyncOnceAsync();

        using var deviceB = new SecretSyncDevice("B", Amk, api, ws);
        await deviceB.Secrets.SyncOnceAsync();
        int tentativasDepoisDoPull = api.UpsertAttempts.Count;

        await deviceB.Secrets.SyncOnceAsync();
        await deviceB.Secrets.SyncOnceAsync();

        Assert.Equal(tentativasDepoisDoPull, api.UpsertAttempts.Count);
        Assert.Single(api.Accepted);
    }

    // ── Retomada ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Queda no meio do pull: o ciclo estoura, mas nada corrompe e nada some. O ciclo seguinte
    /// retoma do cursor gravado e o cofre fica íntegro. Envelope é gravado ANTES do cursor avançar —
    /// a ordem inversa perderia o envelope numa queda.
    /// </summary>
    [Fact]
    public async Task QuedaNoMeioDoPull_RetomaSemPerderNemDuplicar()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();

        using var deviceA = new SecretSyncDevice("A", Amk, api, ws);
        for (int i = 1; i <= 5; i++)
        {
            await deviceA.SealAsync($"c{i}", $"segredo-{i}");
        }

        await deviceA.Secrets.SyncOnceAsync();

        // Página de 2: o B baixa 2, e a rede cai no pull seguinte (cursor 2).
        using var deviceB = new SecretSyncDevice("B", Amk, api, ws, pageSize: 2);
        api.FailPullOnCursor = 2;

        await Assert.ThrowsAsync<HttpRequestException>(() => deviceB.Secrets.SyncOnceAsync());

        // O que já tinha chegado ficou; o cursor parou onde deu.
        Assert.Equal(2, (await deviceB.ListAsync()).Count);
        Assert.Equal(2, deviceB.Metadata.SecretsCursor);

        // Rede volta: retoma e completa, sem duplicar.
        api.FailPullOnCursor = null;
        await deviceB.Secrets.SyncOnceAsync();

        Assert.Equal(5, (await deviceB.ListAsync()).Count);
        for (int i = 1; i <= 5; i++)
        {
            SecretEnvelope env = Assert.Single(await deviceB.ListAsync(), e => e.CredentialId == $"c{i}");
            using VaultSecret opened = await deviceB.OpenAsync(env.EnvelopeId);
            Assert.Equal($"segredo-{i}", opened.RevealString());
        }
    }

    /// <summary>Paginação: mais de uma página é seguida até <c>hasMore == false</c>.</summary>
    [Fact]
    public async Task PullPaginado_BaixaTodasAsPaginas()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();

        using var deviceA = new SecretSyncDevice("A", Amk, api, ws);
        for (int i = 1; i <= 7; i++)
        {
            await deviceA.SealAsync($"c{i}", $"segredo-{i}");
        }

        await deviceA.Secrets.SyncOnceAsync();

        using var deviceB = new SecretSyncDevice("B", Amk, api, ws, pageSize: 3);
        await deviceB.Secrets.SyncOnceAsync();

        Assert.Equal(7, (await deviceB.ListAsync()).Count);
    }

    // ── O que NÃO pode subir ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Guarda crítica.</b> A chave do banco SQLCipher e os tokens de sessão TAMBÉM moram no cofre
    /// (<c>VaultDbKeyProvider</c> e <c>VaultTokenStore</c>), em OUTROS workspaces do cofre ("local" e
    /// o GUID do servidor). O transporte é escopado ao workspace das credenciais — se algum dia
    /// alguém alargar o escopo, este teste morde: subir a chave do banco pro servidor seria um
    /// incidente de segurança.
    /// </summary>
    [Fact]
    public async Task SegredosDeOutrosWorkspacesDoCofre_NuncaSobem()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();
        api.Forbid("chave-do-banco-sqlcipher");
        api.Forbid("refresh-token-secreto");

        string dir = Path.Combine(Path.GetTempPath(), $"remoteops-scope-{Guid.NewGuid():n}");
        try
        {
            var store = new FileVaultStore(Path.Combine(dir, "vault.json"));
            using var keyRing = new AmkWorkspaceKeyRing(Amk);
            var vault = new CredentialVault(store, keyRing, NullVaultAuditSink.Instance);

            // Credencial do operador: DEVE subir.
            await vault.StoreAsync(
                new VaultStoreRequest
                {
                    WorkspaceId = SecretSyncDevice.VaultWorkspaceId,
                    CredentialId = "c1",
                    Type = "password",
                    ActorUserId = "op",
                },
                "senha-do-host".AsMemory());

            // Chave do banco (workspace "local") e tokens (workspace = GUID do servidor): NÃO sobem.
            await vault.StoreAsync(
                new VaultStoreRequest { WorkspaceId = "local", CredentialId = "db", ActorUserId = "system" },
                "chave-do-banco-sqlcipher".AsMemory());
            await vault.StoreAsync(
                new VaultStoreRequest { WorkspaceId = ws, CredentialId = "tk", ActorUserId = "system" },
                "refresh-token-secreto".AsMemory());

            var metadata = new FakeSyncMetadataStore();
            var orchestrator = new SecretSyncOrchestrator(
                ws, SecretSyncDevice.VaultWorkspaceId, store, api, metadata);

            await orchestrator.SyncOnceAsync();

            // Só a credencial do operador subiu.
            Assert.Single(api.Accepted);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Envelope revogado (tombstone) não sobe. Não é preferência: o tombstone zera o material, e o
    /// backend real recusa base64 vazio — subir daria erro de servidor a cada ciclo, pra sempre.
    /// </summary>
    [Fact]
    public async Task EnvelopeRevogado_NaoSobe()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();

        using var deviceA = new SecretSyncDevice("A", Amk, api, ws);
        SecretEnvelope env = await deviceA.SealAsync("c1", "segredo-1");
        await deviceA.Vault.RevokeAsync(env.EnvelopeId, new VaultAccessContext { ActorUserId = "op" });

        await deviceA.Secrets.SyncOnceAsync();

        Assert.Empty(api.Accepted);
        Assert.Empty(api.UpsertAttempts);
    }

    /// <summary>
    /// Envelope ainda enraizado em DPAPI não sobe: a WDK dele é aleatória por MÁQUINA, então nenhum
    /// outro device abriria — subir só publicaria lixo indecifrável. Só sobe o que está sob a AMK.
    /// </summary>
    [Fact]
    public async Task EnvelopeAindaEnraizadoEmDpapi_NaoSobe()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();

        string dir = Path.Combine(Path.GetTempPath(), $"remoteops-dpapi-{Guid.NewGuid():n}");
        try
        {
            var store = new FileVaultStore(Path.Combine(dir, "vault.json"));
            using var keyRing = new AmkWorkspaceKeyRing(Amk);
            var vault = new CredentialVault(store, keyRing, NullVaultAuditSink.Instance);

            SecretEnvelope amkEnv = await vault.StoreAsync(
                new VaultStoreRequest
                {
                    WorkspaceId = SecretSyncDevice.VaultWorkspaceId,
                    CredentialId = "c1",
                    Type = "password",
                    ActorUserId = "op",
                },
                "segredo".AsMemory());

            // Simula um envelope pré-migração: mesmo cofre, Algorithm da raiz antiga.
            await store.SaveAsync(amkEnv with
            {
                EnvelopeId = Guid.NewGuid().ToString("n"),
                CredentialId = "c-legado",
                Algorithm = VaultAlgorithms.DpapiRootedV1,
            });

            var orchestrator = new SecretSyncOrchestrator(
                ws, SecretSyncDevice.VaultWorkspaceId, store, api, new FakeSyncMetadataStore());
            await orchestrator.SyncOnceAsync();

            // Só o AMK-rooted subiu.
            Assert.Single(api.Accepted);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ── Sem downgrade ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Um envelope local mais NOVO não é sobrescrito por uma versão velha vinda do servidor.
    /// Monotonicidade por id, igual à do applier de metadados.
    /// </summary>
    [Fact]
    public async Task VersaoVelhaDoServidor_NaoSobrescreveEnvelopeLocalMaisNovo()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();

        using var deviceA = new SecretSyncDevice("A", Amk, api, ws);
        SecretEnvelope v1 = await deviceA.SealAsync("c1", "valor-v1");
        await deviceA.Secrets.SyncOnceAsync();

        // Device B baixa a v1 e, localmente, passa a ter uma v2 do MESMO id.
        using var deviceB = new SecretSyncDevice("B", Amk, api, ws);
        await deviceB.Secrets.SyncOnceAsync();

        SecretEnvelope local = Assert.Single(await deviceB.ListAsync());
        await deviceB.Store.SaveAsync(local with { Version = 2 });

        // Um novo pull do zero traz a v1 do servidor — que não pode vencer a v2 local.
        await deviceB.Metadata.ResetSecretsCursorAsync(ws);
        await deviceB.Secrets.SyncOnceAsync();

        SecretEnvelope depois = Assert.Single(await deviceB.ListAsync());
        Assert.Equal(2, depois.Version);
        Assert.Equal(v1.EnvelopeId, depois.EnvelopeId);
    }
}
