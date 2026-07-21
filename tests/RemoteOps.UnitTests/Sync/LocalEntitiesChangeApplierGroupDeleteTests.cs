using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;

using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Um delete de grupo vindo do OUTRO device não pode cegar o operador.
///
/// <para>A UI navega EXCLUSIVAMENTE por grupo (<c>HostsViewModel.LoadHostsAsync</c> sempre chama
/// <c>GetAssetsAsync(ws, grupo.Id)</c>; a raiz lista só os cards de grupo). Um asset cujo
/// <c>group_id</c> aponta para um grupo apagado fica INVISÍVEL — o dado existe e o operador não
/// alcança. Com centenas de devices, isso é indistinguível de perda.</para>
///
/// <para>Por isso a MESMA invariante nas duas pontas: <b>grupo com hosts locais não é apagado, venha o
/// delete de onde vier</b>. O device que excluiu pela UI já garantiu "só vazio" na visão DELE; esta
/// guarda cobre o que ele não podia saber — hosts criados aqui e ainda não sincronizados. A divergência
/// se resolve sozinha: quando esses hosts subirem (ou forem movidos/excluídos), o grupo pode ir.</para>
/// </summary>
public sealed class LocalEntitiesChangeApplierGroupDeleteTests
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

    // Passa pelo JSON como o fio faz — senão o teste entregaria tipos CLR e não provaria nada.
    private static Dictionary<string, object?> Wire(Dictionary<string, object?> patch) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(patch))!;

    private static async Task<bool> ExistsAsync(SyncTestContext ctx, string table, string id)
    {
        using SqliteConnection conn = await ctx.Workspace.OpenConnectionAsync();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    [Fact]
    public async Task Delete_De_Grupo_Com_Hosts_Locais_NAO_Apaga_O_Grupo()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-grp-del-guard");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        await applier.ApplyAsync([Change("asset_group", "g1", "created", 0,
            Wire(new() { ["id"] = "g1", ["workspace_id"] = "ws-local", ["name"] = "Bordas" }))]);
        await applier.ApplyAsync([Change("asset", "a1", "created", 0,
            Wire(new() { ["id"] = "a1", ["workspace_id"] = "ws-local", ["group_id"] = "g1", ["name"] = "host-local" }))]);

        await applier.ApplyAsync([Change("asset_group", "g1", "deleted", 1, Wire(new()))]);

        Assert.True(await ExistsAsync(ctx, "asset_groups", "g1"),
            "grupo com host local não pode ser apagado — o host ficaria invisível");
        Assert.True(await ExistsAsync(ctx, "assets", "a1"),
            "o host jamais pode sumir por causa de um delete de grupo");
    }

    [Fact]
    public async Task Delete_De_Grupo_VAZIO_Apaga_Normalmente()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-grp-del-vazio");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        await applier.ApplyAsync([Change("asset_group", "g2", "created", 0,
            Wire(new() { ["id"] = "g2", ["workspace_id"] = "ws-local", ["name"] = "Vazio" }))]);

        await applier.ApplyAsync([Change("asset_group", "g2", "deleted", 1, Wire(new()))]);

        Assert.False(await ExistsAsync(ctx, "asset_groups", "g2"));
    }

    // A guarda é SÓ para grupo: o delete de asset tem de continuar cascateando os endpoints, senão
    // sobram endpoints órfãos que ninguém lista e ninguém apaga.
    [Fact]
    public async Task Delete_De_Asset_Continua_Cascateando_Endpoints()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-grp-del-cascata");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        await applier.ApplyAsync([Change("asset", "a9", "created", 0,
            Wire(new() { ["id"] = "a9", ["workspace_id"] = "ws-local", ["name"] = "host-9" }))]);
        await applier.ApplyAsync([Change("endpoint", "e9", "created", 0,
            Wire(new() { ["id"] = "e9", ["asset_id"] = "a9", ["protocol"] = "ssh" }))]);

        await applier.ApplyAsync([Change("asset", "a9", "deleted", 1, Wire(new()))]);

        Assert.False(await ExistsAsync(ctx, "assets", "a9"));
        Assert.False(await ExistsAsync(ctx, "endpoints", "e9"));
    }
}
