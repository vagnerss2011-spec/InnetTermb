using System;
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

    private SessionVaultScopeResolver Resolver(WkWorkspaceKeyRing ring) =>
        new(ring, _store, OwnerPath);

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

    private static Func<string, CancellationToken, Task<bool>> Probe(bool resposta) =>
        (_, _) => Task.FromResult(resposta);

    /// <summary>A sonda não pode nem ser tocada quando o disco já respondeu.</summary>
    private static Func<string, CancellationToken, Task<bool>> ProbeThatExplodes() =>
        (_, _) => throw new InvalidOperationException("a sonda online não deveria ter sido chamada");

    private static Func<string, CancellationToken, Task<bool>> ProbeThatFailsOffline() =>
        (_, _) => throw new System.Net.Http.HttpRequestException("rede indisponível (teste)");
}
