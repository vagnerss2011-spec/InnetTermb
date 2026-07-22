using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using RemoteOps.Contracts.Sync;
using RemoteOps.Desktop.Account;
using RemoteOps.Sync;
using RemoteOps.Sync.Remote;
using RemoteOps.UnitTests.Sync;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Account;

/// <summary>
/// <b>A fila que ficou parada no cofre que não está aberto.</b>
///
/// <para>Desde o 1j existe <b>um banco por escopo</b> (<c>sync-local.db</c> para o pessoal,
/// <c>sync-team-{W}.db</c> para cada time). O outbox mora no banco, e o sync da sessão só drena o
/// banco DAQUELA sessão. Consequência real, e é dívida assumida pelo estágio anterior: o operador
/// edita um cliente no cofre pessoal, fecha, abre no time — e as edições do pessoal ficam
/// <b>paradas</b> até ele reabrir naquele escopo. Nada na tela diz isso. Ele acha que sincronizou.</para>
///
/// <para>Esta é exatamente a queixa que ele já abriu duas vezes ("as credenciais não sincronizaram"),
/// e é a classe de defeito nº 1 desta base: trabalho que some sem uma linha de erro. A sonda existe
/// para transformar isso em algo que alguém enxerga.</para>
/// </summary>
public sealed class OtherVaultOutboxProbeTests : IDisposable
{
    private const string WorkspaceDoTime = "5d2f8a10-0000-4000-8000-0000000000ee";
    private const string ServerWorkspace = "ws-local";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"remoteops-outbox-probe-{Guid.NewGuid():n}");

    private readonly FakeCredentialVault _vault = new();

    public OtherVaultOutboxProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Limpeza best-effort: um arquivo preso pelo SQLite não pode derrubar o teste.
        }
    }

    private LocalSyncClientFactory NewFactory() => new(_vault, _dir);

    /// <summary>Cria o banco daquele escopo e enfileira <paramref name="pendencias"/> edições.</summary>
    private async Task<WorkspaceContext> SemearAsync(string dbName, int pendencias)
    {
        WorkspaceContext ctx = await NewFactory().OpenWorkspaceAsync(dbName, "local");

        var mudancas = new List<SyncChange>();
        for (int i = 0; i < pendencias; i++)
        {
            mudancas.Add(new SyncChange
            {
                ClientChangeId = $"{dbName}-{i}",
                EntityType = "asset",
                EntityId = $"host-{i}",
                Operation = "updated",
                BaseVersion = 1,
                Patch = new Dictionary<string, object?> { ["name"] = $"host-{i}" },
            });
        }

        if (mudancas.Count > 0)
        {
            await ctx.SyncClient.PushAsync(mudancas);
        }

        return ctx;
    }

    /// <summary>
    /// <b>O caso do operador, literalmente.</b> Ele editou no cofre PESSOAL e abriu no cofre do TIME:
    /// a sonda encontra as edições paradas lá, com a contagem certa — e não olha o banco desta
    /// sessão, que o sync desta sessão já drena.
    /// </summary>
    [Fact]
    public async Task EditouNoPESSOAL_AbriuNoTIME_ASondaACHA_AFilaParada()
    {
        await SemearAsync("local", pendencias: 3);
        await SemearAsync(AppRuntime.TeamDbName(WorkspaceDoTime), pendencias: 7);

        OtherVaultOutboxReport report = await new OtherVaultOutboxProbe(NewFactory(), _dir)
            .ScanAsync(AppRuntime.TeamDbName(WorkspaceDoTime));

        Assert.Equal(3, report.PendingPersonal);
        Assert.Equal(0, report.PendingTeam);
        Assert.False(report.CheckFailed);
    }

    /// <summary>O caminho inverso: a sessão é a pessoal e o que ficou parado é o do TIME.</summary>
    [Fact]
    public async Task EditouNoTIME_AbriuNoPESSOAL_ASondaACHA_AFilaParada()
    {
        await SemearAsync("local", pendencias: 3);
        await SemearAsync(AppRuntime.TeamDbName(WorkspaceDoTime), pendencias: 7);

        OtherVaultOutboxReport report = await new OtherVaultOutboxProbe(NewFactory(), _dir)
            .ScanAsync("local");

        Assert.Equal(0, report.PendingPersonal);
        Assert.Equal(7, report.PendingTeam);
        Assert.False(report.CheckFailed);
    }

    /// <summary>
    /// <b>A metade que impede o alarme falso.</b> O que JÁ SUBIU não é pendência: o cursor do outbox
    /// é o que separa "esperando" de "entregue". Sem esta metade, o aviso apareceria para sempre em
    /// qualquer máquina que um dia tenha editado algo — e aviso permanente ninguém lê.
    /// </summary>
    [Fact]
    public async Task OQueJaSUBIU_NaoConta_ComoPendencia()
    {
        WorkspaceContext pessoal = await SemearAsync("local", pendencias: 4);
        await new SqliteSyncMetadataStore(pessoal).SaveOutboxCursorAsync(ServerWorkspace, 4);

        OtherVaultOutboxReport report = await new OtherVaultOutboxProbe(NewFactory(), _dir)
            .ScanAsync(AppRuntime.TeamDbName(WorkspaceDoTime));

        Assert.Equal(0, report.PendingPersonal);
        Assert.False(report.CheckFailed);
    }

    /// <summary>Drenado pela metade: só o que está DEPOIS do cursor é pendência.</summary>
    [Fact]
    public async Task DrenadoPelaMetade_ContaSoOQueFALTA()
    {
        WorkspaceContext pessoal = await SemearAsync("local", pendencias: 5);
        await new SqliteSyncMetadataStore(pessoal).SaveOutboxCursorAsync(ServerWorkspace, 2);

        OtherVaultOutboxReport report = await new OtherVaultOutboxProbe(NewFactory(), _dir)
            .ScanAsync(AppRuntime.TeamDbName(WorkspaceDoTime));

        Assert.Equal(3, report.PendingPersonal);
    }

    /// <summary>
    /// <b>Máquina de um cofre só</b> (a maioria da frota): não há outro escopo, então não há aviso —
    /// e nem uma leitura de banco a mais.
    /// </summary>
    [Fact]
    public async Task SoUmCofreNaMaquina_NaoHaNadaAAvisar()
    {
        await SemearAsync("local", pendencias: 9);

        OtherVaultOutboxReport report = await new OtherVaultOutboxProbe(NewFactory(), _dir)
            .ScanAsync("local");

        Assert.Equal(0, report.Total);
        Assert.False(report.CheckFailed);
    }

    /// <summary>
    /// <b>"Não deu para ler" NUNCA vira "não há nada".</b> Um banco sem o <c>.keyref</c> não pode ser
    /// aberto sem CRIAR outra chave — e criar chave por cima do banco de senhas do operador seria
    /// trocar um aviso ausente por perda de dado. A sonda recusa aquele escopo e <b>marca</b> que
    /// houve escopo não verificado; devolver silêncio aqui seria a falha muda de sempre.
    /// </summary>
    [Fact]
    public async Task BancoIlegivel_MARCA_NaoVerificado_EmVezDeAfirmarZero()
    {
        await SemearAsync("local", pendencias: 2);
        string keyRef = Path.Combine(_dir, "sync-local.keyref");
        File.Delete(keyRef);

        OtherVaultOutboxReport report = await new OtherVaultOutboxProbe(NewFactory(), _dir)
            .ScanAsync(AppRuntime.TeamDbName(WorkspaceDoTime));

        Assert.True(report.CheckFailed);
        Assert.Equal(0, report.PendingPersonal);

        // ⚠️ E — o que mais importa — a sonda NÃO plantou uma chave nova. O caminho normal de
        // abertura é um GetOrCreate: sem `.keyref` legível ele sorteia outra chave e regrava o
        // arquivo, e o banco com os ~700 equipamentos do operador ficaria cifrado com a chave que
        // acabou de ser jogada fora. Um aviso não pode custar isso.
        Assert.False(File.Exists(keyRef));
    }
}
