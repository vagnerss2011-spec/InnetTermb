using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Conflitos precisam ser INSPECIONÁVEIS e DISPENSÁVEIS, não só contáveis.
///
/// <para>O que motivou: em campo apareceu "Sincronizado (18 conflito(s))" numa máquina e 0 na outra,
/// sem nenhuma forma de ver o que eram nem de limpar. A contagem era <c>SELECT COUNT(*)</c> sobre uma
/// tabela que NUNCA era apagada — ou seja, um log histórico cumulativo apresentado ao operador como se
/// fosse trabalho pendente, inclusive com cicatrizes de bugs já corrigidos.</para>
/// </summary>
public sealed class SyncConflictInspectionTests
{
    private static ConflictDetail Conflict(string entityId, string reason = "version_mismatch")
        => new(ClientChangeId: $"cid-{entityId}", EntityType: "asset", EntityId: entityId,
               BaseVersion: 3, CurrentVersion: 5, Reason: reason);

    [Fact]
    public async Task Recorded_Conflicts_Can_Be_Listed_With_Detail()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-conf-list");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        await store.RecordConflictsAsync([Conflict("host-a"), Conflict("host-b", "secret_envelope")]);

        IReadOnlyList<StoredConflict> conflicts = await store.GetConflictsAsync(limit: 50);

        Assert.Equal(2, conflicts.Count);
        StoredConflict a = conflicts.Single(c => c.EntityId == "host-a");
        Assert.Equal("asset", a.EntityType);
        Assert.Equal("version_mismatch", a.Reason);
        Assert.Equal(3, a.BaseVersion);
        Assert.Equal(5, a.CurrentVersion);
        Assert.True(a.DetectedAt > DateTimeOffset.UtcNow.AddMinutes(-5), "o carimbo de tempo tem de vir preenchido");
    }

    [Fact]
    public async Task Newest_Conflicts_Come_First()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-conf-order");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        await store.RecordConflictsAsync([Conflict("antigo")]);
        await Task.Delay(15);
        await store.RecordConflictsAsync([Conflict("recente")]);

        IReadOnlyList<StoredConflict> conflicts = await store.GetConflictsAsync(limit: 50);

        Assert.Equal("recente", conflicts[0].EntityId);
    }

    [Fact]
    public async Task Limit_Is_Respected()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-conf-limit");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        await store.RecordConflictsAsync(Enumerable.Range(0, 30).Select(i => Conflict($"h{i}")).ToList());

        Assert.Equal(10, (await store.GetConflictsAsync(limit: 10)).Count);
    }

    // O coração da correção: dispensar limpa de verdade, então a contagem passa a significar
    // PENDÊNCIA. Antes, a tabela só crescia e o número nunca voltava a zero.
    [Fact]
    public async Task Clearing_Resets_The_Count_To_Zero()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-conf-clear");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);
        await store.RecordConflictsAsync([Conflict("h1"), Conflict("h2"), Conflict("h3")]);
        Assert.Equal(3, await store.GetConflictCountAsync());

        await store.ClearConflictsAsync();

        Assert.Equal(0, await store.GetConflictCountAsync());
        Assert.Empty(await store.GetConflictsAsync(limit: 50));
    }

    [Fact]
    public async Task Clearing_An_Empty_Table_Is_Harmless()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-conf-clear-empty");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        await store.ClearConflictsAsync(); // não pode lançar

        Assert.Equal(0, await store.GetConflictCountAsync());
    }

    [Fact]
    public async Task Conflicts_After_Clearing_Are_Recorded_Again()
    {
        // Dispensar é "já vi", não "desliga o aviso": um conflito NOVO tem de voltar a aparecer.
        using var ctx = await SyncTestContext.CreateAsync("ws-conf-again");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);
        await store.RecordConflictsAsync([Conflict("h1")]);
        await store.ClearConflictsAsync();

        await store.RecordConflictsAsync([Conflict("h2")]);

        Assert.Equal(1, await store.GetConflictCountAsync());
        Assert.Equal("h2", (await store.GetConflictsAsync(limit: 50))[0].EntityId);
    }

    [Fact]
    public async Task Listing_On_A_Fresh_Database_Is_Empty_Not_Broken()
    {
        // Banco de versão anterior (sem as colunas novas) abre e responde vazio — migração aditiva.
        using var ctx = await SyncTestContext.CreateAsync("ws-conf-fresh");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        Assert.Empty(await store.GetConflictsAsync(limit: 50));
        Assert.Equal(0, await store.GetConflictCountAsync());
    }
}
