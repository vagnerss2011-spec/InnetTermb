using System.Text.Json;

using Microsoft.Data.Sqlite;

using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Storage;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Implementação canônica de <see cref="IRemoteChangeApplier"/>: materializa as mudanças puxadas do
/// servidor nas <b>tabelas REAIS</b> do banco SQLCipher (<c>asset_groups</c>, <c>assets</c>,
/// <c>endpoints</c>, <c>credential_refs</c> — ver <see cref="LocalSchema"/>), de forma idempotente e
/// monotônica, e SEM gravar no outbox (não usa <c>ISyncClient.PushAsync</c>), evitando loop de eco.
/// Ver ADR-013.
///
/// <para><b>Por que "materializar" e não guardar o patch:</b> até a Fase 1 este applier gravava tudo
/// numa tabela genérica <c>local_entities</c>, que NINGUÉM lê — a UI lê as tabelas reais pelo
/// <c>SqlCipherLocalStore</c>. O changelog chegava, o cursor avançava, e o device B mostrava a lista
/// de hosts VAZIA. Era o último bloqueador da fase.</para>
///
/// <para><b>Patches são PARCIAIS por contrato</b> (um rename manda só <c>{name}</c>). Por isso o
/// upsert só toca as colunas presentes no patch: um patch parcial NUNCA zera o resto da linha. As
/// colunas gravadas saem de uma allowlist por tipo (<see cref="Tables"/>) — o nome de coluna nunca
/// vem do patch, então um servidor comprometido não escolhe onde escrever.</para>
///
/// <para><b>SecretEnvelope continua RECUSADO</b> (ADR-003): o segredo viaja pelo canal
/// <c>/secrets</c> (<see cref="SecretSyncOrchestrator"/>), nunca pelo changelog. O
/// <c>secret_envelope_id</c> que aparece em <c>credential_ref</c> é só uma REFERÊNCIA — e é
/// justamente ela que liga o host à senha no outro device.</para>
///
/// <para><b>Tipo desconhecido cai em <c>local_entities</c></b> (quarentena, não cache): o cursor
/// avança de qualquer jeito, então descartar significaria perder a mudança PRA SEMPRE se uma versão
/// futura do app passar a entender o tipo. É a única razão de a tabela continuar existindo — nenhum
/// dos 4 tipos conhecidos escreve nela.</para>
/// </summary>
public sealed class LocalEntitiesChangeApplier : IRemoteChangeApplier
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// O mapa tipo-do-changelog → tabela real. As colunas são o contrato do patch: o
    /// <c>SqlCipherLocalStore</c> emite as chaves com o MESMO nome da coluna, de propósito — assim o
    /// applier é burro e a tradução não tem onde divergir.
    /// </summary>
    private static readonly Dictionary<string, TableMap> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["asset_group"] = new(
            Table: "asset_groups",
            Columns: ["workspace_id", "parent_id", "name", "default_credential_ref_id"],
            IdColumns: ["parent_id", "default_credential_ref_id"],
            RequiredDefaults: new() { ["workspace_id"] = "", ["name"] = "" }),

        ["asset"] = new(
            Table: "assets",
            Columns: ["workspace_id", "group_id", "name", "vendor", "model", "device_role", "site", "tags_json"],
            IdColumns: ["group_id"],
            RequiredDefaults: new() { ["workspace_id"] = "", ["name"] = "" }),

        ["endpoint"] = new(
            Table: "endpoints",
            Columns: ["asset_id", "protocol", "fqdn", "ipv4", "ipv6", "port", "prefer_ipv6",
                      "credential_ref_id", "profile_json"],
            IdColumns: ["asset_id", "credential_ref_id"],
            RequiredDefaults: new() { ["asset_id"] = "", ["protocol"] = "" }),

        ["credential_ref"] = new(
            Table: "credential_refs",
            Columns: ["name", "type", "scope", "metadata_json", "secret_envelope_id"],
            IdColumns: ["secret_envelope_id"],
            RequiredDefaults: new() { ["name"] = "", ["type"] = "" }),
    };

    private readonly WorkspaceContext _workspace;

    public LocalEntitiesChangeApplier(WorkspaceContext workspace)
    {
        _workspace = workspace;
    }

    public async Task ApplyAsync(IReadOnlyList<SyncChange> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0)
        {
            return;
        }

        using SqliteConnection conn = await _workspace.OpenConnectionAsync(ct);
        await LocalSchema.EnsureAsync(conn, ct);
        await EnsureQuarantineAsync(conn, ct);

        foreach (SyncChange change in changes)
        {
            ct.ThrowIfCancellationRequested();

            // SecretEnvelope NUNCA é aplicado/mesclado no cliente (CLAUDE.md / ADR-003): o cofre é a
            // autoridade do segredo. O servidor real também o recusa no push — se um aparecer no
            // changelog, é anomalia, e ignorar é a postura certa (o canal /secrets é o caminho).
            if (string.Equals(change.EntityType, "SecretEnvelope", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool known = Tables.TryGetValue(change.EntityType, out TableMap? map);
            bool deleted = string.Equals(change.Operation, "deleted", StringComparison.Ordinal);

            if (known)
            {
                if (deleted)
                {
                    await DeleteAsync(conn, map!, change, ct);
                }
                else
                {
                    await UpsertAsync(conn, map!, change, ct);
                }
            }
            else if (deleted)
            {
                await QuarantineDeleteAsync(conn, change, ct);
            }
            else
            {
                await QuarantineUpsertAsync(conn, change, ct);
            }
        }
    }

    // ── Tabelas reais ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// UPSERT monotônico e PARCIAL na tabela real.
    ///
    /// <para><b>Monotônico:</b> só sobrescreve quando a versão recebida é >= a local (<c>WHERE
    /// excluded.version >= t.version</c>). Re-aplicar a mesma versão é no-op; versão mais antiga é
    /// ignorada (sem downgrade).</para>
    ///
    /// <para><b>Parcial:</b> o <c>DO UPDATE SET</c> lista só as colunas do patch. Os defaults abaixo
    /// entram apenas no INSERT (a linha nova precisa satisfazer os NOT NULL) e por isso NÃO aparecem
    /// no SET — senão um patch de rename zeraria o workspace da linha existente.</para>
    /// </summary>
    private static async Task UpsertAsync(
        SqliteConnection conn, TableMap map, SyncChange change, CancellationToken ct)
    {
        string id = NormalizeId(change.EntityId);
        int version = change.BaseVersion + 1;

        // Só as colunas que o patch realmente trouxe (allowlist ∩ patch). "id" e "version" nunca vêm
        // do patch: a identidade e a versão são do SERVIDOR (change.EntityId/BaseVersion), e o "id"
        // ecoado no patch está no formato do cliente que empurrou — usá-lo divergiria do PK.
        var patchColumns = new List<string>();
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (string column in map.Columns)
        {
            if (!TryGetPatchValue(change.Patch, column, out object? raw))
            {
                continue;
            }

            patchColumns.Add(column);
            values[column] = map.IdColumns.Contains(column, StringComparer.Ordinal)
                ? NormalizeIdValue(raw)
                : ToDbValue(raw);
        }

        // Defaults só pros NOT NULL sem DEFAULT no schema que o patch não trouxe. Caso patológico
        // (patch parcial de uma linha que este device nunca viu): inserir com o campo vazio preserva
        // a mudança; descartar a perderia PRA SEMPRE, porque o cursor avança do mesmo jeito.
        var insertColumns = new List<string>(patchColumns);
        foreach ((string column, object fallback) in map.RequiredDefaults)
        {
            if (!values.ContainsKey(column))
            {
                insertColumns.Add(column);
                values[column] = fallback;
            }
        }

        // Listas vazias viram string vazia — o INSERT (id, version) sozinho continua válido.
        string insertCols = string.Concat(insertColumns.Select(c => $", {c}"));
        string insertParams = string.Concat(insertColumns.Select(c => $", ${c}"));
        string setClause = string.Concat(patchColumns.Select(c => $", {c} = excluded.{c}"));

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {map.Table} (id, version{insertCols})
            VALUES ($id, $version{insertParams})
            ON CONFLICT (id) DO UPDATE SET
                version = excluded.version{setClause}
            WHERE excluded.version >= {map.Table}.version;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$version", version);
        foreach ((string column, object value) in values)
        {
            cmd.Parameters.AddWithValue($"${column}", value);
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// DELETE incondicional — sem guarda de versão de propósito: o changelog é entregue em ORDEM de
    /// cursor, então um "deleted" só chega depois de tudo que o precede; e um tombstone que
    /// "perdesse" pra uma versão local mais nova ressuscitaria o host apagado. Host fantasma é pior
    /// que host ausente: o operador tentaria conectar nele.
    /// </summary>
    private static async Task DeleteAsync(
        SqliteConnection conn, TableMap map, SyncChange change, CancellationToken ct)
    {
        string id = NormalizeId(change.EntityId);

        using SqliteTransaction tx = conn.BeginTransaction();

        // Cascata igual à do SqlCipherLocalStore.DeleteAssetAsync: apagar o ativo sem os endpoints
        // deixaria endpoints órfãos, que ninguém lista e ninguém apaga. O device A não empurra os
        // deletes dos endpoints (ele também cascateia), então quem recebe TEM que cascatear.
        if (string.Equals(map.Table, "assets", StringComparison.Ordinal))
        {
            using SqliteCommand epCmd = conn.CreateCommand();
            epCmd.Transaction = tx;
            epCmd.CommandText = "DELETE FROM endpoints WHERE asset_id = $id";
            epCmd.Parameters.AddWithValue("$id", id);
            await epCmd.ExecuteNonQueryAsync(ct);
        }

        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {map.Table} WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    // ── Quarentena (tipos que este app ainda não entende) ─────────────────────────────────

    private static async Task EnsureQuarantineAsync(SqliteConnection conn, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS local_entities (
                entity_type TEXT    NOT NULL,
                entity_id   TEXT    NOT NULL,
                version     INTEGER NOT NULL DEFAULT 0,
                data_json   TEXT    NOT NULL,
                updated_at  TEXT    NOT NULL,
                PRIMARY KEY (entity_type, entity_id)
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task QuarantineUpsertAsync(
        SqliteConnection conn, SyncChange change, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_entities (entity_type, entity_id, version, data_json, updated_at)
            VALUES ($et, $eid, $ver, $data, $ts)
            ON CONFLICT (entity_type, entity_id) DO UPDATE SET
                version    = excluded.version,
                data_json  = excluded.data_json,
                updated_at = excluded.updated_at
            WHERE excluded.version >= local_entities.version;
            """;
        cmd.Parameters.AddWithValue("$et", change.EntityType);
        cmd.Parameters.AddWithValue("$eid", change.EntityId);
        cmd.Parameters.AddWithValue("$ver", change.BaseVersion + 1);
        cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(change.Patch, s_json));
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task QuarantineDeleteAsync(
        SqliteConnection conn, SyncChange change, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_entities WHERE entity_type = $et AND entity_id = $eid";
        cmd.Parameters.AddWithValue("$et", change.EntityType);
        cmd.Parameters.AddWithValue("$eid", change.EntityId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Conversões ────────────────────────────────────────────────────────────────────────

    private static bool TryGetPatchValue(
        Dictionary<string, object?> patch, string column, out object? value)
    {
        // O patch vem de JSON: as chaves são case-sensitive no dicionário, mas um cliente/servidor
        // poderia variar o caixa. Busca direta primeiro (o caminho de sempre), depois tolerante.
        if (patch.TryGetValue(column, out value))
        {
            return true;
        }

        foreach ((string key, object? candidate) in patch)
        {
            if (string.Equals(key, column, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Converte o valor do patch (que veio de JSON, logo <see cref="JsonElement"/>) no que o SQLite
    /// aceita. Array/objeto viram texto cru — é exatamente o que as colunas <c>*_json</c> guardam.
    /// </summary>
    private static object ToDbValue(object? value) => value switch
    {
        null => DBNull.Value,
        JsonElement el => el.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
            JsonValueKind.String => el.GetString() ?? (object)DBNull.Value,
            JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
            JsonValueKind.True => 1L,
            JsonValueKind.False => 0L,
            _ => el.GetRawText(),
        },
        bool b => b ? 1L : 0L,
        _ => value,
    };

    private static object NormalizeIdValue(object? value)
    {
        object converted = ToDbValue(value);
        return converted is string s ? NormalizeId(s) : converted;
    }

    /// <summary>
    /// Canoniza o id para "n" (32 hex, sem hífens) — o formato que o cliente gera.
    ///
    /// <para><b>Não é cosmético.</b> O backend guarda o EntityId num <c>Guid</c> e o devolve com
    /// <c>ToString()</c>, formato "D" (COM hífens), enquanto os campos do patch (<c>asset_id</c>,
    /// <c>credential_ref_id</c>) são ecoados VERBATIM, no "n" que o device A escreveu. Sem canonizar,
    /// o <c>assets.id</c> viraria "D", o <c>endpoint.asset_id</c> continuaria "n", e o host chegaria
    /// no device B sem endereço nenhum — a mesma armadilha que o <c>SecretEnvelopeWireCodec</c> já
    /// teve que resolver no canal de segredos.</para>
    ///
    /// <para>Id que não é GUID passa direto: nem todo id do changelog precisa ser um (o backend, por
    /// sua vez, troca um id não-GUID por um aleatório — mas o cliente sempre gera GUID).</para>
    /// </summary>
    private static string NormalizeId(string id) =>
        Guid.TryParse(id, out Guid parsed) ? parsed.ToString("n") : id;

    private sealed record TableMap(
        string Table,
        string[] Columns,
        string[] IdColumns,
        Dictionary<string, object> RequiredDefaults);
}
