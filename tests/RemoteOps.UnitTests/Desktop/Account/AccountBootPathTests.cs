using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Security.Account;
using RemoteOps.Security.Crypto;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Account;

/// <summary>
/// ⚠️ <b>A fiação que faltava, e que teria pego o bloqueante do H2:</b> com o cache da AMK em disco o
/// boot entra DIRETO e a tela de escolha do cofre <b>nunca aparece</b>; sem cache, ela aparece.
///
/// <para>A suíte inteira injetava o chooser direto no autenticador ou montava o resolvedor à mão —
/// ou seja, a metade "com cache ⇒ sem chooser" nunca foi exercitada ponta a ponta. E ela é o defeito:
/// quatro telas mandavam <i>"feche e abra o RemoteOps e escolha o time ao entrar"</i>, o operador
/// fechava, abria, e o app entrava de novo no cofre pessoal sem perguntar nada — porque
/// <c>account.amk</c> continuava lá e <c>LogoutAsync</c> não tinha um único chamador de produção.</para>
///
/// <para>Os tipos são os de produção (<see cref="AccountSyncCoordinator"/> +
/// <see cref="E2eeAccountAuthenticator"/>): o que é fake aqui é a rede, o disco e a JANELA — nunca a
/// decisão sob teste.</para>
/// </summary>
public sealed class AccountBootPathTests
{
    private const string Email = "op@innet.tec.br";
    private const string Password = "senha-forte-123"; // pragma: allowlist secret
    private const string Pessoal = "11111111-1111-4111-8111-111111111111";
    private const string Time = "22222222-2222-4222-8222-222222222222";

    private static readonly Guid Device = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly string[] VaultWorkspaces = ["ws-local", "local"];

    private static byte[] SampleAmk() => [.. Enumerable.Range(0, 32).Select(i => (byte)(i + 1))];

    // ── Fakes: rede, disco e cofre. A decisão sob teste não é fake. ───────────────────────────

    private sealed class FakeAmkCache : IAmkCache
    {
        private CachedAccount? _entry;

        public int ClearCount { get; private set; }

        public void Seed(string workspaceId)
            => _entry = new CachedAccount(Email, workspaceId, 1, SampleAmk());

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
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVaultActivator : IAccountVaultActivator
    {
        public List<string> TokenScopes { get; } = [];

        public Task<ITokenStore> ActivateAsync(
            ReadOnlyMemory<byte> amk,
            string syncWorkspaceId,
            IReadOnlyList<string> vaultWorkspaceIds,
            CancellationToken ct = default)
        {
            TokenScopes.Add(syncWorkspaceId);
            return Task.FromResult<ITokenStore>(new FakeTokenStore());
        }
    }

    private sealed class FakeTokenStore : ITokenStore
    {
        private TokenSet? _current;

        public Task<TokenSet?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_current);

        public Task SaveAsync(TokenSet tokens, CancellationToken ct = default)
        {
            _current = tokens;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            _current = null;
            return Task.CompletedTask;
        }
    }

    private sealed class NoSyncStarter : IAccountSyncStarter
    {
        public Task StartAsync(string workspaceId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Servidor mínimo que devolve a lista de cofres que o teste mandar.</summary>
    private sealed class TwoWorkspaceServer : IAccountApi
    {
        private readonly AccountKeyService _keys = new();
        private AccountEnrollment? _enrollment;

        public Task<KdfResponse> GetKdfAsync(string email, CancellationToken ct = default)
        {
            _enrollment ??= _keys.Enroll(Password.ToCharArray());
            return Task.FromResult(new KdfResponse(_enrollment.Argon2Salt, _enrollment.Params));
        }

        public Task<E2eeLoginResponse> LoginAsync(E2eeLoginRequest request, CancellationToken ct = default)
        {
            if (_enrollment is null || !request.AuthHash.SequenceEqual(_enrollment.AuthHash))
            {
                throw new CloudSyncException(HttpStatusCode.Unauthorized);
            }

            return Task.FromResult(new E2eeLoginResponse(
                "access", "refresh", DateTimeOffset.UtcNow.AddHours(1),
                _enrollment.WrappedAmkPwd,
                1,
                [
                    new AccountWorkspace(Pessoal, "Meu cofre", "Owner"),
                    new AccountWorkspace(Time, "Equipe de campo", "Owner"),
                ]));
        }

        public Task<RegisterAccountResponse> RegisterAsync(
            RegisterAccountRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task ForgotPasswordAsync(string email, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<byte[]> GetResetContextAsync(string token, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>A tela de escolha do cofre, contada. É ela que o operador nunca via.</summary>
    private sealed class SpyChooser : IWorkspaceChooser
    {
        public int Asked { get; private set; }

        public Task<AccountWorkspace?> ChooseAsync(
            IReadOnlyList<AccountWorkspace> workspaces, CancellationToken ct = default)
        {
            Asked++;

            // Escolhe o TIME, que é o que o operador está tentando fazer há dias.
            return Task.FromResult<AccountWorkspace?>(
                workspaces.First(w => w.Id == Time));
        }
    }

    private sealed class Cenario
    {
        internal FakeAmkCache Cache { get; } = new();

        internal FakeVaultActivator Activator { get; } = new();

        internal SpyChooser Chooser { get; } = new();

        internal int LoginCalls { get; private set; }

        internal AccountSyncCoordinator Coordinator { get; }

        internal Cenario()
            => Coordinator = new AccountSyncCoordinator(
                Cache, Activator, new NoSyncStarter(), VaultWorkspaces);

        /// <summary>
        /// O login inteiro, com os tipos de produção. Em produção quem constrói o
        /// <c>DialogWorkspaceChooser</c> é o <c>App.ShowAccountWindow</c>, e ele só é chamado deste
        /// ponto — daí contar as chamadas AQUI ser a mesma medida que "a janela apareceu".
        /// </summary>
        internal AccountBootPath NewBootPath() => new(Coordinator, async ct =>
        {
            LoginCalls++;
            var auth = new E2eeAccountAuthenticator(
                new TwoWorkspaceServer(), Device, "PC-DO-OPERADOR", Chooser);
            return await auth.LoginAsync(Email, Password.ToCharArray(), ct: ct);
        });
    }

    // ── A metade que estava quebrada ─────────────────────────────────────────────────────────

    /// <summary>
    /// <b>O bloqueante, fixado.</b> Com <c>account.amk</c> em disco — a máquina de TODO operador que
    /// já usa o app — o boot entra direto: sem rede, sem login e <b>sem a tela de escolha</b>. Era
    /// por isso que "feche e abra e escolha o time" nunca funcionava.
    /// </summary>
    [Fact]
    public async Task ComCacheDaAMK_OChooser_NAO_APARECE()
    {
        var cenario = new Cenario();
        cenario.Cache.Seed(Pessoal);

        AccountBootEntry? entry = await cenario.NewBootPath().EnterAsync();

        Assert.NotNull(entry);
        Assert.Equal(Pessoal, entry!.Activation.WorkspaceId);
        Assert.Equal(0, cenario.LoginCalls);
        Assert.Equal(0, cenario.Chooser.Asked);

        // Sem lista de cofres neste caminho: o cache guarda UM workspace. "Não perguntei" é o valor
        // mais fraco, e é o que a regra 5 do resolvedor de escopo espera receber daqui.
        Assert.Null(entry.WorkspaceCount);
    }

    /// <summary>
    /// A outra metade, que impede "sempre perguntar": sem cache o login roda, a tela de escolha
    /// aparece e <b>o cofre escolhido é o que manda</b> — não o primeiro da lista.
    /// </summary>
    [Fact]
    public async Task SemCacheDaAMK_OChooser_APARECE_EOEscolhidoMANDA()
    {
        var cenario = new Cenario();

        AccountBootEntry? entry = await cenario.NewBootPath().EnterAsync();

        Assert.NotNull(entry);
        Assert.Equal(1, cenario.LoginCalls);
        Assert.Equal(1, cenario.Chooser.Asked);
        Assert.Equal(Time, entry!.Activation.WorkspaceId);
        Assert.Equal(Time, Assert.Single(cenario.Activator.TokenScopes));
        Assert.Equal(2, entry.WorkspaceCount);
    }

    /// <summary>
    /// ⚠️ <b>O H2 inteiro, em um teste.</b> Sair da conta (o que o botão novo faz) apaga o cache — e
    /// a abertura seguinte volta a PERGUNTAR em qual cofre entrar. É esta a ponte entre o botão e a
    /// tela de escolha; sem ela, o botão seria mais um caminho que não leva a lugar nenhum.
    /// </summary>
    [Fact]
    public async Task DepoisDeSairDaConta_OBootVOLTA_APerguntar()
    {
        var cenario = new Cenario();
        cenario.Cache.Seed(Pessoal);

        await cenario.Coordinator.LogoutAsync();
        AccountBootEntry? entry = await cenario.NewBootPath().EnterAsync();

        Assert.Equal(1, cenario.Cache.ClearCount);
        Assert.Equal(1, cenario.Chooser.Asked);
        Assert.Equal(Time, entry!.Activation.WorkspaceId);
    }

    /// <summary>
    /// ⚠️ <b>Cache que NÃO ABRE não é cache: é "sem cache", e o boot tem de PERGUNTAR.</b>
    ///
    /// <para>É a pasta <c>%APPDATA%\RemoteOps</c> restaurada de backup no segundo computador do
    /// operador, ou o perfil do Windows recriado: o <c>account.amk</c> está lá, íntegro, e o DPAPI
    /// se recusa a abri-lo (o contrato do <c>ILocalKeyProtector</c> manda LANÇAR nesse caso).</para>
    ///
    /// <para>Enquanto essa exceção subia daqui, o <c>App</c> a tratava como falha de ativação,
    /// encontrava o cofre já AMK-rooted e ENCERRAVA o processo com "abra o RemoteOps de novo e entre
    /// na conta" — e reabrir relia o mesmo blob. Beco sem saída, com os ~700 equipamentos intactos e
    /// inalcançáveis. A AMK é portável: a senha resolve, e é ela que o login pede.</para>
    /// </summary>
    [Fact]
    public async Task ComCacheQueNAOABRE_OBoot_PERGUNTA_EmVezDeEstourar()
    {
        // O cache é o DE PRODUÇÃO (DpapiAmkCache em arquivo): o que muda entre as duas máquinas é só
        // o protetor. Um dublê de IAmkCache aqui não provaria nada — quem tem de traduzir "o blob
        // não abre" em "sem cache" é ele.
        string dir = Path.Combine(Path.GetTempPath(), "remoteops-boot-dpapi-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            string amkPath = Path.Combine(dir, "account.amk");
            using (var entry = new CachedAccount(Email, Pessoal, 1, SampleAmk()))
            {
                await new DpapiAmkCache(amkPath, new ProtectorTransparente()).SaveAsync(entry);
            }

            var coordinator = new AccountSyncCoordinator(
                new DpapiAmkCache(amkPath, new ProtectorQueRecusa()),
                new FakeVaultActivator(),
                new NoSyncStarter(),
                VaultWorkspaces);

            int logins = 0;
            var path = new AccountBootPath(coordinator, _ =>
            {
                logins++;
                return Task.FromResult<AccountSession?>(null);
            });

            Assert.Null(await path.EnterAsync());
            Assert.Equal(1, logins); // PERGUNTOU — em vez de estourar e o App encerrar
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* limpeza best-effort */ }
        }
    }

    /// <summary>Grava o cache como o login de produção grava (o DPAPI real não roda em teste).</summary>
    private sealed class ProtectorTransparente : ILocalKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
            => plaintext.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBlob, ReadOnlySpan<byte> entropy)
            => protectedBlob.ToArray();
    }

    /// <summary>O DPAPI da OUTRA máquina/perfil, como o contrato do <c>ILocalKeyProtector</c> manda.</summary>
    private sealed class ProtectorQueRecusa : ILocalKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
            => plaintext.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBlob, ReadOnlySpan<byte> entropy)
            => throw new CryptographicException("blob protegido por outro usuário/máquina");
    }

    /// <summary>
    /// Desistir do login não pode virar "entra em algum cofre": devolve <c>null</c>, o App cai no
    /// modo local e NADA é ativado.
    /// </summary>
    [Fact]
    public async Task DesistindoDoLogin_NadaEAtivado()
    {
        var cenario = new Cenario();
        var path = new AccountBootPath(
            cenario.Coordinator, _ => Task.FromResult<AccountSession?>(null));

        Assert.Null(await path.EnterAsync());
        Assert.Empty(cenario.Activator.TokenScopes);
    }
}
