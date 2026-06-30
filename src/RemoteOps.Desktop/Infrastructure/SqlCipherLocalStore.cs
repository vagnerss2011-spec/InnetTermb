using System.Text.Json;

using Microsoft.Data.Sqlite;

using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sync;
using RemoteOps.Desktop.Domain;
using RemoteOps.Sync;

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

    private static async Task EnsureSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS asset_groups (
                id                        TEXT PRIMARY KEY,
                workspace_id              TEXT NOT NULL,
                parent_id                 TEXT,
                name                      TEXT NOT NULL,
                default_credential_ref_id TEXT,
                version                   INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_ag_workspace ON asset_groups (workspace_id);

            CREATE TABLE IF NOT EXISTS assets (
                id           TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                group_id     TEXT,
                name         TEXT NOT NULL,
                vendor       TEXT,
                model        TEXT,
                site         TEXT,
                tags_json    TEXT NOT NULL DEFAULT '[]',
                version      INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_assets_workspace ON assets (workspace_id);

            CREATE TABLE IF NOT EXISTS endpoints (
                id                TEXT PRIMARY KEY,
                asset_id          TEXT NOT NULL,
                protocol          TEXT NOT NULL,
                fqdn              TEXT,
                ipv4              TEXT,
                ipv6              TEXT,
                port              INTEGER NOT NULL DEFAULT 0,
                prefer_ipv6       INTEGER NOT NULL DEFAULT 1,
                credential_ref_id TEXT,
                profile_json      TEXT,
                version           INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_ep_asset ON endpoints (asset_id);

            CREATE TABLE IF NOT EXISTS credential_refs (
                id                 TEXT PRIMARY KEY,
                name               TEXT NOT NULL,
                type               TEXT NOT NULL,
                scope              TEXT,
                metadata_json      TEXT,
                secret_envelope_id TEXT,
                version            INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_cred_scope ON credential_refs (scope);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

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

        await PushChangeAsync("asset_group", id, "created",
            new() { ["id"] = id, ["workspace_id"] = workspaceId, ["name"] = name, ["parent_id"] = parentId },
            baseVersion: 0, ct);

        return new AssetGroup { Id = id, WorkspaceId = workspaceId, Name = name, ParentId = parentId };
    }

    public async Task RenameGroupAsync(string id, string newName, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE asset_groups SET name = $name WHERE id = $id";
        cmd.Parameters.AddWithValue("$name", newName);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);

        await PushChangeAsync("asset_group", id, "updated",
            new() { ["name"] = newName }, baseVersion: 0, ct);
    }

    public async Task DeleteGroupAsync(string id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM asset_groups WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);

        await PushChangeAsync("asset_group", id, "deleted", [], baseVersion: 0, ct);
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
              SELECT id, workspace_id, group_id, name, vendor, model, site, tags_json, version
              FROM assets WHERE workspace_id = $wid ORDER BY name ASC
              """
            : """
              SELECT id, workspace_id, group_id, name, vendor, model, site, tags_json, version
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
            SELECT id, workspace_id, group_id, name, vendor, model, site, tags_json, version
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
            INSERT INTO assets (id, workspace_id, group_id, name, vendor, model, site, tags_json, version)
            VALUES ($id, $wid, $gid, $name, $vendor, $model, $site, $tags, 0)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$wid", request.WorkspaceId);
        cmd.Parameters.AddWithValue("$gid", (object?)request.GroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", request.Name);
        cmd.Parameters.AddWithValue("$vendor", (object?)request.Vendor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)request.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$site", (object?)request.Site ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(request.Tags, s_json));
        await cmd.ExecuteNonQueryAsync(ct);

        await PushChangeAsync("asset", id, "created",
            new()
            {
                ["id"] = id, ["workspace_id"] = request.WorkspaceId,
                ["group_id"] = request.GroupId, ["name"] = request.Name,
                ["vendor"] = request.Vendor, ["model"] = request.Model, ["site"] = request.Site,
            }, baseVersion: 0, ct);

        return new Asset
        {
            Id = id,
            WorkspaceId = request.WorkspaceId,
            GroupId = request.GroupId,
            Name = request.Name,
            Vendor = request.Vendor,
            Model = request.Model,
            Site = request.Site,
            Tags = [.. request.Tags],
        };
    }

    public async Task<Asset> UpdateAssetAsync(Asset asset, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE assets
            SET group_id = $gid, name = $name, vendor = $vendor, model = $model,
                site = $site, tags_json = $tags, version = $ver
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", asset.Id);
        cmd.Parameters.AddWithValue("$gid", (object?)asset.GroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", asset.Name);
        cmd.Parameters.AddWithValue("$vendor", (object?)asset.Vendor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)asset.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$site", (object?)asset.Site ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(asset.Tags, s_json));
        cmd.Parameters.AddWithValue("$ver", asset.Version);
        await cmd.ExecuteNonQueryAsync(ct);

        await PushChangeAsync("asset", asset.Id, "updated",
            new()
            {
                ["group_id"] = asset.GroupId, ["name"] = asset.Name,
                ["vendor"] = asset.Vendor, ["model"] = asset.Model, ["site"] = asset.Site,
            }, asset.Version, ct);

        return asset;
    }

    public async Task DeleteAssetAsync(string id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

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

        await PushChangeAsync("asset", id, "deleted", [], baseVersion: 0, ct);
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

        await PushChangeAsync("endpoint", endpoint.Id, "created",
            new()
            {
                ["id"] = endpoint.Id, ["asset_id"] = endpoint.AssetId,
                ["protocol"] = endpoint.Protocol, ["port"] = endpoint.Port,
            }, baseVersion: 0, ct);

        return endpoint;
    }

    public async Task DeleteEndpointAsync(string id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM endpoints WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);

        await PushChangeAsync("endpoint", id, "deleted", [], baseVersion: 0, ct);
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
            new()
            {
                ["id"] = credentialRef.Id, ["name"] = credentialRef.Name,
                ["type"] = credentialRef.Type, ["scope"] = credentialRef.Scope,
                ["secret_envelope_id"] = credentialRef.SecretEnvelopeId,
            }, credentialRef.Version, ct);

        return credentialRef;
    }

    public async Task DeleteCredentialRefAsync(string id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _ctx.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM credential_refs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);

        await PushChangeAsync("credential_ref", id, "deleted", [], baseVersion: 0, ct);
    }

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
        string? Vendor, string? Model, string? Site, string TagsJson, int Version);

    private static AssetRow ReadAssetRow(SqliteDataReader r) => new(
        Id: r.GetString(0),
        WorkspaceId: r.GetString(1),
        GroupId: r.IsDBNull(2) ? null : r.GetString(2),
        Name: r.GetString(3),
        Vendor: r.IsDBNull(4) ? null : r.GetString(4),
        Model: r.IsDBNull(5) ? null : r.GetString(5),
        Site: r.IsDBNull(6) ? null : r.GetString(6),
        TagsJson: r.GetString(7),
        Version: r.GetInt32(8));

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
