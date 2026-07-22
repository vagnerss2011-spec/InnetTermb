using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Security.Account;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Account;

/// <summary>
/// Em qual cofre esta sessão do app escreve — decidido UMA vez, no boot, e nunca no meio.
///
/// <para><b>A regra que manda:</b> a resposta sai do DISCO. Rede só na primeiríssima abertura de um
/// workspace que esta máquina nunca viu, e nunca mais depois. Um escopo que dependesse de rede
/// mudaria de valor conforme o sinal do operador — e mudar de cofre por causa do sinal é escrever no
/// banco errado sem que nada apareça na tela.</para>
///
/// <para><b>E a ordem das regras é a guarda:</b> "este workspace é o dono do meu banco pessoal"
/// vem ANTES de qualquer coisa relacionada a time. É isso que impede o app do operador de mudar de
/// modo sozinho por causa de um blob de chave que o build 1e gravou no workspace pessoal dele no
/// instante em que ele clicou "convidar".</para>
/// </summary>
public sealed class SessionVaultScopeTests : IDisposable
{
    private const string ServerWorkspace = "8f3b6f4a-0000-4000-8000-000000000001";
    private const string OutroWorkspace = "11111111-2222-4333-8444-555555555555";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "remoteops-scope-" + Guid.NewGuid().ToString("n"));

    private readonly InMemoryWorkspaceKeyStore _store = new();
    private readonly byte[] _amk = RandomNumberGenerator.GetBytes(32);

    public SessionVaultScopeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* limpeza best-effort: um handle preso não pode derrubar o teste */ }
    }

    private string OwnerPath => Path.Combine(_dir, "sync-local.owner");

    private string PersonalDbPath => Path.Combine(_dir, "sync-local.db");

    private SessionVaultScopeResolver Resolver(WkWorkspaceKeyRing ring) =>
        new(ring, _store, OwnerPath, PersonalDbPath);

    private WkWorkspaceKeyRing Ring() => TeamKeyRingFactory.New(_store, _amk);

    /// <summary>
    /// <b>O teste mais importante da fatia.</b> O operador que clicou "convidar" no build 1e tem, no
    /// disco, uma WK de time gravada sob o PRÓPRIO workspace pessoal — e continua abrindo o cofre
    /// pessoal. A regra "sou o dono do banco local" é avaliada ANTES da regra da chave; inverter a
    /// ordem faria o app dele trocar de cofre sozinho, e os ~700 equipamentos sumiriam da tela sem
    /// um único erro.
    /// </summary>
    [Fact]
    public async Task WorkspaceDonoDoBancoLocal_SempreDaPessoal_MesmoComChaveDeTimeNoDisco()
    {
        using WkWorkspaceKeyRing ring = Ring();

        // Primeira abertura depois do upgrade: o banco local é deste workspace, por construção.
        SessionVaultScope primeira = await Resolver(ring).ResolveAsync(ServerWorkspace, Probe(false));
        Assert.Equal(SessionVaultKind.Personal, primeira.Kind);
        Assert.True(File.Exists(OwnerPath));

        // Agora a WK do time existe em disco PARA ESTE MESMO workspace (o que o 1e gravava ao
        // clicar "convidar"). Nada muda: o escopo continua pessoal.
        (await ring.MintWorkspaceKeyAsync(AppRuntime.TeamVaultWorkspace(ServerWorkspace))).Dispose();

        SessionVaultScope segunda = await Resolver(ring).ResolveAsync(ServerWorkspace, Probe(true));

        Assert.Equal(SessionVaultKind.Personal, segunda.Kind);
        Assert.Equal(AppRuntime.CredentialsWorkspace, segunda.VaultWorkspaceId);
        Assert.Equal(AppRuntime.DbWorkspace, segunda.DbName);
        Assert.Equal(VaultAlgorithms.AmkRootedV1, segunda.VaultAlgorithm);
    }

    /// <summary>
    /// Workspace de TIME (não é o dono do banco local) com a chave em disco: cofre do time, banco do
    /// time, raiz WK. Nenhuma ida à rede.
    /// </summary>
    [Fact]
    public async Task WorkspaceDeTimeComChave_AbreOCofreDoTime_SemRede()
    {
        using WkWorkspaceKeyRing ring = Ring();
        await Resolver(ring).ResolveAsync(ServerWorkspace, Probe(false)); // fixa o dono do banco local
        (await ring.MintWorkspaceKeyAsync(AppRuntime.TeamVaultWorkspace(OutroWorkspace))).Dispose();

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(OutroWorkspace, ProbeThatExplodes());

        Assert.Equal(SessionVaultKind.Team, escopo.Kind);
        Assert.Equal("time:" + OutroWorkspace, escopo.VaultWorkspaceId);
        Assert.Equal("team-" + OutroWorkspace, escopo.DbName);
        Assert.Equal(VaultAlgorithms.WkRootedV1, escopo.VaultAlgorithm);
    }

    /// <summary>
    /// <b>Apagar só o blob da chave não devolve a sessão a "pessoal".</b> O marcador de raiz
    /// (gravado na MESMA operação em que a WK aterrissa) sobrevive, e o escopo vira
    /// <see cref="SessionVaultKind.TeamWithoutKey"/> — fail-closed, com o cofre recusando alto toda
    /// operação de senha. Cair em "pessoal" aqui faria o app abrir o banco pessoal sincronizando
    /// contra o workspace do time: os equipamentos do operador subiriam para o cofre de outra gente.
    /// </summary>
    [Fact]
    public async Task MarcadorSemChave_NaoCaiEmPessoal_CaiEmTeamSemChave()
    {
        using WkWorkspaceKeyRing ring = Ring();
        await Resolver(ring).ResolveAsync(ServerWorkspace, Probe(false));

        // A chave já aterrissou aqui um dia (marcador gravado)…
        await _store.SaveKeyRootingAsync(
            AppRuntime.TeamVaultWorkspace(OutroWorkspace), VaultKeyRooting.WkRandom);

        // …mas o blob não está mais no disco.
        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(OutroWorkspace, ProbeThatExplodes());

        Assert.Equal(SessionVaultKind.TeamWithoutKey, escopo.Kind);
        Assert.Equal("time:" + OutroWorkspace, escopo.VaultWorkspaceId);
        Assert.Equal(VaultAlgorithms.WkRootedV1, escopo.VaultAlgorithm);
    }

    /// <summary>
    /// <b>"Não sei" nunca vira "escreve no banco pessoal".</b> Workspace desconhecido e sem rede:
    /// o app RECUSA a ativação, com texto escrito. Assumir pessoal abriria <c>sync-local.db</c> e
    /// <c>ws-local</c> sincronizando contra um workspace de servidor que não é dono deles — os hosts
    /// pessoais do operador subiriam para o workspace de outra pessoa.
    /// </summary>
    [Fact]
    public async Task WorkspaceDesconhecidoESemRede_RECUSA_NaoAssumeNada()
    {
        using WkWorkspaceKeyRing ring = Ring();
        await Resolver(ring).ResolveAsync(ServerWorkspace, Probe(false));

        var erro = await Assert.ThrowsAsync<SessionVaultScopeException>(
            () => Resolver(ring).ResolveAsync(OutroWorkspace, ProbeThatFailsOffline()));

        Assert.Contains("Conecte-se", erro.Message, StringComparison.Ordinal);
        Assert.Contains("cofre pessoal", erro.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sonda respondeu "não é de time" para um workspace que também NÃO é o dono do banco local: é o
    /// cofre pessoal de OUTRA conta/instalação. Recusar é a única saída honesta — abrir daria o
    /// mesmo estrago do teste acima, só que com a certeza no lugar da dúvida.
    /// </summary>
    [Fact]
    public async Task WorkspacePessoalDeOUTRAInstalacao_RECUSA()
    {
        using WkWorkspaceKeyRing ring = Ring();
        await Resolver(ring).ResolveAsync(ServerWorkspace, Probe(false));

        var erro = await Assert.ThrowsAsync<SessionVaultScopeException>(
            () => Resolver(ring).ResolveAsync(OutroWorkspace, Probe(false)));

        Assert.Contains("cofre pessoal", erro.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A sonda online roda no MÁXIMO uma vez por workspace por máquina: ela restaura a chave na
    /// mesma ida, e a partir daí o disco responde. Um round-trip por boot seria um imposto de rede
    /// numa decisão que precisa valer offline.
    /// </summary>
    [Fact]
    public async Task SondaOnline_RodaUmaVezSo_EDepoisODiscoResponde()
    {
        using WkWorkspaceKeyRing ring = Ring();
        await Resolver(ring).ResolveAsync(ServerWorkspace, Probe(false));

        int idas = 0;
        Task<bool> RestauraNaPrimeiraIda(string ws, CancellationToken ct)
        {
            idas++;
            return ring.MintWorkspaceKeyAsync(AppRuntime.TeamVaultWorkspace(ws), ct)
                .ContinueWith(t => { t.Result.Dispose(); return true; }, TaskScheduler.Default);
        }

        Assert.Equal(
            SessionVaultKind.Team,
            (await Resolver(ring).ResolveAsync(OutroWorkspace, RestauraNaPrimeiraIda)).Kind);
        Assert.Equal(1, idas);

        Assert.Equal(
            SessionVaultKind.Team,
            (await Resolver(ring).ResolveAsync(OutroWorkspace, RestauraNaPrimeiraIda)).Kind);
        Assert.Equal(1, idas);
    }

    /// <summary>
    /// <b>O nome do banco NUNCA tem dois-pontos.</b> <c>LocalSyncClientFactory.OpenWorkspaceAsync</c>
    /// recusa <c>Path.GetInvalidFileNameChars()</c>, e ':' é um deles no Windows — passar
    /// <c>time:{W}</c> como nome de arquivo derrubaria o <c>OnStartup</c> com "Não foi possível
    /// iniciar". Duas nomenclaturas, de propósito: <c>time:{W}</c> é COFRE, <c>team-{W}</c> é ARQUIVO.
    /// </summary>
    [Fact]
    public void DbName_NuncaContemDoisPontos()
    {
        SessionVaultScope time = SessionVaultScope.Team(ServerWorkspace, temChave: true);

        Assert.DoesNotContain(':', time.DbName);
        Assert.Equal(-1, time.DbName.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.NotEqual(time.VaultWorkspaceId, time.DbName);

        Assert.Equal(-1, SessionVaultScope.Personal.DbName.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    /// <summary>
    /// <b>Sem marcador, a evidência de time em disco fala mais alto que a adoção.</b> O colega cujo
    /// <c>sync-local.owner</c> falhou ao ser escrito (ou foi apagado por uma limpeza) e que já tem a
    /// WK do time no disco NÃO pode ter o banco pessoal adotado pelo workspace do TIME: isso
    /// amarraria <c>sync-local.db</c> ao workspace dos colegas — os hosts pessoais dele subiriam
    /// para o time e os do time desceriam para o banco pessoal, sem um único erro na tela. E o
    /// marcador ficaria envenenado com o GUID do time, para sempre.
    /// </summary>
    [Fact]
    public async Task SemMarcador_ComChaveDeTimeNoDisco_AbreOTime_ENaoAdotaODono()
    {
        using WkWorkspaceKeyRing ring = Ring();
        (await ring.MintWorkspaceKeyAsync(AppRuntime.TeamVaultWorkspace(OutroWorkspace))).Dispose();

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(OutroWorkspace, ProbeThatExplodes());

        Assert.Equal(SessionVaultKind.Team, escopo.Kind);
        Assert.Equal("time:" + OutroWorkspace, escopo.VaultWorkspaceId);
        Assert.False(File.Exists(OwnerPath)); // um workspace de TIME nunca vira dono do banco pessoal
    }

    /// <summary>
    /// <b>Máquina NOVA (sem banco pessoal) escolhendo o TIME primeiro.</b> O segundo PC do colega:
    /// login, chooser com dois cofres, ele escolhe o do time. Adotar esse workspace como dono do
    /// banco pessoal (o comportamento antigo da regra 1) abriria <c>sync-local.db</c> sincronizando
    /// contra o workspace do TIME — contaminação de metadados nos dois sentidos, em silêncio. A
    /// sonda decide (e restaura a chave na mesma ida); o dono só é adotado quando a resposta é
    /// "não é de time".
    /// </summary>
    [Fact]
    public async Task MaquinaNova_EscolhendoOTimePrimeiro_ASondaDecide_EODonoNaoEAdotado()
    {
        using WkWorkspaceKeyRing ring = Ring();

        Task<bool> SondaQueRestaura(string ws, CancellationToken ct)
            => ring.MintWorkspaceKeyAsync(AppRuntime.TeamVaultWorkspace(ws), ct)
                .ContinueWith(t => { t.Result.Dispose(); return true; }, TaskScheduler.Default);

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(OutroWorkspace, SondaQueRestaura);

        Assert.Equal(SessionVaultKind.Team, escopo.Kind);
        Assert.False(File.Exists(OwnerPath));
    }

    /// <summary>
    /// Máquina nova, sem banco pessoal, SEM rede: recusa. O antigo caminho respondia "pessoal" e
    /// gravava o dono — transformando "não sei" na pior afirmação possível. A primeira abertura de
    /// uma máquina nova acabou de exigir a rede para o próprio login, então recusar aqui não
    /// tranca ninguém que já conseguia trabalhar.
    /// </summary>
    [Fact]
    public async Task MaquinaNova_SemRede_RECUSA_NaoAdotaODono()
    {
        using WkWorkspaceKeyRing ring = Ring();

        await Assert.ThrowsAsync<SessionVaultScopeException>(
            () => Resolver(ring).ResolveAsync(OutroWorkspace, ProbeThatFailsOffline()));

        Assert.False(File.Exists(OwnerPath));
    }

    /// <summary>
    /// <b>Marcador ILEGÍVEL não é marcador AUSENTE.</b> Tratar falha de leitura como "primeira
    /// ativação" regrava o dono com o workspace da vez — se o operador estiver abrindo o do time,
    /// o banco pessoal muda de dono em silêncio e o app passa a abri-lo como pessoal do TIME para
    /// sempre. "Não sei quem é o dono" tem que recusar alto; a próxima abertura, com o arquivo
    /// legível de novo, volta ao normal.
    /// </summary>
    [Fact]
    public async Task MarcadorIlegivel_RECUSA_EmVezDeReadotarComOWorkspaceDaVez()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(OwnerPath, ServerWorkspace);

        // O banco pessoal EXISTE: é a máquina do operador, no boot de todo dia. Sem esta linha o
        // teste passaria por outro caminho (a recusa da máquina nova) e deixaria de provar a única
        // coisa que importa aqui: ilegível ≠ ausente. Com o marcador colapsado em "ausente", a
        // regra do upgrade adotaria o workspace da vez como dono — sem exceção nenhuma.
        File.WriteAllText(PersonalDbPath, "banco antigo");

        using (new FileStream(OwnerPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<SessionVaultScopeException>(
                () => Resolver(ring).ResolveAsync(OutroWorkspace, ProbeThatExplodes()));
        }

        // O conteúdo sobreviveu intacto: o dono continua sendo o workspace original.
        Assert.Equal(ServerWorkspace, File.ReadAllText(OwnerPath).Trim());
    }

    /// <summary>
    /// O caso do UPGRADE de toda a frota: banco pessoal já existe, marcador ainda não. Continua
    /// pessoal, continua offline (a sonda não pode nem ser tocada) — byte a byte o app de hoje.
    /// É esta regra que impede a correção da máquina nova de virar um imposto de rede no boot de
    /// quem já usava o RemoteOps.
    /// </summary>
    [Fact]
    public async Task UpgradeComBancoLocal_SemMarcador_SeguePessoal_SemSonda()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco antigo");

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(ServerWorkspace, ProbeThatExplodes());

        Assert.Equal(SessionVaultKind.Personal, escopo.Kind);
        Assert.Equal(ServerWorkspace, File.ReadAllText(OwnerPath).Trim());
    }

    /// <summary>
    /// <b>O buraco por onde a suíte passava de raspão.</b> Havia teste para "sem banco pessoal +
    /// escolhendo o time" e para "com banco pessoal + escolhendo o pessoal"; a combinação que faltava
    /// é justamente a do operador: a máquina com os ~700 em <c>sync-local.db</c>, sem marcador de
    /// dono e sem chave de time no disco, abrindo o TIME.
    ///
    /// <para>Adotar aqui grava <c>sync-local.owner</c> = <b>GUID DO TIME</b>, offline e sem uma linha
    /// na tela. A partir daí o banco dos ~700 fica amarrado ao workspace do time, o cursor daquele
    /// workspace é 0 e o <c>local_outbox</c> é append-only — ou seja, um "Reenviar tudo" empurra o
    /// acervo inteiro do operador para os colegas. E o cofre pessoal dele passa a ser recusado na
    /// abertura seguinte (regra 6), acusando-o de abrir cofre de outra instalação.</para>
    /// </summary>
    [Fact]
    public async Task ComBancoPessoal_EscolhendoOTIME_NaoADOTA_OWorkspaceDoTIME()
    {
        using WkWorkspaceKeyRing ring = Ring();

        // O banco com os ~700 está aqui. O que ele diz de si mesmo é que vinha sincronizando com o
        // workspace PESSOAL — nunca com este que está sendo aberto.
        File.WriteAllText(PersonalDbPath, "banco com os ~700");

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(
            OutroWorkspace, SondaQueRestaura(ring), BancoSincronizadoCom(ServerWorkspace));

        Assert.Equal(SessionVaultKind.Team, escopo.Kind);
        Assert.Equal("time:" + OutroWorkspace, escopo.VaultWorkspaceId);

        // O que mais importa: o banco dos ~700 NÃO mudou de dono.
        Assert.False(File.Exists(OwnerPath));
    }

    /// <summary>
    /// <b>A evidência positiva do banco adota SEM REDE</b> — e é ela que segura o boot diário do
    /// operador que já tem um time. Sem esta metade, a correção acima viraria um imposto de rede
    /// (ou, pior, uma sonda respondendo "não é de time" sobre o cofre pessoal dele) toda vez que o
    /// marcador ainda não existisse.
    /// </summary>
    [Fact]
    public async Task BancoQueSincronizouComESTEWorkspace_ADOTA_SemSonda_MesmoComTimeNaConta()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco com os ~700");

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(
            ServerWorkspace,
            ProbeThatExplodes(),
            BancoSincronizadoCom(ServerWorkspace),
            accountWorkspaceCount: 2);

        Assert.Equal(SessionVaultKind.Personal, escopo.Kind);
        Assert.Equal(ServerWorkspace, File.ReadAllText(OwnerPath).Trim());
    }

    /// <summary>
    /// <b>A segunda rede: banco que nunca sincronizou com ninguém não afirma nada.</b> É o segundo
    /// computador do operador, usado local antes da conta: <c>sync-local.db</c> existe e não tem o
    /// que dizer sobre si. Quem tem 2+ workspaces tem um time — e acabou de usar a rede para o
    /// próprio login, que é de onde saiu o chooser onde ele escolheu o time. A sonda decide.
    /// </summary>
    [Fact]
    public async Task BancoSemEvidencia_ContaComTIME_NaoADOTA_ASondaDecide()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco local, ainda sem sync");

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(
            OutroWorkspace,
            SondaQueRestaura(ring),
            BancoQueNuncaSincronizou(),
            accountWorkspaceCount: 2);

        Assert.Equal(SessionVaultKind.Team, escopo.Kind);
        Assert.False(File.Exists(OwnerPath));
    }

    /// <summary>
    /// A metade que impede "recusar tudo": a MESMA falta de evidência, numa conta de um workspace
    /// só, adota offline. Não existe outro dono possível ali, e exigir rede seria o imposto de boot
    /// que a regra 5 existe para não cobrar da frota inteira.
    /// </summary>
    [Fact]
    public async Task BancoSemEvidencia_ContaDeUmWorkspaceSO_ADOTA_SemSonda()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco local, ainda sem sync");

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(
            ServerWorkspace,
            ProbeThatExplodes(),
            BancoQueNuncaSincronizou(),
            accountWorkspaceCount: 1);

        Assert.Equal(SessionVaultKind.Personal, escopo.Kind);
        Assert.Equal(ServerWorkspace, File.ReadAllText(OwnerPath).Trim());
    }

    /// <summary>
    /// Evidência CONTRÁRIA e a sonda dizendo "não é de time": o banco desta máquina é de outro
    /// workspace e este aqui é o cofre pessoal de outra instalação. Recusa alta — adotar misturaria
    /// os dois acervos contra uma evidência medida, que é pior do que fazê-lo na ausência dela.
    /// </summary>
    [Fact]
    public async Task EvidenciaCONTRARIA_ESondaDizQueNaoEDeTime_RECUSA()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco com os ~700");

        var erro = await Assert.ThrowsAsync<SessionVaultScopeException>(
            () => Resolver(ring).ResolveAsync(
                OutroWorkspace, Probe(false), BancoSincronizadoCom(ServerWorkspace)));

        Assert.Contains("outra instalação", erro.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(OwnerPath));
    }

    /// <summary>
    /// A leitura do banco é evidência, nunca um modo de falha novo: se ela estourar, o boot segue
    /// com "não sei" — o valor mais fraco, que não autoriza adotar contra a contagem. Derrubar o
    /// app por causa de uma sondagem opcional trocaria um vazamento por um app que não abre.
    /// </summary>
    [Fact]
    public async Task LeituraDoBancoQueESTOURA_NaoDerrubaOBoot_EVira_NaoSei()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco com os ~700");

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(
            ServerWorkspace,
            ProbeThatExplodes(),
            _ => throw new IOException("banco em uso (teste)"),
            accountWorkspaceCount: 1);

        Assert.Equal(SessionVaultKind.Personal, escopo.Kind);
    }

    /// <summary>
    /// <b>A regra 6 não pode ser a mensagem que o operador recebe sobre o PRÓPRIO cofre.</b> A
    /// recusa "cofre pessoal de outra instalação" é certa para o caso que ela descreve — e era
    /// também o beco sem saída de quem tinha acabado de abrir o time: o marcador ficava com o GUID
    /// do time, e o <c>sync-local.owner</c> é invisível para o operador.
    /// </summary>
    [Fact]
    public async Task DepoisDeAbrirOTIME_OCofrePESSOAL_CONTINUA_Abrindo()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco com os ~700");

        await Resolver(ring).ResolveAsync(
            OutroWorkspace, SondaQueRestaura(ring), BancoSincronizadoCom(ServerWorkspace));

        // No dia seguinte ele abre o PRÓPRIO cofre. Offline, e sem acusação nenhuma.
        SessionVaultScope pessoal = await Resolver(ring).ResolveAsync(
            ServerWorkspace, ProbeThatExplodes(), BancoSincronizadoCom(ServerWorkspace));

        Assert.Equal(SessionVaultKind.Personal, pessoal.Kind);
        Assert.Equal(ServerWorkspace, File.ReadAllText(OwnerPath).Trim());
    }

    private static Func<string, CancellationToken, Task<bool>> Probe(bool resposta) =>
        (_, _) => Task.FromResult(resposta);

    /// <summary>A sonda de produção responde "é de time" e RESTAURA a chave na mesma ida.</summary>
    private static Func<string, CancellationToken, Task<bool>> SondaQueRestaura(WkWorkspaceKeyRing ring) =>
        (ws, ct) => ring.MintWorkspaceKeyAsync(AppRuntime.TeamVaultWorkspace(ws), ct)
            .ContinueWith(t => { t.Result.Dispose(); return true; }, TaskScheduler.Default);

    /// <summary>O <c>sync_cursor</c> do banco pessoal (ver <c>PersonalDbOwnerProbe</c>).</summary>
    private static Func<CancellationToken, Task<IReadOnlyList<string>?>> BancoSincronizadoCom(
        params string[] workspaces) => _ => Task.FromResult<IReadOnlyList<string>?>(workspaces);

    /// <summary>Lista VAZIA — o banco abriu e nunca sincronizou. É medição, não "não deu para ler".</summary>
    private static Func<CancellationToken, Task<IReadOnlyList<string>?>> BancoQueNuncaSincronizou() =>
        _ => Task.FromResult<IReadOnlyList<string>?>([]);

    /// <summary>A sonda não pode nem ser tocada quando o disco já respondeu.</summary>
    private static Func<string, CancellationToken, Task<bool>> ProbeThatExplodes() =>
        (_, _) => throw new InvalidOperationException("a sonda online não deveria ter sido chamada");

    private static Func<string, CancellationToken, Task<bool>> ProbeThatFailsOffline() =>
        (_, _) => throw new System.Net.Http.HttpRequestException("rede indisponível (teste)");
}
