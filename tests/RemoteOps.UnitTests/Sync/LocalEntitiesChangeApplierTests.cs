using System.Text.Json;

using Microsoft.Data.Sqlite;

using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Testes do applier sobre SQLCipher real: created/updated/deleted nas TABELAS REAIS, idempotência,
/// monotonicidade, patch parcial, canonização de id e ausência de re-emissão no outbox (sem eco).
///
/// <para><b>Nota de contrato:</b> até a Fase 1 estes testes afirmavam que o applier grava em
/// <c>local_entities</c> — uma tabela que ninguém lê. Eles passavam, e o device B mostrava a lista de
/// hosts vazia: os testes fixavam o BUG. Agora afirmam o que o operador enxerga.</para>
/// </summary>
public sealed class LocalEntitiesChangeApplierTests
{
    private static SyncChange Change(
        string entityType, string id, string op, int baseVersion, Dictionary<string, object?>? patch = null)
        => new()
        {
            EntityType = entityType,
            EntityId = id,
            Operation = op,
            BaseVersion = baseVersion,
            Patch = patch ?? [],
        };

    /// <summary>
    /// Passa o patch pelo JSON, como o fio faz. Sem isto o teste entregaria tipos CLR ao applier e
    /// não provaria nada sobre o JsonElement que ele recebe de verdade.
    /// </summary>
    private static Dictionary<string, object?> Wire(Dictionary<string, object?> patch) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(patch))!;

    private static async Task<(bool Exists, long Version, Dictionary<string, object?> Row)> ReadRowAsync(
        SyncTestContext ctx, string table, string id)
    {
        using SqliteConnection conn = await ctx.Workspace.OpenConnectionAsync();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);

        using SqliteDataReader r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
        {
            return (false, 0, []);
        }

        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < r.FieldCount; i++)
        {
            row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
        }

        return (true, Convert.ToInt64(row["version"]), row);
    }

    // ── Tabelas reais: os 4 tipos ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_Asset_Created_Materializa_Na_Tabela_assets()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-asset");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        await applier.ApplyAsync([Change("asset", "a1", "created", 0, Wire(new()
        {
            ["id"] = "a1",
            ["workspace_id"] = "ws-local",
            ["name"] = "host-1",
            ["vendor"] = "Huawei",
            ["device_role"] = "router",
            ["tags_json"] = "[\"x\"]",
        }))]);

        (bool exists, long version, Dictionary<string, object?> row) = await ReadRowAsync(ctx, "assets", "a1");
        Assert.True(exists);
        Assert.Equal(1, version);
        Assert.Equal("host-1", row["name"]);
        Assert.Equal("Huawei", row["vendor"]);
        Assert.Equal("router", row["device_role"]);
        Assert.Equal("[\"x\"]", row["tags_json"]);
    }

    [Fact]
    public async Task Apply_Endpoint_Created_Leva_Endereco_Credencial_E_Perfil()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-ep");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        await applier.ApplyAsync([Change("endpoint", "e1", "created", 0, Wire(new()
        {
            ["id"] = "e1",
            ["asset_id"] = "a1",
            ["protocol"] = "ssh",
            ["fqdn"] = "host.local",
            ["ipv4"] = "10.0.0.1",
            ["ipv6"] = "2001:db8::1",
            ["port"] = 2222,
            ["prefer_ipv6"] = false,
            ["credential_ref_id"] = "c1",
            ["profile_json"] = "{\"backspaceMode\":\"ctrl-h\"}",
        }))]);

        (bool exists, _, Dictionary<string, object?> row) = await ReadRowAsync(ctx, "endpoints", "e1");
        Assert.True(exists);
        Assert.Equal("a1", row["asset_id"]);
        Assert.Equal("host.local", row["fqdn"]);
        Assert.Equal("10.0.0.1", row["ipv4"]);
        Assert.Equal("2001:db8::1", row["ipv6"]);
        Assert.Equal(2222L, row["port"]);
        Assert.Equal(0L, row["prefer_ipv6"]); // bool do JSON vira 0/1 na coluna INTEGER
        Assert.Equal("c1", row["credential_ref_id"]);
        Assert.Equal("{\"backspaceMode\":\"ctrl-h\"}", row["profile_json"]);
    }

    [Fact]
    public async Task Apply_CredentialRef_Created_Leva_Metadata_E_Referencia_Do_Envelope()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-cred");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        await applier.ApplyAsync([Change("credential_ref", "c1", "created", 0, Wire(new()
        {
            ["id"] = "c1",
            ["name"] = "cred",
            ["type"] = "password",
            ["scope"] = "endpoint:e1",
            ["metadata_json"] = "{\"username\":\"admin\"}",
            ["secret_envelope_id"] = "env-1",
        }))]);

        (bool exists, _, Dictionary<string, object?> row) = await ReadRowAsync(ctx, "credential_refs", "c1");
        Assert.True(exists);
        Assert.Equal("password", row["type"]);
        Assert.Equal("endpoint:e1", row["scope"]);
        Assert.Equal("{\"username\":\"admin\"}", row["metadata_json"]);
        Assert.Equal("env-1", row["secret_envelope_id"]);
    }

    [Fact]
    public async Task Apply_AssetGroup_Created_Materializa_Na_Tabela_asset_groups()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-group");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        await applier.ApplyAsync([Change("asset_group", "g1", "created", 0, Wire(new()
        {
            ["id"] = "g1",
            ["workspace_id"] = "ws-local",
            ["name"] = "Backbone",
            ["parent_id"] = null,
        }))]);

        (bool exists, _, Dictionary<string, object?> row) = await ReadRowAsync(ctx, "asset_groups", "g1");
        Assert.True(exists);
        Assert.Equal("Backbone", row["name"]);
        Assert.Equal("ws-local", row["workspace_id"]);
        Assert.Null(row["parent_id"]);
    }

    // ── Versão: monotonicidade e idempotência ─────────────────────────────────────────────

    [Fact]
    public async Task Apply_Updated_Sobrescreve_A_Linha()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-update");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);
        await applier.ApplyAsync([Change("asset", "a1", "created", 0, Wire(new()
        {
            ["workspace_id"] = "ws-local",
            ["name"] = "nome-velho",
        }))]);

        await applier.ApplyAsync([Change("asset", "a1", "updated", 1, Wire(new()
        {
            ["workspace_id"] = "ws-local",
            ["name"] = "nome-novo",
        }))]);

        (bool exists, long version, Dictionary<string, object?> row) = await ReadRowAsync(ctx, "assets", "a1");
        Assert.True(exists);
        Assert.Equal(2, version);
        Assert.Equal("nome-novo", row["name"]);
    }

    [Fact]
    public async Task Apply_E_Idempotente_Para_A_Mesma_Mudanca()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-idem");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);
        SyncChange change = Change("asset", "a1", "created", 0, Wire(new()
        {
            ["workspace_id"] = "ws-local",
            ["name"] = "host-1",
        }));

        await applier.ApplyAsync([change]);
        await applier.ApplyAsync([change]);

        (bool exists, long version, Dictionary<string, object?> row) = await ReadRowAsync(ctx, "assets", "a1");
        Assert.True(exists);
        Assert.Equal(1, version);
        Assert.Equal("host-1", row["name"]);
    }

    [Fact]
    public async Task Apply_Nao_Regride_Para_Versao_Mais_Antiga()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-monotonic");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);
        await applier.ApplyAsync([Change("asset", "a1", "updated", 4, Wire(new()
        {
            ["workspace_id"] = "ws-local",
            ["name"] = "novo",
        }))]);

        await applier.ApplyAsync([Change("asset", "a1", "updated", 0, Wire(new()
        {
            ["workspace_id"] = "ws-local",
            ["name"] = "velho",
        }))]);

        (bool exists, long version, Dictionary<string, object?> row) = await ReadRowAsync(ctx, "assets", "a1");
        Assert.True(exists);
        Assert.Equal(5, version);
        Assert.Equal("novo", row["name"]); // a versão antiga não venceu
    }

    /// <summary>
    /// Patch PARCIAL (um rename manda só <c>{name}</c>) não pode zerar o resto da linha. É o caso do
    /// <c>RenameGroupAsync</c> — se o applier fizesse upsert de linha inteira, o rename apagaria o
    /// workspace do grupo e ele sumiria da lista.
    /// </summary>
    [Fact]
    public async Task Apply_Patch_Parcial_Nao_Zera_As_Outras_Colunas()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-partial");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);
        await applier.ApplyAsync([Change("asset", "a1", "created", 0, Wire(new()
        {
            ["workspace_id"] = "ws-local",
            ["name"] = "host-1",
            ["vendor"] = "Huawei",
            ["site"] = "POP Centro",
            ["tags_json"] = "[\"a\"]",
        }))]);

        // Só o nome muda.
        await applier.ApplyAsync([Change("asset", "a1", "updated", 1, Wire(new()
        {
            ["name"] = "host-2",
        }))]);

        (_, _, Dictionary<string, object?> row) = await ReadRowAsync(ctx, "assets", "a1");
        Assert.Equal("host-2", row["name"]);
        Assert.Equal("ws-local", row["workspace_id"]); // continua visível
        Assert.Equal("Huawei", row["vendor"]);
        Assert.Equal("POP Centro", row["site"]);
        Assert.Equal("[\"a\"]", row["tags_json"]);
    }

    /// <summary>
    /// O backend guarda o EntityId num <c>Guid</c> e o devolve em formato "D" (com hífens), enquanto
    /// os campos do patch são ecoados no "n" que o cliente escreveu. Sem canonizar, o
    /// <c>endpoint.asset_id</c> ("n") nunca acharia o <c>assets.id</c> ("D") — o host chegaria sem
    /// endereço. Esta é a asserção que fixa isso.
    /// </summary>
    [Fact]
    public async Task Apply_Canoniza_O_Id_Do_Servidor_Para_O_Formato_Do_Cliente()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-guid");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        Guid id = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();

        // O servidor manda "D"; o patch (ecoado verbatim do device A) traz "n".
        await applier.ApplyAsync([Change("endpoint", id.ToString(), "created", 0, Wire(new()
        {
            ["asset_id"] = assetId.ToString("n"),
            ["protocol"] = "ssh",
        }))]);

        (bool exists, _, Dictionary<string, object?> row) = await ReadRowAsync(ctx, "endpoints", id.ToString("n"));
        Assert.True(exists); // achou pelo "n" — não ficou gravado como "D"
        Assert.Equal(assetId.ToString("n"), row["asset_id"]);
    }

    /// <summary>Um FK em "D" também é canonizado — senão o link quebra do outro lado.</summary>
    [Fact]
    public async Task Apply_Canoniza_Tambem_As_Chaves_Estrangeiras()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-fk");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        Guid assetId = Guid.NewGuid();
        Guid credId = Guid.NewGuid();

        await applier.ApplyAsync([Change("endpoint", "e1", "created", 0, Wire(new()
        {
            ["asset_id"] = assetId.ToString(),      // "D"
            ["protocol"] = "ssh",
            ["credential_ref_id"] = credId.ToString(), // "D"
        }))]);

        (_, _, Dictionary<string, object?> row) = await ReadRowAsync(ctx, "endpoints", "e1");
        Assert.Equal(assetId.ToString("n"), row["asset_id"]);
        Assert.Equal(credId.ToString("n"), row["credential_ref_id"]);
    }

    // ── Delete ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_Deleted_Remove_A_Linha()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-delete");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);
        await applier.ApplyAsync([Change("asset", "a1", "created", 0, Wire(new()
        {
            ["workspace_id"] = "ws-local",
            ["name"] = "host-1",
        }))]);

        await applier.ApplyAsync([Change("asset", "a1", "deleted", 1)]);

        (bool exists, _, _) = await ReadRowAsync(ctx, "assets", "a1");
        Assert.False(exists);
    }

    /// <summary>
    /// Apagar o ativo cascateia nos endpoints — o device A não empurra o delete de cada endpoint
    /// (ele também cascateia), então quem recebe TEM que cascatear, senão sobram endpoints órfãos.
    /// </summary>
    [Fact]
    public async Task Apply_Deleted_De_Asset_Cascateia_Nos_Endpoints()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-cascade");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);
        await applier.ApplyAsync([
            Change("asset", "a1", "created", 0, Wire(new()
            {
                ["workspace_id"] = "ws-local",
                ["name"] = "host-1",
            })),
            Change("endpoint", "e1", "created", 0, Wire(new()
            {
                ["asset_id"] = "a1",
                ["protocol"] = "ssh",
            })),
        ]);

        await applier.ApplyAsync([Change("asset", "a1", "deleted", 1)]);

        (bool assetExists, _, _) = await ReadRowAsync(ctx, "assets", "a1");
        (bool epExists, _, _) = await ReadRowAsync(ctx, "endpoints", "e1");
        Assert.False(assetExists);
        Assert.False(epExists);
    }

    // ── Invariantes: sem eco, sem segredo ─────────────────────────────────────────────────

    [Fact]
    public async Task Apply_Nao_Emite_No_Outbox()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-noecho");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        await applier.ApplyAsync([Change("asset", "a1", "created", 0, Wire(new()
        {
            ["workspace_id"] = "ws-local",
            ["name"] = "host-1",
        }))]);

        IReadOnlyList<SyncChange> outbox = await ctx.Client.PullAsync(0);
        Assert.Empty(outbox);
    }

    [Fact]
    public async Task Apply_Ignora_SecretEnvelope()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-secret");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        // SecretEnvelope NUNCA é aplicado/mesclado no cliente (CLAUDE.md / ADR-003). Mesmo misturado
        // com mudanças normais, é ignorado; o asset legítimo é aplicado.
        var secret = new SyncChange
        {
            EntityType = "SecretEnvelope",
            EntityId = "se1",
            Operation = "updated",
            BaseVersion = 3,
            Patch = new Dictionary<string, object?> { ["keyVersion"] = 2 },
        };

        await applier.ApplyAsync([secret, Change("asset", "a1", "created", 0, Wire(new()
        {
            ["workspace_id"] = "ws-local",
            ["name"] = "host-1",
        }))]);

        Assert.False(await QuarantineExistsAsync(ctx, "SecretEnvelope", "se1"));
        (bool assetExists, _, _) = await ReadRowAsync(ctx, "assets", "a1");
        Assert.True(assetExists);
    }

    [Fact]
    public async Task Apply_Ignora_Delete_De_SecretEnvelope()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-secret-del");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        var del = new SyncChange
        {
            EntityType = "SecretEnvelope",
            EntityId = "se1",
            Operation = "deleted",
            BaseVersion = 0,
            Patch = [],
        };

        // Não lança e não toca em nada (segregado antes do DELETE).
        await applier.ApplyAsync([del]);

        Assert.False(await QuarantineExistsAsync(ctx, "SecretEnvelope", "se1"));
    }

    // ── Quarentena: tipos que este app ainda não entende ──────────────────────────────────

    /// <summary>
    /// Tipo desconhecido não pode ser descartado: o cursor avança de qualquer jeito, então descartar
    /// perderia a mudança PRA SEMPRE se uma versão futura passar a entender o tipo.
    /// </summary>
    [Fact]
    public async Task Apply_Tipo_Desconhecido_Vai_Pra_Quarentena()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-unknown");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        await applier.ApplyAsync([Change("coisa_do_futuro", "x1", "created", 0, Wire(new()
        {
            ["campo"] = "valor",
        }))]);

        Assert.True(await QuarantineExistsAsync(ctx, "coisa_do_futuro", "x1"));
    }

    [Fact]
    public async Task Apply_Tipos_Conhecidos_Nao_Sujam_A_Quarentena()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-noquar");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        await applier.ApplyAsync([Change("asset", "a1", "created", 0, Wire(new()
        {
            ["workspace_id"] = "ws-local",
            ["name"] = "host-1",
        }))]);

        Assert.False(await QuarantineExistsAsync(ctx, "asset", "a1"));
    }

    private static async Task<bool> QuarantineExistsAsync(
        SyncTestContext ctx, string entityType, string entityId)
    {
        using SqliteConnection conn = await ctx.Workspace.OpenConnectionAsync();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM local_entities WHERE entity_type = $et AND entity_id = $eid";
        cmd.Parameters.AddWithValue("$et", entityType);
        cmd.Parameters.AddWithValue("$eid", entityId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }
}
