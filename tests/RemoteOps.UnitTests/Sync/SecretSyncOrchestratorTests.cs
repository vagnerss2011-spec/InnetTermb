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
    /// Conflito RETENTÁVEL não pode ser marcado como enviado. <c>cursor.race-retry</c> é o servidor
    /// dizendo "corrida no cursor, manda de novo" — marcar no ledger aqui esconderia pra sempre um
    /// envelope que NUNCA subiu: o segredo simplesmente não existiria no device B, em silêncio.
    /// </summary>
    [Fact]
    public async Task ConflitoRetentavel_NaoMarcaComoEnviado_ETentaDeNovo()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();
        api.ForcedUpsertResults.Enqueue(new SecretUpsertResult("conflict", 0, null, "cursor.race-retry"));

        using var deviceA = new SecretSyncDevice("A", Amk, api, ws);
        await deviceA.SealAsync("c1", "segredo-1");

        await deviceA.Secrets.SyncOnceAsync();
        Assert.Empty(api.Accepted); // a corrida barrou

        // Ciclo seguinte: sem a marca no ledger, ele TEM que tentar de novo — e aí entra.
        await deviceA.Secrets.SyncOnceAsync();
        Assert.Single(api.Accepted);
    }

    /// <summary>
    /// <c>envelope.workspace-mismatch</c> (o id já existe noutro workspace) é anomalia real: também
    /// não marca. Marcar declararia "está no servidor" para um envelope que o servidor recusou.
    /// </summary>
    [Fact]
    public async Task ConflitoDeWorkspace_NaoMarcaComoEnviado()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();
        api.ForcedUpsertResults.Enqueue(
            new SecretUpsertResult("conflict", 0, null, "envelope.workspace-mismatch"));

        using var deviceA = new SecretSyncDevice("A", Amk, api, ws);
        await deviceA.SealAsync("c1", "segredo-1");

        await deviceA.Secrets.SyncOnceAsync();
        await deviceA.Secrets.SyncOnceAsync();

        // Duas tentativas: a recusa não vira "enviado" em silêncio.
        Assert.Equal(2, api.UpsertAttempts.Count);
    }

    /// <summary>
    /// <c>version.conflict</c> é o oposto: o servidor JÁ tem esta versão (ou mais nova). Aí marcar é
    /// o certo — insistir seria um POST inútil por ciclo, pra sempre.
    /// </summary>
    [Fact]
    public async Task ConflitoDeVersao_MarcaComoEnviado_ENaoInsiste()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();
        api.ForcedUpsertResults.Enqueue(new SecretUpsertResult("conflict", 3, 9, "version.conflict"));

        using var deviceA = new SecretSyncDevice("A", Amk, api, ws);
        await deviceA.SealAsync("c1", "segredo-1");

        await deviceA.Secrets.SyncOnceAsync();
        await deviceA.Secrets.SyncOnceAsync();

        Assert.Single(api.UpsertAttempts); // não insistiu
    }

    /// <summary>
    /// O fake de API é a rede de segurança da opacidade — se ELE não morder, nenhum teste de E2EE
    /// nesta suíte vale. Prova que um plaintext no fio realmente estoura.
    /// </summary>
    [Fact]
    public async Task GuardaDeOpacidade_NaoEhVacua_EstouraSePlaintextVazar()
    {
        var api = new FakeSecretsApi();
        api.Forbid("senha-em-claro");

        SecretEnvelopeDto vazando = new(
            Id: Guid.NewGuid().ToString(),
            WorkspaceId: Guid.NewGuid().ToString(),
            Ciphertext: Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("senha-em-claro")),
            Nonce: Convert.ToBase64String(new byte[12]),
            Tag: Convert.ToBase64String(new byte[16]),
            WrappedCek: Convert.ToBase64String(new byte[32]),
            CekNonce: Convert.ToBase64String(new byte[12]),
            CekTag: Convert.ToBase64String(new byte[16]),
            KeyVersion: "1|password|c1",
            Version: 1);

        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(
            () => api.PushAsync(NewServerWorkspace(), [vazando]));
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

    /// <summary>
    /// <b>Queda GRAVANDO a página</b> (disco cheio, processo morto) — o caso que decide a ORDEM
    /// entre gravar o envelope e avançar o cursor.
    ///
    /// <para>Gravando primeiro: a falha estoura ANTES do cursor avançar, o ciclo seguinte re-baixa a
    /// página inteira (idempotente) e nada se perde. Avançando o cursor primeiro, os envelopes da
    /// página ficariam ATRÁS do cursor e nunca mais seriam baixados — segredo perdido em silêncio,
    /// que é o pior desfecho possível aqui.</para>
    /// </summary>
    [Fact]
    public async Task QuedaGravandoAPagina_NaoAvancaOCursor_ENaoPerdeEnvelope()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();

        using var deviceA = new SecretSyncDevice("A", Amk, api, ws);
        await deviceA.SealAsync("c1", "segredo-1");
        await deviceA.SealAsync("c2", "segredo-2");
        await deviceA.Secrets.SyncOnceAsync();

        string dir = Path.Combine(Path.GetTempPath(), $"remoteops-flaky-{Guid.NewGuid():n}");
        try
        {
            var inner = new FileVaultStore(Path.Combine(dir, "vault.json"));
            var flaky = new FlakyEnvelopeStore(inner);
            using var keyRing = new AmkWorkspaceKeyRing(Amk);
            var vault = new CredentialVault(flaky, keyRing, NullVaultAuditSink.Instance);
            var metadata = new FakeSyncMetadataStore();
            var orchestrator = new SecretSyncOrchestrator(
                ws, SecretSyncDevice.VaultWorkspaceId, flaky, api, metadata);

            // A 2ª gravação da página estoura.
            flaky.FailSaveAfter = 1;
            await Assert.ThrowsAsync<IOException>(() => orchestrator.SyncOnceAsync());

            // O cursor NÃO pode ter avançado: a página não foi aplicada inteira.
            Assert.Equal(0, metadata.SecretsCursor);

            // Disco volta: o ciclo seguinte re-baixa a página e completa. Nada se perdeu.
            flaky.FailSaveAfter = int.MaxValue;
            await orchestrator.SyncOnceAsync();

            Assert.Equal(2, (await inner.ListEnvelopesAsync(SecretSyncDevice.VaultWorkspaceId)).Count);
            foreach ((string cred, string secret) in new[] { ("c1", "segredo-1"), ("c2", "segredo-2") })
            {
                SecretEnvelope env = Assert.Single(
                    await inner.ListEnvelopesAsync(SecretSyncDevice.VaultWorkspaceId),
                    e => e.CredentialId == cred);
                using VaultSecret opened = await vault.RetrieveAsync(
                    env.EnvelopeId, new VaultAccessContext { ActorUserId = "op" });
                Assert.Equal(secret, opened.RevealString());
            }
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
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

    // ── Revogação ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>A falha de segurança que esta fatia fecha.</b> Ao trocar/revogar uma senha, o envelope
    /// antigo virava tombstone SÓ no PC onde a troca aconteceu. No outro device ele continuava vivo
    /// e DECIFRÁVEL no disco, para sempre — resíduo de senha velha em N máquinas a cada troca.
    ///
    /// <para>Aqui a revogação sobe, desce e MATA o segredo no device B: o teste não se contenta com
    /// a marca <c>RevokedAt</c>, ele exige que abrir o envelope lá deixe de funcionar.</para>
    /// </summary>
    [Fact]
    public async Task Revogacao_Propaga_EODeviceBDeixaDeDecifrar()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();

        using var deviceA = new SecretSyncDevice("A", Amk, api, ws);
        SecretEnvelope env = await deviceA.SealAsync("c1", "senha-velha");
        await deviceA.Secrets.SyncOnceAsync();

        using var deviceB = new SecretSyncDevice("B", Amk, api, ws);
        await deviceB.Secrets.SyncOnceAsync();
        using (VaultSecret antes = await deviceB.OpenAsync(env.EnvelopeId))
        {
            Assert.Equal("senha-velha", antes.RevealString()); // o B realmente tinha a senha
        }

        await deviceA.Vault.RevokeAsync(env.EnvelopeId, new VaultAccessContext { ActorUserId = "op" });
        await deviceA.Secrets.SyncOnceAsync();
        await deviceB.Secrets.SyncOnceAsync();

        SecretEnvelope noB = Assert.Single(await deviceB.ListAsync());
        Assert.NotNull(noB.RevokedAt);
        Assert.Empty(noB.Ciphertext);
        await Assert.ThrowsAsync<VaultException>(() => deviceB.OpenAsync(env.EnvelopeId));

        // E a lápide não vira churn: com ela no ledger, nenhum dos dois insiste no POST a cada
        // ciclo. Um tombstone que re-sobe para sempre seria o preço escondido desta correção.
        int tentativas = api.UpsertAttempts.Count;
        await deviceA.Secrets.SyncOnceAsync();
        await deviceB.Secrets.SyncOnceAsync();
        Assert.Equal(tentativas, api.UpsertAttempts.Count);
    }

    /// <summary>
    /// Envelope ainda enraizado em DPAPI não tem tombstone que valha: o vivo dele nunca subiu, então
    /// a revogação também não sobe. Subir criaria no servidor uma lápide de um envelope que nenhum
    /// outro device jamais teve — cursor queimado à toa.
    /// </summary>
    [Fact]
    public async Task RevogacaoDeEnvelopeDpapi_NaoSobe()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();

        string dir = Path.Combine(Path.GetTempPath(), $"remoteops-revdpapi-{Guid.NewGuid():n}");
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

            // Um envelope pré-migração já revogado: mesmo cofre, carimbo da raiz antiga.
            await store.SaveAsync(amkEnv with
            {
                EnvelopeId = Guid.NewGuid().ToString("n"),
                CredentialId = "c-legado",
                Algorithm = VaultAlgorithms.DpapiRootedV1,
                RevokedAt = DateTimeOffset.UtcNow,
                WrappedCek = [],
                CekNonce = [],
                CekTag = [],
                Ciphertext = [],
                Nonce = [],
                Tag = [],
            });

            var orchestrator = new SecretSyncOrchestrator(
                ws, SecretSyncDevice.VaultWorkspaceId, store, api, new FakeSyncMetadataStore());
            await orchestrator.SyncOnceAsync();

            Assert.Single(api.Accepted); // só o AMK-rooted vivo
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

    /// <summary>
    /// <b>O cenário que RESSUSCITARIA a senha.</b> O guarda de downgrade só protege
    /// <c>existing.Version &gt; incoming.Version</c> — e uma cópia viva pode chegar na MESMA versão
    /// do tombstone (cliente antigo que não fala revogação, réplica atrasada, servidor adulterado).
    /// Aplicá-la devolveria o material antigo ao disco e a senha revogada voltaria a abrir AQUI.
    ///
    /// <para>Revogação é terminal: uma vez lápide, nunca mais cópia viva.</para>
    /// </summary>
    [Fact]
    public async Task TombstoneLocal_NaoEhRessuscitado_PorCopiaVivaDeMesmaVersao()
    {
        string ws = NewServerWorkspace();
        var api = new FakeSecretsApi();

        using var device = new SecretSyncDevice("A", Amk, api, ws);
        SecretEnvelope vivo = await device.SealAsync("c1", "senha-velha");
        await device.Vault.RevokeAsync(vivo.EnvelopeId, new VaultAccessContext { ActorUserId = "op" });

        SecretEnvelope tombstone = Assert.Single(await device.ListAsync());
        Assert.NotNull(tombstone.RevokedAt);

        // A cópia VIVA do mesmo envelope, na MESMA versão da lápide, plantada direto no servidor.
        api.SeedRaw(SecretEnvelopeWireCodec.ToWire(vivo with { Version = tombstone.Version }, ws, 1));

        await device.Secrets.SyncOnceAsync();

        SecretEnvelope depois = Assert.Single(await device.ListAsync());
        Assert.NotNull(depois.RevokedAt);
        Assert.Empty(depois.Ciphertext);
        await Assert.ThrowsAsync<VaultException>(() => device.OpenAsync(vivo.EnvelopeId));
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
