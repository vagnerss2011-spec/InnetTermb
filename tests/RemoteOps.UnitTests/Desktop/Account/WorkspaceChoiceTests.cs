using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Security.Account;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Account;

/// <summary>
/// Escolher o cofre ao abrir o app. Até aqui o login pegava <c>workspaces[0]</c> — com o time isso
/// vira roleta: o operador cadastraria o host do cliente no cofre pessoal (ou o contrário) sem
/// nunca ter sido perguntado.
///
/// <para>A regra tem DOIS lados e os dois importam: com 2+ workspaces, pergunta; com UM só, não
/// pergunta nada. Uma tela a mais no boot diário de quem nunca vai ter time é atrito puro.</para>
/// </summary>
public sealed class WorkspaceChoiceTests
{
    private const string Password = "senha-forte-123"; // pragma: allowlist secret
    private static readonly Guid Device = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>Servidor mínimo que devolve a LISTA de workspaces que o teste mandar.</summary>
    private sealed class MultiWorkspaceServer : IAccountApi
    {
        private readonly AccountKeyService _keys = new();
        private AccountEnrollment? _enrollment;

        public MultiWorkspaceServer(params AccountWorkspace[] workspaces) => Workspaces = workspaces;

        public IReadOnlyList<AccountWorkspace> Workspaces { get; set; }

        public Task<RegisterAccountResponse> RegisterAsync(
            RegisterAccountRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<KdfResponse> GetKdfAsync(string email, CancellationToken ct = default)
        {
            _enrollment ??= _keys.Enroll(Password);
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
                _enrollment.WrappedAmkPwd, 1, Workspaces));
        }

        public Task ForgotPasswordAsync(string email, CancellationToken ct = default) => Task.CompletedTask;

        public Task<byte[]> GetResetContextAsync(string token, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>Chooser de teste: conta quantas vezes foi perguntado e responde o que mandarem.</summary>
    private sealed class SpyChooser : IWorkspaceChooser
    {
        private readonly Func<IReadOnlyList<AccountWorkspace>, AccountWorkspace?> _pick;

        public SpyChooser(Func<IReadOnlyList<AccountWorkspace>, AccountWorkspace?> pick) => _pick = pick;

        public int Asked { get; private set; }

        public IReadOnlyList<AccountWorkspace>? Offered { get; private set; }

        public Task<AccountWorkspace?> ChooseAsync(
            IReadOnlyList<AccountWorkspace> workspaces, CancellationToken ct = default)
        {
            Asked++;
            Offered = workspaces;
            return Task.FromResult(_pick(workspaces));
        }
    }

    private static char[] Senha() => Password.ToCharArray();

    /// <summary>
    /// <b>Sem atrito.</b> Um workspace só: entra direto e o chooser NEM É CHAMADO. Este teste é o
    /// que protege o boot diário do operador que nunca vai ter time.
    /// </summary>
    [Fact]
    public async Task UmWorkspace_EntraDireto_SemPerguntar()
    {
        var api = new MultiWorkspaceServer(new AccountWorkspace("ws-pessoal", "Innet", "Owner"));
        var chooser = new SpyChooser(_ => throw new InvalidOperationException("não podia perguntar"));
        var auth = new E2eeAccountAuthenticator(api, Device, "PC-1", chooser);

        AccountSession session = await auth.LoginAsync("op@innet.tec.br", Senha());
        try
        {
            Assert.Equal(0, chooser.Asked);
            Assert.Equal("ws-pessoal", session.WorkspaceId);
        }
        finally
        {
            session.ZeroAmk();
        }
    }

    /// <summary>Dois workspaces: pergunta, e o ESCOLHIDO é o que define a sessão (não o primeiro).</summary>
    [Fact]
    public async Task DoisWorkspaces_Pergunta_EUsaOEscolhido()
    {
        var api = new MultiWorkspaceServer(
            new AccountWorkspace("ws-pessoal", "Meu cofre", "Owner"),
            new AccountWorkspace("ws-time", "Innet Telecom", "Manager"));
        var chooser = new SpyChooser(list => list.First(w => w.Id == "ws-time"));
        var auth = new E2eeAccountAuthenticator(api, Device, "PC-1", chooser);

        AccountSession session = await auth.LoginAsync("op@innet.tec.br", Senha());
        try
        {
            Assert.Equal(1, chooser.Asked);
            Assert.Equal(2, chooser.Offered!.Count);
            Assert.Equal("ws-time", session.WorkspaceId);
        }
        finally
        {
            session.ZeroAmk();
        }
    }

    /// <summary>
    /// Desistir NÃO pode virar "abre o primeiro". Abrir o cofre errado em silêncio é o defeito que
    /// esta tela existe para impedir — melhor voltar ao login.
    /// </summary>
    [Fact]
    public async Task Desistir_NaoAbreOPrimeiro_Cancela()
    {
        var api = new MultiWorkspaceServer(
            new AccountWorkspace("ws-pessoal", "Meu cofre", "Owner"),
            new AccountWorkspace("ws-time", "Innet Telecom", "Manager"));
        var auth = new E2eeAccountAuthenticator(api, Device, "PC-1", new SpyChooser(_ => null));

        await Assert.ThrowsAsync<WorkspaceChoiceCancelledException>(
            () => auth.LoginAsync("op@innet.tec.br", Senha()));
    }

    /// <summary>
    /// Sem chooser configurado (modo local/testes antigos), o comportamento de hoje continua: o
    /// primeiro workspace. Compatibilidade — a escolha ADICIONA, não troca.
    /// </summary>
    [Fact]
    public async Task SemChooser_ContinuaComoAntes()
    {
        var api = new MultiWorkspaceServer(
            new AccountWorkspace("ws-pessoal", "Meu cofre", "Owner"),
            new AccountWorkspace("ws-time", "Innet Telecom", "Manager"));
        var auth = new E2eeAccountAuthenticator(api, Device, "PC-1");

        AccountSession session = await auth.LoginAsync("op@innet.tec.br", Senha());
        try
        {
            Assert.Equal("ws-pessoal", session.WorkspaceId);
        }
        finally
        {
            session.ZeroAmk();
        }
    }
}
