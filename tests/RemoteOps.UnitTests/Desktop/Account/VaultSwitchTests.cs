using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Contracts.Sync;
using RemoteOps.Desktop.Account;
using RemoteOps.Sync;
using RemoteOps.Sync.Remote;
using RemoteOps.UnitTests.Sync;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Account;

/// <summary>
/// ⚠️ <b>Trocar de cofre sem perder trabalho.</b>
///
/// <para>Sair da conta é o que faz o próximo boot voltar a perguntar em qual cofre entrar — é o
/// caminho de produção que faltava para o <c>LogoutAsync</c> (que existia, testado, e com ZERO
/// chamadores). Mas ele mexe em duas coisas ao mesmo tempo, e a ordem entre elas é o ponto:
/// <c>LogoutAsync</c> apaga o token store, e é esse token que autentica o push do outbox. Sair antes
/// de drenar deixaria a fila parada com um erro de autenticação que ninguém veria — trocar de cofre
/// viraria exatamente a perda de trabalho que este fluxo existe para evitar.</para>
///
/// <para>O outbox é durável e append-only: o que não subir continua no banco daquele cofre. Por isso
/// o número é <b>medido</b> e mostrado antes, em vez de um aviso genérico permanente — que é o aviso
/// que ninguém lê.</para>
/// </summary>
public sealed class VaultSwitchTests : IDisposable
{
    private const string Email = "op@innet.tec.br";
    private const string ServerWorkspace = "ws-local";

    private static readonly string[] VaultWorkspaces = ["ws-local", "local"];

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"remoteops-vault-switch-{Guid.NewGuid():n}");

    private readonly FakeCredentialVault _vault = new();

    public VaultSwitchTests() => Directory.CreateDirectory(_dir);

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

    private static byte[] SampleAmk() => [.. Enumerable.Range(0, 32).Select(i => (byte)(i + 1))];

    // ── Fakes de conta ───────────────────────────────────────────────────────────────────────

    private sealed class FakeAmkCache : IAmkCache
    {
        private CachedAccount? _entry;

        public int ClearCount { get; private set; }

        /// <summary>
        /// Onde o cache registra que saiu, NO INSTANTE em que sai. Sem isto o teste de ordem seria
        /// vácuo: olhar o <see cref="ClearCount"/> depois da chamada inteira passa igual com a saída
        /// acontecendo ANTES do drenar — que é justamente o defeito.
        /// </summary>
        public List<string>? Trace { get; set; }

        public void Seed() => _entry = new CachedAccount(Email, "ws-guid", 1, SampleAmk());

        public Task<CachedAccount?> LoadAsync(CancellationToken ct = default)
            => Task.FromResult(_entry is null
                ? null
                : new CachedAccount(_entry.Email, _entry.WorkspaceId, _entry.AmkKeyVersion, _entry.Amk.ToArray()));

        public Task SaveAsync(CachedAccount account, CancellationToken ct = default)
        {
            _entry = new CachedAccount(
                account.Email, account.WorkspaceId, account.AmkKeyVersion, account.Amk.ToArray());
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            _entry?.Dispose();
            _entry = null;
            ClearCount++;
            Trace?.Add("saiu");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVaultActivator : IAccountVaultActivator
    {
        internal FakeTokenStore Tokens { get; } = new();

        public Task<ITokenStore> ActivateAsync(
            ReadOnlyMemory<byte> amk,
            string syncWorkspaceId,
            IReadOnlyList<string> vaultWorkspaceIds,
            CancellationToken ct = default)
            => Task.FromResult<ITokenStore>(Tokens);
    }

    private sealed class FakeTokenStore : ITokenStore
    {
        private TokenSet? _current;

        internal int ClearCount { get; private set; }

        public Task<TokenSet?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_current);

        public Task SaveAsync(TokenSet tokens, CancellationToken ct = default)
        {
            _current = tokens;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            _current = null;
            ClearCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class NoSyncStarter : IAccountSyncStarter
    {
        public Task StartAsync(string workspaceId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private LocalSyncClientFactory NewFactory() => new(_vault, _dir);

    /// <summary>Cria o banco desta sessão e enfileira <paramref name="pendencias"/> edições.</summary>
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

    // ── (a) o botão chega mesmo ao LogoutAsync ───────────────────────────────────────────────

    /// <summary>
    /// <b>O teste que amarra o botão ao conserto.</b> Sair pelo caminho novo apaga o cache da AMK —
    /// e é a ausência desse arquivo que faz o próximo boot pedir login e oferecer a escolha do cofre.
    /// Aqui o coordenador é o de PRODUÇÃO: se a chamada não chegar ao <c>LogoutAsync</c>, o cache
    /// continua lá e o operador continua preso no mesmo cofre.
    /// </summary>
    [Fact]
    public async Task SairDoCofre_APAGA_OCacheDaConta_EOsTokens()
    {
        var cache = new FakeAmkCache();
        cache.Seed();
        var activator = new FakeVaultActivator();
        var coordinator = new AccountSyncCoordinator(
            cache, activator, new NoSyncStarter(), VaultWorkspaces);
        WorkspaceContext ctx = await SemearAsync("local", pendencias: 0);

        await new VaultSwitch(coordinator, ctx).SignOutAsync();

        Assert.Equal(1, cache.ClearCount);
        Assert.Null(await coordinator.TryActivateFromCacheAsync());
    }

    /// <summary>
    /// ⚠️ <b>A ordem.</b> Drenar DEPOIS de sair da conta seria drenar sem token: o push falharia e a
    /// fila ficaria parada em silêncio. O drenar tem de acontecer com a sessão ainda viva.
    /// </summary>
    [Fact]
    public async Task SairDoCofre_DRENA_ANTES_DeSairDaConta()
    {
        var ordem = new List<string>();
        var cache = new FakeAmkCache();
        cache.Seed();
        cache.Trace = ordem;
        var coordinator = new AccountSyncCoordinator(
            cache, new FakeVaultActivator(), new NoSyncStarter(), VaultWorkspaces);
        WorkspaceContext ctx = await SemearAsync("local", pendencias: 0);

        // Os dois eventos se registram QUANDO acontecem — o drenar aqui, a saída dentro do cache de
        // produção. Conferir estado depois da chamada inteira não distinguiria uma ordem da outra.
        var alvo = new VaultSwitch(coordinator, ctx, drain: () =>
        {
            ordem.Add("drenou");
            return Task.CompletedTask;
        });

        await alvo.SignOutAsync();

        Assert.Equal(["drenou", "saiu"], ordem);
    }

    /// <summary>
    /// Offline-first: sem sessão de sync não há o que drenar, e isso <b>não</b> impede o operador de
    /// trocar de cofre. Um app que só deixa trocar com servidor no ar seria pior que o problema.
    /// </summary>
    [Fact]
    public async Task SemSessaoDeSync_NaoDrena_MasSAI_DaConta()
    {
        var cache = new FakeAmkCache();
        cache.Seed();
        var coordinator = new AccountSyncCoordinator(
            cache, new FakeVaultActivator(), new NoSyncStarter(), VaultWorkspaces);
        WorkspaceContext ctx = await SemearAsync("local", pendencias: 0);

        await new VaultSwitch(coordinator, ctx, drain: null).SignOutAsync();

        Assert.Equal(1, cache.ClearCount);
    }

    /// <summary>
    /// Rede fora no meio do drenar é ROTINA de campo, não erro: o outbox é durável e o que ficou sobe
    /// quando o RemoteOps for aberto naquele cofre. Travar a troca aqui prenderia o operador no cofre
    /// errado por causa de um servidor que ele não controla.
    /// </summary>
    [Fact]
    public async Task DrenarFALHANDO_NaoIMPEDE_ATroca()
    {
        var cache = new FakeAmkCache();
        cache.Seed();
        var coordinator = new AccountSyncCoordinator(
            cache, new FakeVaultActivator(), new NoSyncStarter(), VaultWorkspaces);
        WorkspaceContext ctx = await SemearAsync("local", pendencias: 0);

        await new VaultSwitch(coordinator, ctx, drain: () =>
            Task.FromException(new HttpRequestException("servidor fora"))).SignOutAsync();

        Assert.Equal(1, cache.ClearCount);
    }

    // ── (b) o que ficaria para trás, MEDIDO ──────────────────────────────────────────────────

    /// <summary>
    /// O número que o operador vê antes de confirmar sai do banco DESTA sessão — o único que a troca
    /// deixa para trás.
    /// </summary>
    [Fact]
    public async Task AFilaDESTASessao_EhMEDIDA_AntesDaTroca()
    {
        WorkspaceContext ctx = await SemearAsync("local", pendencias: 5);
        VaultSwitchBacklog backlog = await NewSwitch(ctx).ReadBacklogAsync();

        Assert.Equal(5, backlog.Pending);
        Assert.False(backlog.CheckFailed);
        Assert.True(backlog.HasSomethingToSay);
    }

    /// <summary>
    /// A metade que impede o alarme falso: o que JÁ SUBIU não é pendência. Sem ela o aviso apareceria
    /// em toda troca de qualquer máquina que um dia editou algo — e alarme falso mata o aviso
    /// verdadeiro.
    /// </summary>
    [Fact]
    public async Task OQueJaSUBIU_NaoConta_ENaoHaOQueAvisar()
    {
        WorkspaceContext ctx = await SemearAsync("local", pendencias: 4);
        await new SqliteSyncMetadataStore(ctx).SaveOutboxCursorAsync(ServerWorkspace, 4);

        VaultSwitchBacklog backlog = await NewSwitch(ctx).ReadBacklogAsync();

        Assert.Equal(0, backlog.Pending);
        Assert.False(backlog.CheckFailed);
        Assert.False(backlog.HasSomethingToSay);
    }

    /// <summary>
    /// ⚠️ <b>"Não deu para conferir" nunca vira zero.</b> Com o banco ilegível, afirmar "nada
    /// pendente" seria a classe de defeito nº 1 desta base — erro virando estado vazio — e no pior
    /// momento: o instante em que o operador decide se troca de cofre.
    /// </summary>
    [Fact]
    public async Task BancoIlegivel_MARCA_NaoVerificado_EmVezDeAfirmarZero()
    {
        WorkspaceContext ctx = await SemearAsync("local", pendencias: 3);

        // Lixo por cima do arquivo: é o que um banco corrompido/chave errada produz na prática.
        await File.WriteAllBytesAsync(
            NewFactory().DbPath("local"), [.. Enumerable.Repeat((byte)0x7f, 8192)]);

        VaultSwitchBacklog backlog = await NewSwitch(ctx).ReadBacklogAsync();

        Assert.True(backlog.CheckFailed);
        Assert.True(backlog.HasSomethingToSay);
    }

    private VaultSwitch NewSwitch(WorkspaceContext ctx)
    {
        var cache = new FakeAmkCache();
        cache.Seed();
        return new VaultSwitch(
            new AccountSyncCoordinator(cache, new FakeVaultActivator(), new NoSyncStarter(), VaultWorkspaces),
            ctx);
    }
}
