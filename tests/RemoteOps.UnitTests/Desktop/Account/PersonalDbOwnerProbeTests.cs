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
/// <b>De quem é o <c>sync-local.db</c> desta máquina — perguntado ao próprio banco.</b>
///
/// <para>A regra 5 do <c>SessionVaultScopeResolver</c> adotava o workspace da vez como dono do banco
/// pessoal só porque o arquivo existia. Isso era correto enquanto não havia como criar workspace de
/// time; nesta fatia passou a significar que o operador abrindo o TIME amarrava o banco com os ~700
/// clientes dele ao workspace dos colegas — offline, sem sonda e sem uma linha na tela.</para>
///
/// <para>A evidência que faltava estava dentro do banco: <c>sync_cursor.workspace_id</c> é o
/// workspace de SERVIDOR contra o qual ele vinha sincronizando.</para>
/// </summary>
public sealed class PersonalDbOwnerProbeTests : IDisposable
{
    private const string WorkspacePessoal = "8f3b6f4a-0000-4000-8000-000000000001";
    private const string PersonalDb = "local";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"remoteops-db-owner-{Guid.NewGuid():n}");

    private readonly FakeCredentialVault _vault = new();

    public PersonalDbOwnerProbeTests() => Directory.CreateDirectory(_dir);

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

    private PersonalDbOwnerProbe NewProbe() => new(NewFactory(), PersonalDb);

    /// <summary>
    /// <b>O caso do operador.</b> O banco dele vinha sincronizando com o workspace pessoal, e é isso
    /// que ele responde — o que basta para o resolvedor recusar adotar qualquer OUTRO workspace como
    /// dono dele.
    /// </summary>
    [Fact]
    public async Task BancoQueJaSINCRONIZOU_DIZ_ComQualWorkspaceFoi()
    {
        WorkspaceContext ctx = await NewFactory().OpenWorkspaceAsync(PersonalDb, "local");
        await new SqliteSyncMetadataStore(ctx).SaveServerCursorAsync(WorkspacePessoal, 4210);

        IReadOnlyList<string>? workspaces = await NewProbe().ReadSyncedWorkspacesAsync();

        Assert.NotNull(workspaces);
        Assert.Equal([WorkspacePessoal], workspaces);
    }

    /// <summary>
    /// <b>Lista VAZIA é uma medição; <c>null</c> é "não deu para olhar".</b> Colapsar as duas seria
    /// exatamente como um erro vira estado vazio — e aqui o estado vazio autorizaria uma adoção que
    /// ninguém mediu. O banco existe, abriu e nunca sincronizou: isso é um fato, e é diferente de
    /// não ter conseguido ler.
    /// </summary>
    [Fact]
    public async Task BancoQueNUNCASincronizou_DevolveVAZIO_ENaoNULL()
    {
        // Ele foi USADO (tem equipamentos na fila, o esquema inteiro está lá) e nunca chegou a
        // sincronizar com servidor nenhum: é o segundo computador do operador antes da conta.
        WorkspaceContext ctx = await NewFactory().OpenWorkspaceAsync(PersonalDb, "local");
        await ctx.SyncClient.PushAsync([new SyncChange
        {
            ClientChangeId = "c1",
            EntityType = "asset",
            EntityId = "host-1",
            Operation = "created",
            BaseVersion = 0,
            Patch = new Dictionary<string, object?> { ["name"] = "host-1" },
        }]);

        IReadOnlyList<string>? workspaces = await NewProbe().ReadSyncedWorkspacesAsync();

        Assert.NotNull(workspaces);
        Assert.Empty(workspaces);
    }

    /// <summary>
    /// Banco recém-criado, ainda sem a tabela de cursores: ele ABRIU, então a resposta é "nunca
    /// sincronizou" (vazio) e não "não deu para ler" (<c>null</c>). A diferença decide se o
    /// resolvedor pode adotar sem rede.
    /// </summary>
    [Fact]
    public async Task BancoSemATabelaDeCursores_DevolveVAZIO_ENaoNULL()
    {
        WorkspaceContext ctx = await NewFactory().OpenWorkspaceAsync(PersonalDb, "local");
        (await ctx.OpenConnectionAsync()).Dispose(); // materializa o arquivo, sem criar esquema

        IReadOnlyList<string>? workspaces = await NewProbe().ReadSyncedWorkspacesAsync();

        Assert.NotNull(workspaces);
        Assert.Empty(workspaces);
    }

    /// <summary>
    /// <b>Banco ilegível devolve <c>null</c> — e a sondagem NÃO planta uma chave nova.</b> O caminho
    /// normal de abertura é um <c>GetOrCreate</c>: sem <c>.keyref</c> legível ele sorteia outra chave
    /// e regrava o arquivo, e o banco com os ~700 equipamentos ficaria cifrado com a chave que
    /// acabou de ser jogada fora. Esta sondagem roda ANTES de o app sequer decidir qual banco vai
    /// abrir — destruir a chave de um banco que esta sessão talvez nem use seria trocar um vazamento
    /// por perda de dado.
    /// </summary>
    [Fact]
    public async Task BancoSemKeyref_DevolveNULL_ENaoRECRIA_AChave()
    {
        WorkspaceContext ctx = await NewFactory().OpenWorkspaceAsync(PersonalDb, "local");
        await new SqliteSyncMetadataStore(ctx).SaveServerCursorAsync(WorkspacePessoal, 1);

        string keyRef = Path.Combine(_dir, "sync-local.keyref");
        File.Delete(keyRef);

        Assert.Null(await NewProbe().ReadSyncedWorkspacesAsync());
        Assert.False(File.Exists(keyRef));
    }

    /// <summary>Sem banco pessoal não há o que ler — e "não li" nunca é "não há".</summary>
    [Fact]
    public async Task SemBancoPessoal_DevolveNULL()
    {
        Assert.Null(await NewProbe().ReadSyncedWorkspacesAsync());
    }

    /// <summary>
    /// Arquivo que tem o nome do banco mas não é um banco (o disco do operador depois de uma
    /// restauração pela metade). Devolve <c>null</c> em vez de estourar: o boot inteiro depende
    /// desta chamada, e uma sondagem opcional não pode ser o motivo de o app não abrir.
    /// </summary>
    [Fact]
    public async Task ArquivoQueNaoEBanco_DevolveNULL_EmVezDeEstourar()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "sync-local.db"), "isto não é um banco");
        await File.WriteAllTextAsync(Path.Combine(_dir, "sync-local.keyref"), "envelope-inexistente");

        Assert.Null(await NewProbe().ReadSyncedWorkspacesAsync());
    }
}
