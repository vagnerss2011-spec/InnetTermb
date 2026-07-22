using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using RemoteOps.Sync;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// <b>A raiz do cofre chega ao canal de segredos pela FÁBRICA</b> — o último elo da tabela de raízes
/// que não tinha guarda.
///
/// <para><b>O que faltava, e por quê importa:</b> o <c>SecretSyncOrchestrator</c> sempre aceitou um
/// <c>vaultAlgorithm</c>, e até esta fatia o <c>SyncSessionFactory</c> não o informava — o valor
/// certo existia e não chegava a quem precisava dele. O 1j corrigiu, e o
/// <c>SecretSyncAlgorithmTests</c> (T17) fixa o COMPORTAMENTO do orquestrador; ninguém fixava a
/// PASSAGEM. Uma reversão que apagasse <c>options.VaultAlgorithm</c> da chamada deixava a suíte
/// inteira verde, e o estrago só apareceria num cofre de time: um registro sem <c>algorithm</c> no
/// fio gravado como <c>AmkRootedV1</c>, ou seja, um envelope que monta o AAD errado e nunca abre —
/// sem erro nenhum na hora em que o defeito é cometido.</para>
///
/// <para><b>Qual costura foi escolhida, e por quê:</b> a <see cref="SyncSession"/> INTEIRA, montada
/// pela <see cref="SyncSessionFactory.Create"/> de produção, com o banco SQLCipher de verdade. O
/// plano registrava que isso "exige HTTP + SignalR reais" — não exige: a fábrica só CONSTRÓI o
/// <c>HttpClient</c> e o <c>HubConnection</c>, e nenhum dos dois toca a rede antes de alguém pedir
/// um ciclo de sync. Testar o ponto de entrada real era possível o tempo todo, e é estritamente
/// melhor do que abrir um método privado só para o teste alcançá-lo: uma costura nova provaria o
/// pedaço que ela mesma expõe, não o caminho que o app percorre.</para>
///
/// <para><b>E por que DOIS testes:</b> o primeiro lê o valor que aterrissou (prova que é o certo, e
/// não um qualquer); o segundo é comportamental e não depende de reflexão nenhuma — ele fica
/// vermelho na reversão exata mesmo que o primeiro seja apagado. Uma guarda que uma única mudança de
/// nome desarma não é guarda.</para>
/// </summary>
public sealed class SyncSessionFactoryAlgorithmTests : IDisposable
{
    private const string ServerWorkspace = "8f3b6f4a-0000-4000-8000-000000000001";
    private const string TeamVault = "time:8f3b6f4a-0000-4000-8000-000000000001";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"remoteops-factory-algo-{Guid.NewGuid():n}");

    private readonly FakeCredentialVault _vault = new();

    public SyncSessionFactoryAlgorithmTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Limpeza best-effort: arquivo preso pelo SQLite não pode derrubar o teste.
        }
    }

    /// <summary>
    /// As opções como o <c>App.StartAccountSyncAsync</c> as monta: banco desta sessão, cofre desta
    /// sessão e a RAIZ desta sessão. A URL é https e inalcançável de propósito — a montagem não fala
    /// com ela, e um teste que precisasse de servidor não estaria testando a fábrica.
    /// </summary>
    private async Task<SyncSessionOptions> OptionsAsync(
        string vaultAlgorithm, bool comCanalDeSegredos = true)
    {
        WorkspaceContext workspace = await new LocalSyncClientFactory(_vault, _dir)
            .OpenWorkspaceAsync("local", "local");

        return new SyncSessionOptions
        {
            Workspace = workspace,
            WorkspaceId = ServerWorkspace,
            CloudBaseUrl = new Uri("https://localhost:1/"),
            DeviceId = Guid.NewGuid(),
            Vault = _vault,
            TokenRefPath = Path.Combine(_dir, "cloud-tokens.tokenref"),
            EnvelopeStore = comCanalDeSegredos ? new FileVaultStore(Path.Combine(_dir, "vault.json")) : null,
            VaultWorkspaceId = comCanalDeSegredos ? TeamVault : null,
            VaultAlgorithm = vaultAlgorithm,
        };
    }

    /// <summary>
    /// Desce do <see cref="SyncSession"/> até a raiz que o canal de segredos recebeu. Cada salto tem
    /// asserção própria: se um campo for renomeado, o teste morre dizendo QUAL elo quebrou, em vez de
    /// estourar um <c>NullReferenceException</c> que ninguém sabe ler.
    /// </summary>
    private static string RaizDoCanalDeSegredos(SyncSession session)
    {
        object? orchestrator = Campo(session, "_orchestrator");
        Assert.IsType<SyncOrchestrator>(orchestrator);

        object? secrets = Campo(orchestrator, "_secrets");
        var canal = Assert.IsType<SecretSyncOrchestrator>(secrets);

        object? algoritmo = Campo(canal, "_vaultAlgorithm");
        return Assert.IsType<string>(algoritmo);
    }

    private static object? Campo(object? alvo, string nome)
    {
        Assert.NotNull(alvo);
        FieldInfo? campo = alvo.GetType().GetField(
            nome, BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.True(
            campo is not null,
            $"O campo '{nome}' não existe mais em {alvo.GetType().Name}. Este teste amarra a "
            + "PASSAGEM da raiz do cofre pela SyncSessionFactory; se o campo mudou de nome, atualize "
            + "o caminho — não apague a guarda.");

        return campo!.GetValue(alvo);
    }

    /// <summary>
    /// <b>Sessão de TIME: a fábrica entrega <c>WkRootedV1</c> ao canal de segredos.</b> Sem isto, um
    /// envelope que descesse sem <c>algorithm</c> no fio seria selado com a raiz da conta e ninguém
    /// do time o abriria — e nada nesta máquina daria erro no momento do estrago.
    /// </summary>
    [Fact]
    public async Task AFabrica_ENTREGA_ARaizDaSessaoDeTIME_AoCanalDeSegredos()
    {
        SyncSessionOptions options = await OptionsAsync(VaultAlgorithms.WkRootedV1);

        await using SyncSession session = SyncSessionFactory.Create(options);

        Assert.Equal(VaultAlgorithms.WkRootedV1, RaizDoCanalDeSegredos(session));
    }

    /// <summary>
    /// O simétrico, e o guarda dos ~700: a sessão PESSOAL continua chegando ao canal com
    /// <c>AmkRootedV1</c>. As duas metades juntas é que provam a passagem — só a de time passaria
    /// também num código que ignorasse as opções e cravasse a constante do time.
    /// </summary>
    [Fact]
    public async Task AFabrica_ENTREGA_ARaizDaSessaoPESSOAL_AoCanalDeSegredos()
    {
        SyncSessionOptions options = await OptionsAsync(VaultAlgorithms.AmkRootedV1);

        await using SyncSession session = SyncSessionFactory.Create(options);

        Assert.Equal(VaultAlgorithms.AmkRootedV1, RaizDoCanalDeSegredos(session));
    }

    /// <summary>
    /// <b>A guarda que sobrevive a um <c>rename</c>.</b> O <c>SecretSyncOrchestrator</c> recusa raiz
    /// em branco no próprio construtor; se a fábrica PASSA o que veio nas opções, a recusa chega até
    /// quem chamou a fábrica. Se ela deixar de passar, o default toma o lugar e a montagem passa a
    /// funcionar com um valor que ninguém pediu — que é exatamente a reversão a ser barrada, e ela
    /// fica vermelha aqui sem uma linha de reflexão.
    /// </summary>
    [Fact]
    public async Task RaizEmBRANCO_NasOpcoes_CHEGA_AoCanal_EEleRECUSA()
    {
        SyncSessionOptions options = await OptionsAsync("   ");

        ArgumentException erro = Assert.Throws<ArgumentException>(
            () => SyncSessionFactory.Create(options));

        Assert.Equal("vaultAlgorithm", erro.ParamName);
    }

    /// <summary>
    /// <b>A metade que impede "recusar tudo".</b> Sem canal de segredos (sessão só de metadados, o
    /// comportamento pré-Fase 1) não há raiz a validar, e a MESMA opção inválida não impede a
    /// montagem. Sem este teste, a asserção acima poderia estar sendo satisfeita por uma validação
    /// solta na fábrica — que provaria outra coisa.
    /// </summary>
    [Fact]
    public async Task SemCanalDeSegredos_ARaizNaoEhValidada_ASessaoMonta()
    {
        SyncSessionOptions options = await OptionsAsync("   ", comCanalDeSegredos: false);

        await using SyncSession session = SyncSessionFactory.Create(options);

        Assert.NotNull(session);
    }
}
