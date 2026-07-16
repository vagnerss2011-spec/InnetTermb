using System.Text.Json;

using Microsoft.Data.Sqlite;

using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sync;
using RemoteOps.Desktop.Domain;
using RemoteOps.Sync;
using RemoteOps.Sync.Storage;

namespace RemoteOps.Desktop.Infrastructure;

/// <summary>
/// Implementação persistente e criptografada de <see cref="ILocalStore"/> usando
/// SQLCipher (ADR-008). A chave AES-256 vem do vault (ADR-003); nunca é logada.
///
/// Cada mutação também grava no outbox via <see cref="WorkspaceContext.SyncClient"/>
/// para consumo pela frente de cloud sync (INT-5).
///
/// Metadados apenas — nunca persiste segredos; somente <c>SecretEnvelopeId</c> (ref).
/// </summary>
public sealed class SqlCipherLocalStore : ILocalStore
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    private readonly WorkspaceContext _ctx;

    public SqlCipherLocalStore(WorkspaceContext ctx) => _ctx = ctx;

    // ── Schema ───────────────────────────────────────────────────────────────

    // O schema mora no RemoteOps.Sync (LocalSchema) porque o applier do changelog escreve nas MESMAS
    // tabelas — e duas definições do mesmo schema já divergiram uma vez (ver LocalSchema).
    private static Task EnsureSchemaAsync(SqliteConnection conn, CancellationToken ct)
        => LocalSchema.EnsureAsync(conn, ct);

    // ── Grupos ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AssetGroup>> GetGroupsAsync(
        string workspaceId, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, workspace_id, parent_id, name, default_credential_ref_id, version
            FROM asset_groups
            WHERE workspace_id = $wid
            ORDER BY name ASC
            """;
        cmd.Parameters.AddWithValue("$wid", workspaceId);

        var groups = new List<AssetGroup>();
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            groups.Add(ReadGroup(reader));

        return groups;
    }

    public async Task<AssetGroup> AddGroupAsync(
        string workspaceId, string name, string? parentId = null, CancellationToken ct = default)
    {
        string id = NewId();

        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO asset_groups (id, workspace_id, parent_id, name, version)
            VALUES ($id, $wid, $pid, $name, 0)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$wid", workspaceId);
        cmd.Parameters.AddWithValue("$pid", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", name);
        await cmd.ExecuteNonQueryAsync(ct);

        var group = new AssetGroup { Id = id, WorkspaceId = workspaceId, Name = name, ParentId = parentId };
        await PushChangeAsync("asset_group", id, "created", GroupPatch(group), baseVersion: 0, ct);

        return group;
    }

    public async Task RenameGroupAsync(string id, string newName, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        int baseVersion = await CurrentVersionAsync(conn, "asset_groups", id, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE asset_groups SET name = $name WHERE id = $id";
        cmd.Parameters.AddWithValue("$name", newName);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);

        // Patch PARCIAL de propósito: rename mexe no nome e só. O applier do outro lado toca apenas
        // as colunas presentes, então o resto da linha fica intacto.
        await PushChangeAsync("asset_group", id, "updated",
            new() { ["name"] = newName }, baseVersion, ct);
    }

    public async Task DeleteGroupAsync(string id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        int baseVersion = await CurrentVersionAsync(conn, "asset_groups", id, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM asset_groups WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);

        await PushChangeAsync("asset_group", id, "deleted", [], baseVersion, ct);
    }

    // ── Ativos ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Asset>> GetAssetsAsync(
        string workspaceId, string? groupId = null, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        // Phase 1: fetch asset rows
        using SqliteCommand assetCmd = conn.CreateCommand();
        assetCmd.CommandText = groupId is null
            ? """
              SELECT id, workspace_id, group_id, name, vendor, model, site, tags_json, version, device_role
              FROM assets WHERE workspace_id = $wid ORDER BY name ASC
              """
            : """
              SELECT id, workspace_id, group_id, name, vendor, model, site, tags_json, version, device_role
              FROM assets WHERE workspace_id = $wid AND group_id = $gid ORDER BY name ASC
              """;
        assetCmd.Parameters.AddWithValue("$wid", workspaceId);
        if (groupId is not null)
            assetCmd.Parameters.AddWithValue("$gid", groupId);

        var rows = new List<AssetRow>();
        using (SqliteDataReader r = await assetCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                rows.Add(ReadAssetRow(r));
        }

        if (rows.Count == 0)
            return [];

        // SQLite limits bound variable count to 999 (SQLITE_LIMIT_VARIABLE_NUMBER).
        if (rows.Count > 900)
            throw new InvalidOperationException(
                $"GetAssetsAsync: workspace contains {rows.Count} assets; batch retrieval not yet supported (limit 900).");

        // Phase 2: fetch endpoints for those assets (single IN query, all params bound)
        var endpointsByAsset = new Dictionary<string, List<Endpoint>>(rows.Count);
        foreach (AssetRow row in rows)
            endpointsByAsset[row.Id] = [];

        using SqliteCommand epCmd = conn.CreateCommand();
        string inClause = string.Join(",", Enumerable.Range(0, rows.Count).Select(i => $"$a{i}"));
        epCmd.CommandText = $"""
            SELECT id, asset_id, protocol, fqdn, ipv4, ipv6, port, prefer_ipv6,
                   credential_ref_id, profile_json, version
            FROM endpoints WHERE asset_id IN ({inClause}) ORDER BY id ASC
            """;
        for (int i = 0; i < rows.Count; i++)
            epCmd.Parameters.AddWithValue($"$a{i}", rows[i].Id);

        using (SqliteDataReader r = await epCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                Endpoint ep = ReadEndpoint(r);
                if (endpointsByAsset.TryGetValue(ep.AssetId, out List<Endpoint>? list))
                    list.Add(ep);
            }
        }

        // Phase 3: assemble final Asset objects
        return rows
            .Select(row => BuildAsset(row, endpointsByAsset[row.Id]))
            .ToList();
    }

    public async Task<Asset?> GetAssetAsync(string id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, workspace_id, group_id, name, vendor, model, site, tags_json, version, device_role
            FROM assets WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);

        AssetRow? row = null;
        using (SqliteDataReader r = await cmd.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
                row = ReadAssetRow(r);
        }

        if (row is null)
            return null;

        List<Endpoint> endpoints = await FetchEndpointsForAssetAsync(conn, id, ct);
        return BuildAsset(row.Value, endpoints);
    }

    public async Task<Asset> AddAssetAsync(AddAssetRequest request, CancellationToken ct = default)
    {
        string id = NewId();

        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO assets (id, workspace_id, group_id, name, vendor, model, device_role, site, tags_json, version)
            VALUES ($id, $wid, $gid, $name, $vendor, $model, $device_role, $site, $tags, 0)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$wid", request.WorkspaceId);
        cmd.Parameters.AddWithValue("$gid", (object?)request.GroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", request.Name);
        cmd.Parameters.AddWithValue("$vendor", (object?)request.Vendor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)request.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$device_role", (object?)request.DeviceRole ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$site", (object?)request.Site ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(request.Tags, s_json));
        await cmd.ExecuteNonQueryAsync(ct);

        var asset = new Asset
        {
            Id = id,
            WorkspaceId = request.WorkspaceId,
            GroupId = request.GroupId,
            Name = request.Name,
            Vendor = request.Vendor,
            Model = request.Model,
            DeviceRole = request.DeviceRole,
            Site = request.Site,
            Tags = [.. request.Tags],
        };

        await PushChangeAsync("asset", id, "created", AssetPatch(asset), baseVersion: 0, ct);

        return asset;
    }

    public async Task<Asset> UpdateAssetAsync(Asset asset, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        // A versão do BANCO, não a que o chamador trouxe: uma Asset carregada há dois minutos pode
        // ter versão obsoleta, e baseVersion obsoleta = mudança recusada pelo servidor, em silêncio.
        int baseVersion = await CurrentVersionAsync(conn, "assets", asset.Id, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE assets
            SET group_id = $gid, name = $name, vendor = $vendor, model = $model,
                device_role = $device_role, site = $site, tags_json = $tags, version = $ver
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", asset.Id);
        cmd.Parameters.AddWithValue("$gid", (object?)asset.GroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", asset.Name);
        cmd.Parameters.AddWithValue("$vendor", (object?)asset.Vendor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)asset.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$device_role", (object?)asset.DeviceRole ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$site", (object?)asset.Site ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(asset.Tags, s_json));
        cmd.Parameters.AddWithValue("$ver", asset.Version);
        await cmd.ExecuteNonQueryAsync(ct);

        await PushChangeAsync("asset", asset.Id, "updated", AssetPatch(asset), baseVersion, ct);

        return asset;
    }

    public async Task DeleteAssetAsync(string id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        int baseVersion = await CurrentVersionAsync(conn, "assets", id, ct);

        using SqliteTransaction tx = conn.BeginTransaction();

        using SqliteCommand epCmd = conn.CreateCommand();
        epCmd.Transaction = tx;
        epCmd.CommandText = "DELETE FROM endpoints WHERE asset_id = $id";
        epCmd.Parameters.AddWithValue("$id", id);
        await epCmd.ExecuteNonQueryAsync(ct);

        using SqliteCommand assetCmd = conn.CreateCommand();
        assetCmd.Transaction = tx;
        assetCmd.CommandText = "DELETE FROM assets WHERE id = $id";
        assetCmd.Parameters.AddWithValue("$id", id);
        await assetCmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);

        // Só o delete do ATIVO sobe: os endpoints foram cascateados aqui e o applier do outro device
        // cascateia igual. Empurrar cada endpoint separado só encheria o changelog.
        await PushChangeAsync("asset", id, "deleted", [], baseVersion, ct);
    }

    // ── Endpoints ────────────────────────────────────────────────────────────

    public async Task<Endpoint?> GetEndpointAsync(string endpointId, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, asset_id, protocol, fqdn, ipv4, ipv6, port, prefer_ipv6,
                   credential_ref_id, profile_json, version
            FROM endpoints WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", endpointId);

        using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadEndpoint(r) : null;
    }

    public async Task<Endpoint> AddEndpointAsync(Endpoint endpoint, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO endpoints
                (id, asset_id, protocol, fqdn, ipv4, ipv6, port, prefer_ipv6,
                 credential_ref_id, profile_json, version)
            VALUES
                ($id, $aid, $proto, $fqdn, $ipv4, $ipv6, $port, $pref,
                 $crid, $profile, 0)
            """;
        cmd.Parameters.AddWithValue("$id", endpoint.Id);
        cmd.Parameters.AddWithValue("$aid", endpoint.AssetId);
        cmd.Parameters.AddWithValue("$proto", endpoint.Protocol);
        cmd.Parameters.AddWithValue("$fqdn", (object?)endpoint.Fqdn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ipv4", (object?)endpoint.Ipv4 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ipv6", (object?)endpoint.Ipv6 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$port", endpoint.Port);
        cmd.Parameters.AddWithValue("$pref", endpoint.PreferIpv6 ? 1 : 0);
        cmd.Parameters.AddWithValue("$crid", (object?)endpoint.CredentialRefId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$profile",
            endpoint.Profile is null ? (object)DBNull.Value : JsonSerializer.Serialize(endpoint.Profile, s_json));
        await cmd.ExecuteNonQueryAsync(ct);

        await PushChangeAsync("endpoint", endpoint.Id, "created", EndpointPatch(endpoint), baseVersion: 0, ct);

        return endpoint;
    }

    public async Task<Endpoint> UpdateEndpointAsync(Endpoint endpoint, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        int baseVersion = await CurrentVersionAsync(conn, "endpoints", endpoint.Id, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE endpoints SET
                protocol = $proto, fqdn = $fqdn, ipv4 = $ipv4, ipv6 = $ipv6, port = $port,
                prefer_ipv6 = $pref, credential_ref_id = $crid, profile_json = $profile
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", endpoint.Id);
        cmd.Parameters.AddWithValue("$proto", endpoint.Protocol);
        cmd.Parameters.AddWithValue("$fqdn", (object?)endpoint.Fqdn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ipv4", (object?)endpoint.Ipv4 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ipv6", (object?)endpoint.Ipv6 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$port", endpoint.Port);
        cmd.Parameters.AddWithValue("$pref", endpoint.PreferIpv6 ? 1 : 0);
        cmd.Parameters.AddWithValue("$crid", (object?)endpoint.CredentialRefId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$profile",
            endpoint.Profile is null ? (object)DBNull.Value : JsonSerializer.Serialize(endpoint.Profile, s_json));
        await cmd.ExecuteNonQueryAsync(ct);

        await PushChangeAsync("endpoint", endpoint.Id, "updated", EndpointPatch(endpoint), baseVersion, ct);

        return endpoint;
    }

    public async Task DeleteEndpointAsync(string id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        int baseVersion = await CurrentVersionAsync(conn, "endpoints", id, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM endpoints WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);

        await PushChangeAsync("endpoint", id, "deleted", [], baseVersion, ct);
    }

    // ── Referências de credencial ─────────────────────────────────────────────

    public async Task<CredentialRef?> GetCredentialRefAsync(
        string credentialRefId, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, type, scope, metadata_json, secret_envelope_id, version
            FROM credential_refs WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", credentialRefId);

        using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadCredentialRef(r) : null;
    }

    public async Task<IReadOnlyList<CredentialRef>> GetCredentialRefsAsync(
        string workspaceId, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, type, scope, metadata_json, secret_envelope_id, version
            FROM credential_refs
            WHERE scope IS NULL OR scope = $wid
            ORDER BY name ASC
            """;
        cmd.Parameters.AddWithValue("$wid", workspaceId);

        var refs = new List<CredentialRef>();
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            refs.Add(ReadCredentialRef(reader));

        return refs;
    }

    public async Task<CredentialRef> AddCredentialRefAsync(
        CredentialRef credentialRef, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        string? metaJson = credentialRef.Metadata is null
            ? null
            : JsonSerializer.Serialize(credentialRef.Metadata, s_json);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO credential_refs (id, name, type, scope, metadata_json, secret_envelope_id, version)
            VALUES ($id, $name, $type, $scope, $meta, $seid, $ver)
            """;
        cmd.Parameters.AddWithValue("$id", credentialRef.Id);
        cmd.Parameters.AddWithValue("$name", credentialRef.Name);
        cmd.Parameters.AddWithValue("$type", credentialRef.Type);
        cmd.Parameters.AddWithValue("$scope", (object?)credentialRef.Scope ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$meta", (object?)metaJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seid", (object?)credentialRef.SecretEnvelopeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ver", credentialRef.Version);
        await cmd.ExecuteNonQueryAsync(ct);

        // SecretEnvelopeId é referência (não o segredo) — seguro incluir no outbox.
        await PushChangeAsync("credential_ref", credentialRef.Id, "created",
            CredentialRefPatch(credentialRef), baseVersion: 0, ct);

        return credentialRef;
    }

    public async Task<CredentialRef> UpdateCredentialRefAsync(
        CredentialRef credentialRef, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        int baseVersion = await CurrentVersionAsync(conn, "credential_refs", credentialRef.Id, ct);

        string? metaJson = credentialRef.Metadata is null
            ? null
            : JsonSerializer.Serialize(credentialRef.Metadata, s_json);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE credential_refs
            SET name = $name, type = $type, scope = $scope, metadata_json = $meta,
                secret_envelope_id = $seid, version = $ver
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", credentialRef.Id);
        cmd.Parameters.AddWithValue("$name", credentialRef.Name);
        cmd.Parameters.AddWithValue("$type", credentialRef.Type);
        cmd.Parameters.AddWithValue("$scope", (object?)credentialRef.Scope ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$meta", (object?)metaJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seid", (object?)credentialRef.SecretEnvelopeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ver", credentialRef.Version);
        await cmd.ExecuteNonQueryAsync(ct);

        // SecretEnvelopeId é referência (não o segredo) — seguro incluir no outbox.
        await PushChangeAsync("credential_ref", credentialRef.Id, "updated",
            CredentialRefPatch(credentialRef), baseVersion, ct);

        return credentialRef;
    }

    public async Task DeleteCredentialRefAsync(string id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        int baseVersion = await CurrentVersionAsync(conn, "credential_refs", id, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM credential_refs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);

        await PushChangeAsync("credential_ref", id, "deleted", [], baseVersion, ct);
    }

    // ── Outbox: os patches ────────────────────────────────────────────────────
    //
    // As chaves do patch são os NOMES DAS COLUNAS, de propósito: o applier do outro device
    // (LocalEntitiesChangeApplier) grava direto na tabela real, sem tradução — não há onde divergir.
    // Cada tipo tem UM construtor de patch, usado por created E updated: a versão anterior tinha
    // dois patches escritos à mão por tipo, e foi exatamente aí que os campos sumiram (o endpoint
    // subia sem fqdn/ipv4/credential_ref_id, e o device B recebia um host sem endereço e sem senha).
    //
    // NUNCA entra segredo aqui (ADR-003). SecretEnvelopeId e PassphraseEnvelopeId são REFERÊNCIAS a
    // envelopes cifrados — é o que liga o host à senha no outro device, e o servidor não abre nada
    // com elas. O blob viaja pelo canal /secrets.

    private static Dictionary<string, object?> GroupPatch(AssetGroup group) => new()
    {
        ["id"] = group.Id,
        ["workspace_id"] = group.WorkspaceId,
        ["parent_id"] = group.ParentId,
        ["name"] = group.Name,
        ["default_credential_ref_id"] = group.DefaultCredentialRefId,
    };

    private static Dictionary<string, object?> AssetPatch(Asset asset) => new()
    {
        ["id"] = asset.Id,
        ["workspace_id"] = asset.WorkspaceId,
        ["group_id"] = asset.GroupId,
        ["name"] = asset.Name,
        ["vendor"] = asset.Vendor,
        ["model"] = asset.Model,
        ["device_role"] = asset.DeviceRole,
        ["site"] = asset.Site,
        // Colunas *_json viajam já serializadas (o mesmo texto que a coluna guarda) — o applier grava
        // verbatim e o round-trip não tem chance de reinterpretar nada.
        ["tags_json"] = JsonSerializer.Serialize(asset.Tags, s_json),
    };

    private static Dictionary<string, object?> EndpointPatch(Endpoint endpoint) => new()
    {
        ["id"] = endpoint.Id,
        ["asset_id"] = endpoint.AssetId,
        ["protocol"] = endpoint.Protocol,
        ["fqdn"] = endpoint.Fqdn,
        ["ipv4"] = endpoint.Ipv4,
        ["ipv6"] = endpoint.Ipv6,
        ["port"] = endpoint.Port,
        ["prefer_ipv6"] = endpoint.PreferIpv6,
        ["credential_ref_id"] = endpoint.CredentialRefId,
        ["profile_json"] = endpoint.Profile is null
            ? null
            : JsonSerializer.Serialize(endpoint.Profile, s_json),
    };

    private static Dictionary<string, object?> CredentialRefPatch(CredentialRef cr) => new()
    {
        ["id"] = cr.Id,
        ["name"] = cr.Name,
        ["type"] = cr.Type,
        ["scope"] = cr.Scope,
        // metadata_json carrega o USERNAME (metadado, não segredo) — sem ele o device B recebe a
        // senha e não sabe de quem ela é.
        ["metadata_json"] = cr.Metadata is null ? null : JsonSerializer.Serialize(cr.Metadata, s_json),
        ["secret_envelope_id"] = cr.SecretEnvelopeId,
    };

    // ── Helpers privados ──────────────────────────────────────────────────────

    private Task PushChangeAsync(
        string entityType, string entityId, string operation,
        Dictionary<string, object?> patch, int baseVersion, CancellationToken ct)
        => _ctx.SyncClient.PushAsync(
            [new SyncChange
            {
                ClientChangeId = Guid.NewGuid().ToString("n"),
                EntityType = entityType,
                EntityId = entityId,
                Operation = operation,
                BaseVersion = baseVersion,
                Patch = patch,
            }], ct);

    /// <summary>
    /// Versão ATUAL da linha, para usar como <c>BaseVersion</c> do push. 0 se a linha não existe.
    ///
    /// <para><b>Por que ler do banco em vez de mandar 0:</b> o servidor recusa a mudança quando
    /// <c>baseVersion &lt; versão atual</c> (<c>version.conflict</c>) e ela nunca entra no changelog.
    /// Como toda entidade fica em v1 assim que o primeiro push é aceito, um <c>baseVersion: 0</c>
    /// fixo fazia TODO update e TODO delete serem silenciosamente recusados — o rename e o host
    /// apagado simplesmente não chegavam no outro device. O nome da tabela é constante interna
    /// (não entrada do usuário), então a interpolação não abre injeção.</para>
    /// </summary>
    private static async Task<int> CurrentVersionAsync(
        SqliteConnection conn, string table, string id, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT version FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    private static async Task<List<Endpoint>> FetchEndpointsForAssetAsync(
        SqliteConnection conn, string assetId, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, asset_id, protocol, fqdn, ipv4, ipv6, port, prefer_ipv6,
                   credential_ref_id, profile_json, version
            FROM endpoints WHERE asset_id = $aid ORDER BY id ASC
            """;
        cmd.Parameters.AddWithValue("$aid", assetId);

        var list = new List<Endpoint>();
        using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(ReadEndpoint(r));

        return list;
    }

    // ── Leitura de linhas ─────────────────────────────────────────────────────

    private static AssetGroup ReadGroup(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        WorkspaceId = r.GetString(1),
        ParentId = r.IsDBNull(2) ? null : r.GetString(2),
        Name = r.GetString(3),
        DefaultCredentialRefId = r.IsDBNull(4) ? null : r.GetString(4),
        Version = r.GetInt32(5),
    };

    // Intermediate struct to carry raw row data before endpoints are fetched.
    private readonly record struct AssetRow(
        string Id, string WorkspaceId, string? GroupId, string Name,
        string? Vendor, string? Model, string? Site, string TagsJson, int Version, string? DeviceRole);

    private static AssetRow ReadAssetRow(SqliteDataReader r) => new(
        Id: r.GetString(0),
        WorkspaceId: r.GetString(1),
        GroupId: r.IsDBNull(2) ? null : r.GetString(2),
        Name: r.GetString(3),
        Vendor: r.IsDBNull(4) ? null : r.GetString(4),
        Model: r.IsDBNull(5) ? null : r.GetString(5),
        Site: r.IsDBNull(6) ? null : r.GetString(6),
        TagsJson: r.GetString(7),
        Version: r.GetInt32(8),
        DeviceRole: r.IsDBNull(9) ? null : r.GetString(9));

    private static Asset BuildAsset(AssetRow row, List<Endpoint> endpoints)
    {
        List<string> tags = JsonSerializer.Deserialize<List<string>>(row.TagsJson, s_json) ?? [];
        return new Asset
        {
            Id = row.Id,
            WorkspaceId = row.WorkspaceId,
            GroupId = row.GroupId,
            Name = row.Name,
            Vendor = row.Vendor,
            Model = row.Model,
            DeviceRole = row.DeviceRole,
            Site = row.Site,
            Tags = tags,
            Version = row.Version,
            Endpoints = endpoints,
        };
    }

    private static Endpoint ReadEndpoint(SqliteDataReader r)
    {
        string? profileJson = r.IsDBNull(9) ? null : r.GetString(9);
        return new Endpoint
        {
            Id = r.GetString(0),
            AssetId = r.GetString(1),
            Protocol = r.GetString(2),
            Fqdn = r.IsDBNull(3) ? null : r.GetString(3),
            Ipv4 = r.IsDBNull(4) ? null : r.GetString(4),
            Ipv6 = r.IsDBNull(5) ? null : r.GetString(5),
            Port = r.GetInt32(6),
            PreferIpv6 = r.GetInt32(7) != 0,
            CredentialRefId = r.IsDBNull(8) ? null : r.GetString(8),
            Profile = profileJson is null ? null : JsonSerializer.Deserialize<EndpointProfile>(profileJson, s_json),
        };
    }

    private static CredentialRef ReadCredentialRef(SqliteDataReader r)
    {
        // Columns: 0=id, 1=name, 2=type, 3=scope, 4=metadata_json, 5=secret_envelope_id, 6=version
        string? metaJson = r.IsDBNull(4) ? null : r.GetString(4);
        return new CredentialRef
        {
            Id = r.GetString(0),
            Name = r.GetString(1),
            Type = r.GetString(2),
            Scope = r.IsDBNull(3) ? null : r.GetString(3),
            Metadata = metaJson is null ? null : JsonSerializer.Deserialize<CredentialMetadata>(metaJson, s_json),
            SecretEnvelopeId = r.IsDBNull(5) ? null : r.GetString(5),
            Version = r.GetInt32(6),
        };
    }

    private static string NewId() => Guid.NewGuid().ToString("n");
}
