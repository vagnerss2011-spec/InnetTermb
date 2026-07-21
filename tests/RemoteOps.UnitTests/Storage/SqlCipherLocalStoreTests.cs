using System.IO;

using Microsoft.Data.Sqlite;

using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sync;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Sync;
using RemoteOps.Sync.Storage;
using RemoteOps.UnitTests.Sync;

using Xunit;

namespace RemoteOps.UnitTests.Storage;

/// <summary>
/// Testes de aceite para <see cref="SqlCipherLocalStore"/>:
/// CRUD round-trip, persistência após restart, criptografia do banco,
/// outbox e isolamento por workspace.
/// </summary>
public sealed class SqlCipherLocalStoreTests
{
    // ── Grupos ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddGroup_Then_GetGroups_RoundTrip()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        AssetGroup g = await ctx.Store.AddGroupAsync(ctx.WorkspaceId, "DC-Nordeste", parentId: null);

        IReadOnlyList<AssetGroup> groups = await ctx.Store.GetGroupsAsync(ctx.WorkspaceId);

        Assert.Single(groups);
        Assert.Equal(g.Id, groups[0].Id);
        Assert.Equal("DC-Nordeste", groups[0].Name);
        Assert.Equal(ctx.WorkspaceId, groups[0].WorkspaceId);
    }

    [Fact]
    public async Task RenameGroup_Updates_Name()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        AssetGroup g = await ctx.Store.AddGroupAsync(ctx.WorkspaceId, "Old Name");
        await ctx.Store.RenameGroupAsync(g.Id, "New Name");

        IReadOnlyList<AssetGroup> groups = await ctx.Store.GetGroupsAsync(ctx.WorkspaceId);
        Assert.Equal("New Name", groups[0].Name);
    }

    [Fact]
    public async Task DeleteGroup_Removes_It()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        AssetGroup g = await ctx.Store.AddGroupAsync(ctx.WorkspaceId, "Temp");
        await ctx.Store.DeleteGroupAsync(g.Id);

        IReadOnlyList<AssetGroup> groups = await ctx.Store.GetGroupsAsync(ctx.WorkspaceId);
        Assert.Empty(groups);
    }

    // ── Ativos ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddAsset_Then_GetAsset_RoundTrip()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        var request = new AddAssetRequest
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "Router-Core-01",
            Vendor = "MikroTik",
            Model = "CCR2004",
            Site = "SP",
            Tags = ["core", "bgp"],
        };

        Asset added = await ctx.Store.AddAssetAsync(request);
        Asset? fetched = await ctx.Store.GetAssetAsync(added.Id);

        Assert.NotNull(fetched);
        Assert.Equal("Router-Core-01", fetched!.Name);
        Assert.Equal("MikroTik", fetched.Vendor);
        Assert.Equal("CCR2004", fetched.Model);
        Assert.Equal("SP", fetched.Site);
        Assert.Equal(["core", "bgp"], fetched.Tags);
    }

    [Fact]
    public async Task GetAssetsAsync_FiltersByGroup()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        AssetGroup grp = await ctx.Store.AddGroupAsync(ctx.WorkspaceId, "Core");
        await ctx.Store.AddAssetAsync(new AddAssetRequest { WorkspaceId = ctx.WorkspaceId, GroupId = grp.Id, Name = "sw-core" });
        await ctx.Store.AddAssetAsync(new AddAssetRequest { WorkspaceId = ctx.WorkspaceId, Name = "sw-edge" });

        IReadOnlyList<Asset> all = await ctx.Store.GetAssetsAsync(ctx.WorkspaceId);
        IReadOnlyList<Asset> inGroup = await ctx.Store.GetAssetsAsync(ctx.WorkspaceId, grp.Id);

        Assert.Equal(2, all.Count);
        Assert.Single(inGroup);
        Assert.Equal("sw-core", inGroup[0].Name);
    }

    [Fact]
    public async Task UpdateAsset_Persists_Changes()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        Asset asset = await ctx.Store.AddAssetAsync(
            new AddAssetRequest { WorkspaceId = ctx.WorkspaceId, Name = "Original" });

        var updated = new Asset
        {
            Id = asset.Id,
            WorkspaceId = asset.WorkspaceId,
            Name = "Renamed",
            Vendor = "Cisco",
            Model = null,
            Site = null,
            Tags = [],
            Version = asset.Version,
        };

        await ctx.Store.UpdateAssetAsync(updated);
        Asset? fetched = await ctx.Store.GetAssetAsync(asset.Id);

        Assert.Equal("Renamed", fetched!.Name);
        Assert.Equal("Cisco", fetched.Vendor);
    }

    [Fact]
    public async Task DeleteAsset_CascadesEndpoints()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        Asset asset = await ctx.Store.AddAssetAsync(
            new AddAssetRequest { WorkspaceId = ctx.WorkspaceId, Name = "fw-01" });

        await ctx.Store.AddEndpointAsync(new Endpoint
        {
            Id = Guid.NewGuid().ToString("n"),
            AssetId = asset.Id,
            Protocol = "ssh",
            Port = 22,
        });

        await ctx.Store.DeleteAssetAsync(asset.Id);

        Asset? fetched = await ctx.Store.GetAssetAsync(asset.Id);
        Assert.Null(fetched);
        // Endpoints should also be gone (no FK violation on re-insert with same assetId)
        IReadOnlyList<Asset> all = await ctx.Store.GetAssetsAsync(ctx.WorkspaceId);
        Assert.Empty(all);
    }

    // ── Endpoints ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddEndpoint_AppearsInGetAsset()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        Asset asset = await ctx.Store.AddAssetAsync(
            new AddAssetRequest { WorkspaceId = ctx.WorkspaceId, Name = "router-01" });

        var ep = new Endpoint
        {
            Id = Guid.NewGuid().ToString("n"),
            AssetId = asset.Id,
            Protocol = "ssh",
            Ipv4 = "10.0.0.1",
            Port = 22,
            PreferIpv6 = false,
        };

        await ctx.Store.AddEndpointAsync(ep);

        Asset? fetched = await ctx.Store.GetAssetAsync(asset.Id);
        Assert.NotNull(fetched);
        Assert.Single(fetched!.Endpoints);
        Assert.Equal("ssh", fetched.Endpoints[0].Protocol);
        Assert.Equal("10.0.0.1", fetched.Endpoints[0].Ipv4);
        Assert.Equal(22, fetched.Endpoints[0].Port);
        Assert.False(fetched.Endpoints[0].PreferIpv6);
    }

    [Fact]
    public async Task AddEndpoint_WithProfile_RoundTrip()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        Asset asset = await ctx.Store.AddAssetAsync(
            new AddAssetRequest { WorkspaceId = ctx.WorkspaceId, Name = "sw-01" });

        var ep = new Endpoint
        {
            Id = Guid.NewGuid().ToString("n"),
            AssetId = asset.Id,
            Protocol = "telnet",
            Port = 23,
            Profile = new EndpointProfile { VendorProfile = "mikrotik", TerminalEncoding = "utf8" },
        };

        await ctx.Store.AddEndpointAsync(ep);

        Asset? fetched = await ctx.Store.GetAssetAsync(asset.Id);
        EndpointProfile? profile = fetched!.Endpoints[0].Profile;
        Assert.NotNull(profile);
        Assert.Equal("mikrotik", profile!.VendorProfile);
        Assert.Equal("utf8", profile.TerminalEncoding);
    }

    [Fact]
    public async Task UpdateEndpoint_PersistsBackspaceMode_RoundTrip()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        Asset asset = await ctx.Store.AddAssetAsync(
            new AddAssetRequest { WorkspaceId = ctx.WorkspaceId, Name = "olt-01" });

        string epId = Guid.NewGuid().ToString("n");
        var ep = new Endpoint { Id = epId, AssetId = asset.Id, Protocol = "ssh", Port = 22 };
        await ctx.Store.AddEndpointAsync(ep);

        // Troca o modo do Backspace para Ctrl+H (BS) e persiste.
        var updated = new Endpoint
        {
            Id = epId,
            AssetId = asset.Id,
            Protocol = "ssh",
            Port = 22,
            Profile = new EndpointProfile { BackspaceMode = TerminalBackspaceModes.ControlH },
        };
        await ctx.Store.UpdateEndpointAsync(updated);

        Endpoint? fetched = await ctx.Store.GetEndpointAsync(epId);
        Assert.NotNull(fetched);
        Assert.Equal(TerminalBackspaceModes.ControlH, fetched!.Profile?.BackspaceMode);
    }

    [Fact]
    public async Task DeleteEndpoint_RemovesIt()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        Asset asset = await ctx.Store.AddAssetAsync(
            new AddAssetRequest { WorkspaceId = ctx.WorkspaceId, Name = "ap-01" });
        string epId = Guid.NewGuid().ToString("n");
        await ctx.Store.AddEndpointAsync(new Endpoint
        {
            Id = epId,
            AssetId = asset.Id,
            Protocol = "ssh",
            Port = 22,
        });

        await ctx.Store.DeleteEndpointAsync(epId);

        Asset? fetched = await ctx.Store.GetAssetAsync(asset.Id);
        Assert.Empty(fetched!.Endpoints);
    }

    // ── Referências de credencial ──────────────────────────────────────────────

    [Fact]
    public async Task AddCredentialRef_Then_GetCredentialRefs_RoundTrip()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        var cred = new CredentialRef
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = "admin-ssh",
            Type = "password",
            Scope = ctx.WorkspaceId,
            SecretEnvelopeId = "envelope-abc123",   // referência, não o segredo
            Metadata = new CredentialMetadata { Username = "admin", HasPrivateKey = false },
        };

        await ctx.Store.AddCredentialRefAsync(cred);
        IReadOnlyList<CredentialRef> refs = await ctx.Store.GetCredentialRefsAsync(ctx.WorkspaceId);

        Assert.Single(refs);
        Assert.Equal("admin-ssh", refs[0].Name);
        Assert.Equal("password", refs[0].Type);
        Assert.Equal("envelope-abc123", refs[0].SecretEnvelopeId);
        Assert.Equal("admin", refs[0].Metadata!.Username);
    }

    [Fact]
    public async Task GetCredentialRefs_IncludesGlobalScope()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        var scoped = new CredentialRef
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = "scoped-cred",
            Type = "password",
            Scope = ctx.WorkspaceId,
        };
        var global = new CredentialRef
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = "global-cred",
            Type = "password",
            Scope = null,
        };

        await ctx.Store.AddCredentialRefAsync(scoped);
        await ctx.Store.AddCredentialRefAsync(global);

        IReadOnlyList<CredentialRef> refs = await ctx.Store.GetCredentialRefsAsync(ctx.WorkspaceId);
        Assert.Equal(2, refs.Count);
    }

    [Fact]
    public async Task DeleteCredentialRef_RemovesIt()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        var cred = new CredentialRef
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = "temp",
            Type = "password",
            Scope = ctx.WorkspaceId,
        };
        await ctx.Store.AddCredentialRefAsync(cred);
        await ctx.Store.DeleteCredentialRefAsync(cred.Id);

        IReadOnlyList<CredentialRef> refs = await ctx.Store.GetCredentialRefsAsync(ctx.WorkspaceId);
        Assert.Empty(refs);
    }

    // ── Persistência após restart ──────────────────────────────────────────────

    [Fact]
    public async Task Data_Persists_AfterRestart()
    {
        using var ctx = await StoreTestContext.CreateAsync("ws-persist");

        AssetGroup g = await ctx.Store.AddGroupAsync(ctx.WorkspaceId, "Backbone");
        Asset a = await ctx.Store.AddAssetAsync(new AddAssetRequest
        {
            WorkspaceId = ctx.WorkspaceId,
            GroupId = g.Id,
            Name = "core-rtr",
        });

        // "Restart": second store over the same DB (same vault + same keyref file)
        SqlCipherLocalStore store2 = await ctx.ReopenStoreAsync();

        IReadOnlyList<AssetGroup> groups = await store2.GetGroupsAsync(ctx.WorkspaceId);
        IReadOnlyList<Asset> assets = await store2.GetAssetsAsync(ctx.WorkspaceId);

        Assert.Single(groups);
        Assert.Equal("Backbone", groups[0].Name);
        Assert.Single(assets);
        Assert.Equal("core-rtr", assets[0].Name);
    }

    // ── Criptografia (SQLCipher) ───────────────────────────────────────────────

    [Fact]
    public async Task Db_Is_Unreadable_Without_Key()
    {
        if (!IsSqlCipherAvailable())
            return;

        using var ctx = await StoreTestContext.CreateAsync("ws-crypto");
        await ctx.Store.AddGroupAsync(ctx.WorkspaceId, "Crypto-Test");

        string dbPath = ctx.Factory.DbPath(ctx.WorkspaceId);
        Assert.True(File.Exists(dbPath));

        // Tenta abrir o arquivo .db sem fornecer a chave → SQLCipher rejeita.
        using var rawConn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await rawConn.OpenAsync();
        using var cmd = rawConn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM asset_groups";

        await Assert.ThrowsAnyAsync<Exception>(() => cmd.ExecuteScalarAsync());
    }

    // ── Outbox ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddGroup_Pushes_Created_Change_To_Outbox()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        await ctx.Store.AddGroupAsync(ctx.WorkspaceId, "Outbox-Test");

        IReadOnlyList<SyncChange> changes = await ctx.Ctx.SyncClient.PullAsync(0);

        Assert.Single(changes);
        Assert.Equal("asset_group", changes[0].EntityType);
        Assert.Equal("created", changes[0].Operation);
        Assert.Equal("Outbox-Test", changes[0].Patch["name"]?.ToString());
    }

    [Fact]
    public async Task DeleteAsset_Pushes_Deleted_Change_To_Outbox()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        Asset a = await ctx.Store.AddAssetAsync(
            new AddAssetRequest { WorkspaceId = ctx.WorkspaceId, Name = "to-delete" });

        // O cursor só avança no Pull (Push apenas grava no outbox). Consome o "created"
        // para que o Pull seguinte traga somente o "deleted".
        await ctx.Ctx.SyncClient.PullAsync(0);
        long cursorAfterAdd = ctx.Ctx.SyncClient.CurrentCursor;

        await ctx.Store.DeleteAssetAsync(a.Id);

        IReadOnlyList<SyncChange> changes = await ctx.Ctx.SyncClient.PullAsync(cursorAfterAdd);
        Assert.Single(changes);
        Assert.Equal("asset", changes[0].EntityType);
        Assert.Equal("deleted", changes[0].Operation);
        Assert.Equal(a.Id, changes[0].EntityId);
    }

    [Fact]
    public async Task AllMutations_Produce_Outbox_Entry()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        AssetGroup g = await ctx.Store.AddGroupAsync(ctx.WorkspaceId, "G");          // +1
        await ctx.Store.RenameGroupAsync(g.Id, "G2");                                // +1
        Asset a = await ctx.Store.AddAssetAsync(
            new AddAssetRequest { WorkspaceId = ctx.WorkspaceId, Name = "A" });      // +1
        string epId = Guid.NewGuid().ToString("n");
        await ctx.Store.AddEndpointAsync(
            new Endpoint { Id = epId, AssetId = a.Id, Protocol = "ssh", Port = 22 });// +1
        await ctx.Store.DeleteEndpointAsync(epId);                                   // +1
        await ctx.Store.DeleteGroupAsync(g.Id);                                      // +1

        IReadOnlyList<SyncChange> all = await ctx.Ctx.SyncClient.PullAsync(0);

        Assert.Equal(6, all.Count);
    }

    // ── Isolamento por workspace ───────────────────────────────────────────────

    [Fact]
    public async Task Different_Workspaces_Use_Separate_Databases()
    {
        string dir = Path.Combine(Path.GetTempPath(), "remoteops-isolation-test", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);

        try
        {
            var vault = new FakeCredentialVault();
            var factory = new LocalSyncClientFactory(vault, dir);

            WorkspaceContext ctxA = await factory.OpenWorkspaceAsync("ws-alpha");
            WorkspaceContext ctxB = await factory.OpenWorkspaceAsync("ws-beta");

            var storeA = new SqlCipherLocalStore(ctxA);
            var storeB = new SqlCipherLocalStore(ctxB);

            await storeA.AddGroupAsync("ws-alpha", "Only in Alpha");

            // ws-beta should see nothing
            IReadOnlyList<AssetGroup> betaGroups = await storeB.GetGroupsAsync("ws-beta");
            Assert.Empty(betaGroups);

            // ws-alpha still has its data
            IReadOnlyList<AssetGroup> alphaGroups = await storeA.GetGroupsAsync("ws-alpha");
            Assert.Single(alphaGroups);
        }
        finally
        {
            if (Directory.Exists(dir))
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Workspaces_Have_Independent_DB_Files()
    {
        string dir = Path.Combine(Path.GetTempPath(), "remoteops-dbfile-test", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);

        try
        {
            var vault = new FakeCredentialVault();
            var factory = new LocalSyncClientFactory(vault, dir);

            WorkspaceContext c1 = await factory.OpenWorkspaceAsync("ws-1");
            WorkspaceContext c2 = await factory.OpenWorkspaceAsync("ws-2");

            // O arquivo .db é criado preguiçosamente na primeira operação do store;
            // uma mutação em cada workspace materializa os dois bancos separados.
            await new SqlCipherLocalStore(c1).AddGroupAsync("ws-1", "g1");
            await new SqlCipherLocalStore(c2).AddGroupAsync("ws-2", "g2");

            Assert.True(File.Exists(Path.Combine(dir, "sync-ws-1.db")));
            Assert.True(File.Exists(Path.Combine(dir, "sync-ws-2.db")));
        }
        finally
        {
            if (Directory.Exists(dir))
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ── Testes negativos ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsset_Returns_Null_For_Unknown_Id()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        Asset? result = await ctx.Store.GetAssetAsync(Guid.NewGuid().ToString("n"));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCredentialRefs_Excludes_Other_Workspace_Scoped_Creds()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        // Credential scoped to a DIFFERENT workspace — should not appear.
        await ctx.Store.AddCredentialRefAsync(new CredentialRef
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = "other-ws-cred",
            Type = "password",
            Scope = "ws-other",
        });

        IReadOnlyList<CredentialRef> refs = await ctx.Store.GetCredentialRefsAsync(ctx.WorkspaceId);
        Assert.Empty(refs);
    }

    // ── Segurança ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenWorkspace_Throws_For_Invalid_WorkspaceId()
    {
        string dir = Path.Combine(Path.GetTempPath(), "remoteops-invalid-ws", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var vault = new FakeCredentialVault();
            var factory = new LocalSyncClientFactory(vault, dir);

            // Path traversal attempt — must be rejected before any file operation.
            await Assert.ThrowsAsync<ArgumentException>(
                () => factory.OpenWorkspaceAsync("../evil"));
        }
        finally
        {
            if (Directory.Exists(dir))
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ── Casos extremos ────────────────────────────────────────────────────────

    [Fact]
    public async Task PreferIpv6_True_RoundTrip()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        Asset asset = await ctx.Store.AddAssetAsync(
            new AddAssetRequest { WorkspaceId = ctx.WorkspaceId, Name = "v6-host" });

        await ctx.Store.AddEndpointAsync(new Endpoint
        {
            Id = Guid.NewGuid().ToString("n"),
            AssetId = asset.Id,
            Protocol = "ssh",
            Ipv6 = "2001:db8::1",
            Port = 22,
            PreferIpv6 = true,
        });

        Asset? fetched = await ctx.Store.GetAssetAsync(asset.Id);
        Assert.True(fetched!.Endpoints[0].PreferIpv6);
        Assert.Equal("2001:db8::1", fetched.Endpoints[0].Ipv6);
    }

    [Fact]
    public async Task Tags_Empty_List_RoundTrip()
    {
        using var ctx = await StoreTestContext.CreateAsync();

        Asset asset = await ctx.Store.AddAssetAsync(
            new AddAssetRequest { WorkspaceId = ctx.WorkspaceId, Name = "no-tags", Tags = [] });

        Asset? fetched = await ctx.Store.GetAssetAsync(asset.Id);
        Assert.NotNull(fetched);
        Assert.Empty(fetched!.Tags);
    }

    // ── Acervo grande (endpoints buscados em lotes) ────────────────────────────

    [Fact]
    public async Task GetAssets_With_More_Than_900_Assets_Returns_All_With_Their_Endpoints()
    {
        using var ctx = await StoreTestContext.CreateAsync("ws-acervo");

        // 1200 ativos: acima do teto de 999 variáveis vinculadas do SQLite
        // (SQLITE_LIMIT_VARIABLE_NUMBER), portanto impossível de atender com um IN único — só passa
        // se a busca de endpoints for quebrada em lotes. O operador tem ~700 em produção e o
        // "Reenviar tudo para a nuvem" (CloudResyncService) varre o workspace inteiro.
        const int total = 1200;

        // Um ativo no MEIO do acervo (2º lote) recebe um endpoint extra: prova que a ordem dentro do
        // ativo (id ASC) sobrevive ao fatiamento, além da contagem.
        const int multiEndpointIndex = 700;

        await SeedAssetsWithEndpointsAsync(ctx, total, multiEndpointIndex);

        IReadOnlyList<Asset> assets = await ctx.Store.GetAssetsAsync(ctx.WorkspaceId);

        Assert.Equal(total, assets.Count);

        for (int i = 0; i < total; i++)
        {
            Asset a = assets[i];

            // Ordem global preservada (name ASC), como antes do fatiamento.
            Assert.Equal($"host-{i:0000}", a.Name);

            // O erro que o lote pode introduzir é entregar o endpoint do VIZINHO — o IP carrega o
            // índice do ativo, então a associação é verificada uma a uma.
            string expectedIp = $"10.{i / 256}.{i % 256}.1";
            if (i == multiEndpointIndex)
            {
                Assert.Equal(2, a.Endpoints.Count);
                Assert.Equal("10.255.255.254", a.Endpoints[0].Ipv4);   // id "…-0" vem antes de "…-1"
                Assert.Equal(expectedIp, a.Endpoints[1].Ipv4);
            }
            else
            {
                Assert.Single(a.Endpoints);
                Assert.Equal(expectedIp, a.Endpoints[0].Ipv4);
            }
        }
    }

    /// <summary>
    /// Semeia <paramref name="count"/> ativos com um endpoint cada, direto no banco e numa única
    /// transação. Passar pelo <c>AddAssetAsync</c>/<c>AddEndpointAsync</c> abriria 2×N conexões e
    /// gravaria 2×N entradas no outbox — dezenas de segundos para 1200 —, e o que este teste precisa
    /// provar é apenas a LEITURA em lotes.
    /// </summary>
    private static async Task SeedAssetsWithEndpointsAsync(
        StoreTestContext ctx, int count, int multiEndpointIndex)
    {
        using SqliteConnection conn = await ctx.Ctx.OpenConnectionAsync();
        await LocalSchema.EnsureAsync(conn);

        using SqliteTransaction tx = conn.BeginTransaction();

        using SqliteCommand assetCmd = conn.CreateCommand();
        assetCmd.Transaction = tx;
        assetCmd.CommandText = """
            INSERT INTO assets (id, workspace_id, group_id, name, tags_json, version)
            VALUES ($id, $wid, NULL, $name, '[]', 0)
            """;
        SqliteParameter assetId = assetCmd.Parameters.Add("$id", SqliteType.Text);
        assetCmd.Parameters.AddWithValue("$wid", ctx.WorkspaceId);
        SqliteParameter assetName = assetCmd.Parameters.Add("$name", SqliteType.Text);

        using SqliteCommand epCmd = conn.CreateCommand();
        epCmd.Transaction = tx;
        epCmd.CommandText = """
            INSERT INTO endpoints (id, asset_id, protocol, ipv4, port, prefer_ipv6, version)
            VALUES ($id, $aid, 'ssh', $ipv4, 22, 0, 0)
            """;
        SqliteParameter epId = epCmd.Parameters.Add("$id", SqliteType.Text);
        SqliteParameter epAssetId = epCmd.Parameters.Add("$aid", SqliteType.Text);
        SqliteParameter epIpv4 = epCmd.Parameters.Add("$ipv4", SqliteType.Text);

        for (int i = 0; i < count; i++)
        {
            string id = $"asset-{i:0000}";

            assetId.Value = id;
            assetName.Value = $"host-{i:0000}";
            await assetCmd.ExecuteNonQueryAsync();

            epId.Value = $"ep-{i:0000}-1";
            epAssetId.Value = id;
            epIpv4.Value = $"10.{i / 256}.{i % 256}.1";
            await epCmd.ExecuteNonQueryAsync();

            if (i == multiEndpointIndex)
            {
                epId.Value = $"ep-{i:0000}-0";
                epIpv4.Value = "10.255.255.254";
                await epCmd.ExecuteNonQueryAsync();
            }
        }

        tx.Commit();
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static bool IsSqlCipherAvailable()
    {
        try
        {
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA cipher_version";
            return cmd.ExecuteScalar() is not null;
        }
        catch
        {
            return false;
        }
    }
}
