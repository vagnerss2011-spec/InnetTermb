using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Security.Account;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Account;

/// <summary>
/// Em qual cofre esta sessão do app escreve — decidido UMA vez, no boot, e nunca no meio.
///
/// <para><b>A regra que manda:</b> a resposta sai do DISCO e do FATO que o login trouxe. Rede só
/// quando os dois se calam, e nunca mais depois. Um escopo que dependesse de rede mudaria de valor
/// conforme o sinal do operador — e mudar de cofre por causa do sinal é escrever no banco errado sem
/// que nada apareça na tela.</para>
///
/// <para><b>E a ordem das regras é a guarda:</b> "o servidor disse que este workspace é um TIME" vem
/// antes de tudo (um time NUNCA vira dono do banco pessoal), e "este workspace é o dono do meu banco
/// pessoal" vem antes de qualquer heurística de time. É isso que impede o app do operador de mudar de
/// modo sozinho por causa de um blob de chave que o build 1e gravou no workspace pessoal dele no
/// instante em que ele clicou "convidar".</para>
///
/// <para><b>E o que esta rodada acrescentou:</b> a sonda online é TRI-ESTADO. O 404 de
/// <c>GET /workspaces/{id}/key</c> significa "a SUA CONTA não guarda embrulho neste workspace" — e é
/// indistinguível de um 404 de infraestrutura. Ele NÃO é "não é de time", e portanto não autoriza
/// gravar o dono do banco com os ~700.</para>
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
    /// Fixa o dono do banco pessoal do jeito que a PRODUÇÃO fixa: o login declarou que aquele
    /// workspace é o cofre PESSOAL da conta. Sem rede — a sonda nem é tocada.
    /// </summary>
    private Task<SessionVaultScope> FixaDonoPessoal(WkWorkspaceKeyRing ring) =>
        Resolver(ring).ResolveAsync(
            ServerWorkspace, ProbeThatExplodes(), declaredKind: WorkspaceKindFact.Personal);

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

        // Primeira abertura depois do upgrade: o servidor declarou que este workspace é o pessoal.
        SessionVaultScope primeira = await FixaDonoPessoal(ring);
        Assert.Equal(SessionVaultKind.Personal, primeira.Kind);
        Assert.True(File.Exists(OwnerPath));

        // Agora a WK do time existe em disco PARA ESTE MESMO workspace (o que o 1e gravava ao
        // clicar "convidar"). Nada muda: o escopo continua pessoal.
        (await ring.MintWorkspaceKeyAsync(AppRuntime.TeamVaultWorkspace(ServerWorkspace))).Dispose();

        SessionVaultScope segunda = await Resolver(ring).ResolveAsync(
            ServerWorkspace, Probe(WorkspaceKindFact.Team));

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
        await FixaDonoPessoal(ring); // fixa o dono do banco local
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
        await FixaDonoPessoal(ring);

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
        await FixaDonoPessoal(ring);

        var erro = await Assert.ThrowsAsync<SessionVaultScopeException>(
            () => Resolver(ring).ResolveAsync(OutroWorkspace, ProbeThatFailsOffline()));

        Assert.Contains("Conecte-se", erro.Message, StringComparison.Ordinal);
        Assert.Contains("cofre pessoal", erro.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// O servidor declarou que este workspace é PESSOAL e ele não é o dono do banco desta máquina: é
    /// o cofre pessoal de OUTRA conta/instalação. Recusar é a única saída honesta — abrir misturaria
    /// dois acervos.
    ///
    /// <para><b>O que mudou nesta rodada:</b> essa conclusão exige agora o FATO do servidor. Antes ela
    /// saía de um <c>false</c> da sonda, e o <c>false</c> da sonda também era o que um 404 de
    /// infraestrutura produzia — ver <see cref="Sonda404DeINFRA_NaoAcusaOOperador_DizQueNaoSabe"/>.</para>
    /// </summary>
    [Fact]
    public async Task WorkspacePessoalDeOUTRAInstalacao_RECUSA()
    {
        using WkWorkspaceKeyRing ring = Ring();
        await FixaDonoPessoal(ring);

        var erro = await Assert.ThrowsAsync<SessionVaultScopeException>(
            () => Resolver(ring).ResolveAsync(
                OutroWorkspace, Probe(WorkspaceKindFact.Unknown),
                declaredKind: WorkspaceKindFact.Personal));

        Assert.Contains("outra instalação", erro.Message, StringComparison.Ordinal);
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
        await FixaDonoPessoal(ring);

        int idas = 0;
        Task<WorkspaceKindFact> RestauraNaPrimeiraIda(string ws, CancellationToken ct)
        {
            idas++;
            return ring.MintWorkspaceKeyAsync(AppRuntime.TeamVaultWorkspace(ws), ct)
                .ContinueWith(
                    t => { t.Result.Dispose(); return WorkspaceKindFact.Team; }, TaskScheduler.Default);
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
    /// sonda decide (e restaura a chave na mesma ida); o dono só é adotado com evidência do lado
    /// pessoal.
    /// </summary>
    [Fact]
    public async Task MaquinaNova_EscolhendoOTimePrimeiro_ASondaDecide_EODonoNaoEAdotado()
    {
        using WkWorkspaceKeyRing ring = Ring();

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(
            OutroWorkspace, SondaQueRestaura(ring));

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
    /// O caso do UPGRADE de toda a frota: banco pessoal já existe e já sincronizou com ESTE
    /// workspace, marcador ainda não. Continua pessoal, continua offline (a sonda não pode nem ser
    /// tocada) — byte a byte o app de hoje.
    ///
    /// <para>É esta regra que impede a correção da máquina nova de virar um imposto de rede no boot
    /// de quem já usava o RemoteOps. E a evidência vem do delegate que a PRODUÇÃO sempre passa
    /// (<c>PersonalDbOwnerProbe</c>) — a versão anterior deste teste o omitia, e com isso afirmava
    /// que a mera existência do arquivo bastava.</para>
    /// </summary>
    [Fact]
    public async Task UpgradeComBancoLocal_SemMarcador_SeguePessoal_SemSonda()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco antigo");

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(
            ServerWorkspace, ProbeThatExplodes(), BancoSincronizadoCom(ServerWorkspace));

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
    /// Evidência CONTRÁRIA e o servidor dizendo que este workspace é pessoal: o banco desta máquina é
    /// de outro workspace e este aqui é o cofre pessoal de outra instalação. Recusa alta — adotar
    /// misturaria os dois acervos contra uma evidência medida, que é pior do que fazê-lo na ausência
    /// dela.
    /// </summary>
    [Fact]
    public async Task EvidenciaCONTRARIA_ESondaDizQueNaoEDeTime_RECUSA()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco com os ~700");

        var erro = await Assert.ThrowsAsync<SessionVaultScopeException>(
            () => Resolver(ring).ResolveAsync(
                OutroWorkspace,
                Probe(WorkspaceKindFact.Unknown),
                BancoSincronizadoCom(ServerWorkspace),
                declaredKind: WorkspaceKindFact.Personal));

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

    // ── I1: o fato autoritativo (workspaces.kind) e o 404 que não é resposta ────────────────

    /// <summary>
    /// ⚠️ <b>O BLOQUEANTE, reproduzido.</b> A máquina do operador (banco com os ~700, marcador ainda
    /// não escrito) abrindo o TIME, com a sonda devolvendo o que um <b>404</b> devolve. Esse 404 quer
    /// dizer "a SUA CONTA não guarda embrulho neste workspace" — e é indistinguível de um 404 de
    /// INFRAESTRUTURA (proxy sem a rota, URL errada, cliente novo × backend velho: exatamente a
    /// janela da ordem de deploy).
    ///
    /// <para>Lido como "não é de time", ele fazia <c>sync-local.owner</c> nascer com o <b>GUID DO
    /// TIME</b>: o banco dos ~700 passava a ser "o banco pessoal do time", o outbox inteiro subia
    /// para os colegas e, no dia seguinte, o cofre pessoal do operador era recusado com "acervo de
    /// outra instalação" — e o app encerrava, sem caminho de volta na interface.</para>
    ///
    /// <para><b>A prova aqui é a AUSÊNCIA do arquivo:</b> "não sei" não grava dono.</para>
    /// </summary>
    [Fact]
    public async Task Sonda404DeINFRA_NaoVira_Pessoal_ENAO_GravaODonoComOGuidDoTIME()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco com os ~700");

        // Record, e não Assert.ThrowsAsync: a asserção que PROVA o bloqueante é a do arquivo, e ela
        // tem de rodar mesmo quando o resolvedor devolve um escopo em vez de recusar — senão a
        // reversão da guarda falharia por "não lançou" e nunca mostraria o estrago real.
        Exception? erro = await Record.ExceptionAsync(
            () => Resolver(ring).ResolveAsync(
                OutroWorkspace,
                Probe(WorkspaceKindFact.Unknown),
                // Cliente novo × backend velho: nem o `kind` chega, nem o banco tem o que dizer
                // (ele nunca sincronizou — a conta é nova neste PC).
                BancoQueNuncaSincronizou(),
                accountWorkspaceCount: 2));

        // ⚠️ A PROVA: o banco com os ~700 NÃO mudou de dono. Com a conflação, este arquivo nasce
        // aqui com o GUID do TIME.
        Assert.False(File.Exists(OwnerPath));

        var recusa = Assert.IsType<SessionVaultScopeException>(erro);
        Assert.Contains("não foi possível identificar", recusa.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A recusa por "não sei" NÃO pode usar o texto de "cofre pessoal de outra instalação": aquele
    /// texto ACUSA o operador de estar abrindo acervo alheio, e aqui o app simplesmente não
    /// perguntou direito. O recado tem de dizer o que aconteceu e o que fazer.
    /// </summary>
    [Fact]
    public async Task Sonda404DeINFRA_NaoAcusaOOperador_DizQueNaoSabe()
    {
        using WkWorkspaceKeyRing ring = Ring();
        await FixaDonoPessoal(ring); // há um dono registrado: cai na regra 6

        var erro = await Assert.ThrowsAsync<SessionVaultScopeException>(
            () => Resolver(ring).ResolveAsync(OutroWorkspace, Probe(WorkspaceKindFact.Unknown)));

        Assert.DoesNotContain("outra instalação", erro.Message, StringComparison.Ordinal);
        Assert.Contains("não foi possível identificar", erro.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>O fato autoritativo do login resolve sem tocar a rede.</b> O servidor já sabe que o
    /// workspace é um TIME (<c>workspaces.kind</c>); ele viaja na lista do login. Com ele, o boot do
    /// segundo PC do colega não precisa de sonda nenhuma — e a chave que falta é buscada depois, pelo
    /// reparo de boot, com o desfecho indo para os Logs.
    /// </summary>
    [Fact]
    public async Task KindDoLogin_DizTIME_AbreOTIME_SemRede_ENaoGravaDono()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco com os ~700");

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(
            OutroWorkspace,
            ProbeThatExplodes(),
            BancoQueNuncaSincronizou(),
            accountWorkspaceCount: 2,
            declaredKind: WorkspaceKindFact.Team);

        Assert.Equal(SessionVaultKind.TeamWithoutKey, escopo.Kind);
        Assert.Equal("time:" + OutroWorkspace, escopo.VaultWorkspaceId);
        Assert.False(File.Exists(OwnerPath));
    }

    /// <summary>
    /// O MESMO fato, com a chave já no disco: cofre do time completo, e continua sem rede.
    /// </summary>
    [Fact]
    public async Task KindDoLogin_DizTIME_ComChaveNoDisco_AbreOTimeCompleto()
    {
        using WkWorkspaceKeyRing ring = Ring();
        (await ring.MintWorkspaceKeyAsync(AppRuntime.TeamVaultWorkspace(OutroWorkspace))).Dispose();

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(
            OutroWorkspace, ProbeThatExplodes(), declaredKind: WorkspaceKindFact.Team);

        Assert.Equal(SessionVaultKind.Team, escopo.Kind);
    }

    /// <summary>
    /// ⚠️ <b>O marcador envenenado não pode sobreviver ao fato.</b> Se <c>sync-local.owner</c> disser
    /// que um workspace de TIME é o dono do banco pessoal (o estrago que esta correção fecha, e que
    /// um build desta branch chegou a produzir), a regra 1 daria "pessoal" e o acervo inteiro subiria
    /// para os colegas. O fato do servidor vem ANTES dela: é time, e o banco dos ~700 nem é aberto.
    /// </summary>
    [Fact]
    public async Task MarcadorENVENENADO_ComOFatoDizendoTIME_AbreOTIME_ENaoOBancoPessoal()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco com os ~700");
        File.WriteAllText(OwnerPath, OutroWorkspace); // envenenado: o "dono" é o TIME

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(
            OutroWorkspace, ProbeThatExplodes(), declaredKind: WorkspaceKindFact.Team);

        Assert.Equal(SessionVaultKind.TeamWithoutKey, escopo.Kind);
        Assert.Equal("team-" + OutroWorkspace, escopo.DbName);
    }

    /// <summary>
    /// <b>E o caminho de volta.</b> Com o marcador apontando para OUTRO workspace, o banco desta
    /// máquina ainda sabe de quem ele é: o <c>sync_cursor</c> é medição, o marcador é heurística
    /// gravada. Quando os dois discordam, quem ganha é a medição — senão o operador fica trancado
    /// fora do próprio cofre com um arquivo que ele não enxerga.
    /// </summary>
    [Fact]
    public async Task MarcadorApontandoParaOUTRO_MasOBancoDizQueEMeu_ABRE_ECorrigeOMarcador()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco com os ~700");
        File.WriteAllText(OwnerPath, OutroWorkspace);

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(
            ServerWorkspace, ProbeThatExplodes(), BancoSincronizadoCom(ServerWorkspace));

        Assert.Equal(SessionVaultKind.Personal, escopo.Kind);
        Assert.Equal(ServerWorkspace, File.ReadAllText(OwnerPath).Trim());
    }

    /// <summary>
    /// <b>Backend VELHO (sem o campo <c>kind</c>) não quebra e não decide errado.</b> O fato ausente é
    /// "não sei", e a evidência do banco continua respondendo pela frota inteira — offline, como
    /// antes. É a metade "cliente novo × backend velho" da janela de deploy.
    /// </summary>
    [Fact]
    public async Task BackendVELHO_SemOCampoKind_ContinuaAbrindoOCofrePessoal_PelaEvidencia()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco com os ~700");

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(
            ServerWorkspace,
            ProbeThatExplodes(),
            BancoSincronizadoCom(ServerWorkspace),
            accountWorkspaceCount: 2,
            declaredKind: WorkspaceKindFact.Unknown);

        Assert.Equal(SessionVaultKind.Personal, escopo.Kind);
        Assert.Equal(ServerWorkspace, File.ReadAllText(OwnerPath).Trim());
    }

    /// <summary>
    /// O fato dizendo PESSOAL adota <b>sem banco em disco e sem rede</b>: é a primeira abertura de uma
    /// máquina nova entrando no cofre pessoal da conta. Antes disso, este boot dependia de a sonda
    /// responder 404 — ou seja, dependia de uma AUSÊNCIA para afirmar um fato.
    /// </summary>
    [Fact]
    public async Task KindDoLogin_DizPESSOAL_ADOTA_SemBancoEmDisco_ESemRede()
    {
        using WkWorkspaceKeyRing ring = Ring();

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(
            ServerWorkspace, ProbeThatExplodes(), declaredKind: WorkspaceKindFact.Personal);

        Assert.Equal(SessionVaultKind.Personal, escopo.Kind);
        Assert.Equal(ServerWorkspace, File.ReadAllText(OwnerPath).Trim());
    }

    /// <summary>
    /// <b>403 é resposta do servidor, não "não é de time".</b> Membership cortada no meio: o app
    /// recusa e o texto fala do que aconteceu (o servidor respondeu e negou), em vez de mandar o
    /// operador "conectar-se à internet" — coisa que ele já está.
    /// </summary>
    [Fact]
    public async Task Sonda403_NaoViraResposta_RECUSA_ComRecadoDeAcessoNegado()
    {
        using WkWorkspaceKeyRing ring = Ring();
        await FixaDonoPessoal(ring);

        var erro = await Assert.ThrowsAsync<SessionVaultScopeException>(
            () => Resolver(ring).ResolveAsync(OutroWorkspace, SondaQueRecusa(HttpStatusCode.Forbidden)));

        Assert.False(File.Exists(OwnerPath) && File.ReadAllText(OwnerPath).Trim() == OutroWorkspace);
        Assert.Contains("recusou", erro.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Conecte-se", erro.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ <b>O relaunch pelo cache da AMK não traz a lista de workspaces</b> (ele não fala com o
    /// servidor, por desenho). Ali <c>accountWorkspaceCount</c> é <c>null</c> e <c>declaredKind</c> é
    /// <c>Unknown</c> — dois "não perguntei". Com o banco também sem nada a dizer, sobra ZERO
    /// evidência, e o resolvedor <b>recusa</b> em vez de adotar.
    ///
    /// <para>A versão anterior adotava: <c>null</c> passava no teste <c>is not >= 2</c>, ou seja, "não
    /// perguntei" tinha o mesmo peso de "medi e é 1".</para>
    /// </summary>
    [Fact]
    public async Task RelaunchPeloCache_SemLista_ESemEvidencia_NaoADOTA()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco que não deu para ler");

        await Assert.ThrowsAsync<SessionVaultScopeException>(
            () => Resolver(ring).ResolveAsync(
                ServerWorkspace,
                Probe(WorkspaceKindFact.Unknown),
                BancoIlegivel(),
                accountWorkspaceCount: null,
                declaredKind: WorkspaceKindFact.Unknown));

        Assert.False(File.Exists(OwnerPath));
    }

    /// <summary>
    /// E a metade que impede "recusar tudo" no mesmo caminho: o relaunch pelo cache do operador REAL
    /// tem o banco dos ~700, que já sincronizou com este workspace. Ele abre offline, sem lista e sem
    /// sonda — que é o boot de todo dia dele.
    /// </summary>
    [Fact]
    public async Task RelaunchPeloCache_ComEvidenciaDoBanco_ABRE_Offline()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(PersonalDbPath, "banco com os ~700");

        SessionVaultScope escopo = await Resolver(ring).ResolveAsync(
            ServerWorkspace,
            ProbeThatExplodes(),
            BancoSincronizadoCom(ServerWorkspace),
            accountWorkspaceCount: null,
            declaredKind: WorkspaceKindFact.Unknown);

        Assert.Equal(SessionVaultKind.Personal, escopo.Kind);
        Assert.Equal(ServerWorkspace, File.ReadAllText(OwnerPath).Trim());
    }

    // ── A recusa precisa ter VOLTA ────────────────────────────────────────────────────────

    /// <summary>
    /// ⚠️ <b>Recusar sem caminho de volta é o app morto.</b> O boot entra pelo cache da AMK, que
    /// guarda UM workspace — o mesmo que acabou de ser recusado. Sem apagar esse cache, toda
    /// abertura seguinte mostra a mesma tela e encerra, e a escolha de cofre mora DENTRO do login,
    /// que não é pedido enquanto houver cache.
    ///
    /// <para>Toda recusa sobre <b>qual workspace</b> foi aberto carrega a marca que manda o
    /// <c>App</c> sair da conta antes de encerrar (é o mesmo caminho do botão "Trocar de cofre…",
    /// decidido aqui porque o operador ainda não tem tela para clicar).</para>
    /// </summary>
    [Theory]
    [InlineData("naoSei")]
    [InlineData("outraInstalacao")]
    [InlineData("semRede")]
    [InlineData("acessoNegado")]
    public async Task RecusaSobreQualCofreFoiAberto_PEDE_LoginDeNovo(string caso)
    {
        using WkWorkspaceKeyRing ring = Ring();
        await FixaDonoPessoal(ring);
        File.WriteAllText(PersonalDbPath, "banco com os ~700");

        var erro = await Assert.ThrowsAsync<SessionVaultScopeException>(
            () => Resolver(ring).ResolveAsync(
                OutroWorkspace,
                caso switch
                {
                    "semRede" => ProbeThatFailsOffline(),
                    "acessoNegado" => SondaQueRecusa(HttpStatusCode.Forbidden),
                    _ => Probe(WorkspaceKindFact.Unknown),
                },
                BancoSincronizadoCom(ServerWorkspace),
                declaredKind: caso == "outraInstalacao"
                    ? WorkspaceKindFact.Personal
                    : WorkspaceKindFact.Unknown));

        Assert.True(erro.ReopenLoginToChooseVault);
    }

    /// <summary>
    /// E a metade que impede "apagar o cache por tudo": marcador ILEGÍVEL é falha TRANSITÓRIA sobre
    /// um cofre que está certo. Cobrar a senha do operador ali seria atrito por nada — a próxima
    /// abertura, com o arquivo legível, resolve sozinha.
    /// </summary>
    [Fact]
    public async Task RecusaTRANSITORIA_NaoApagaOCache_ENaoPedeSenha()
    {
        using WkWorkspaceKeyRing ring = Ring();
        File.WriteAllText(OwnerPath, ServerWorkspace);
        File.WriteAllText(PersonalDbPath, "banco com os ~700");

        using var travado = new FileStream(OwnerPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var erro = await Assert.ThrowsAsync<SessionVaultScopeException>(
            () => Resolver(ring).ResolveAsync(OutroWorkspace, ProbeThatExplodes()));

        Assert.False(erro.ReopenLoginToChooseVault);
    }

    private static Func<string, CancellationToken, Task<WorkspaceKindFact>> Probe(
        WorkspaceKindFact resposta) => (_, _) => Task.FromResult(resposta);

    /// <summary>A sonda de produção responde "é de time" e RESTAURA a chave na mesma ida.</summary>
    private static Func<string, CancellationToken, Task<WorkspaceKindFact>> SondaQueRestaura(
        WkWorkspaceKeyRing ring) =>
        (ws, ct) => ring.MintWorkspaceKeyAsync(AppRuntime.TeamVaultWorkspace(ws), ct)
            .ContinueWith(
                t => { t.Result.Dispose(); return WorkspaceKindFact.Team; }, TaskScheduler.Default);

    /// <summary>O <c>sync_cursor</c> do banco pessoal (ver <c>PersonalDbOwnerProbe</c>).</summary>
    private static Func<CancellationToken, Task<IReadOnlyList<string>?>> BancoSincronizadoCom(
        params string[] workspaces) => _ => Task.FromResult<IReadOnlyList<string>?>(workspaces);

    /// <summary>Lista VAZIA — o banco abriu e nunca sincronizou. É medição, não "não deu para ler".</summary>
    private static Func<CancellationToken, Task<IReadOnlyList<string>?>> BancoQueNuncaSincronizou() =>
        _ => Task.FromResult<IReadOnlyList<string>?>([]);

    /// <summary><c>null</c> — NÃO DEU PARA OLHAR. Nunca confundir com lista vazia.</summary>
    private static Func<CancellationToken, Task<IReadOnlyList<string>?>> BancoIlegivel() =>
        _ => Task.FromResult<IReadOnlyList<string>?>(null);

    /// <summary>A sonda não pode nem ser tocada quando o disco já respondeu.</summary>
    private static Func<string, CancellationToken, Task<WorkspaceKindFact>> ProbeThatExplodes() =>
        (_, _) => throw new InvalidOperationException("a sonda online não deveria ter sido chamada");

    private static Func<string, CancellationToken, Task<WorkspaceKindFact>> ProbeThatFailsOffline() =>
        (_, _) => throw new System.Net.Http.HttpRequestException("rede indisponível (teste)");

    /// <summary>O servidor RESPONDEU e recusou (403/401): não é falta de rede.</summary>
    private static Func<string, CancellationToken, Task<WorkspaceKindFact>> SondaQueRecusa(
        HttpStatusCode status) => (_, _) => throw new CloudSyncException(status);
}
