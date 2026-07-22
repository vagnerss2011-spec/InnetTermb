using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Security.Account;
using RemoteOps.Security.Storage;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// A seção de Equipe das Configurações (Fatia 1e) e — o que importa aqui — a reavaliação do
/// indicador de cofre.
///
/// <para><b>A janela de falha que estes testes fecham:</b> gerar o PRIMEIRO convite é o ato que faz
/// a chave do time nascer neste computador, ou seja, é exatamente aí que o workspace ativo passa a
/// ser "de time". Se o indicador só fosse calculado no boot, ele continuaria dizendo "cofre pessoal",
/// sem o aviso, até o próximo reinício — no pior momento possível: o operador acabou de convidar
/// alguém e vai começar a cadastrar achando que já está compartilhando.</para>
/// </summary>
public sealed class SettingsViewModelTeamTests
{
    private const string Workspace = "8f3b6f4a-0000-4000-8000-000000000001";

    private sealed class FakeStore : ISettingsStore
    {
        private AppSettings _current = new();

        public AppSettings Load() => _current;

        public void Save(AppSettings settings) => _current = settings;
    }

    /// <summary>
    /// Servidor de time mínimo: aceita o convite e não tem chave guardada até alguém publicar a
    /// dela. O <c>/key</c> só passa a responder depois do <c>PUT</c> — como no backend real, onde o
    /// dono do time recém-criado ainda não tem embrulho nenhum.
    /// </summary>
    private sealed class FakeTeamApi : ITeamApi
    {
        private TeamWorkspaceKeyResponse? _key;

        public bool KeyEndpointOffline { get; set; }

        /// <summary>O time criado, para o teste conferir sem adivinhar formato.</summary>
        public CreateTeamWorkspaceRequest? CreatedWorkspace { get; private set; }

        public Task<CreateTeamWorkspaceResponse> CreateWorkspaceAsync(
            CreateTeamWorkspaceRequest request, CancellationToken ct = default)
        {
            CreatedWorkspace = request;

            // Como o backend real: a membership Owner nasce COM o embrulho da chave.
            _key = new TeamWorkspaceKeyResponse(request.Id, request.WrappedWk, request.WkVersion);
            return Task.FromResult(new CreateTeamWorkspaceResponse(request.Id, request.Name, "Owner"));
        }

        public Task<CreateTeamInviteResponse> CreateInviteAsync(
            string workspaceId, CreateTeamInviteRequest request, CancellationToken ct = default)
            => Task.FromResult(new CreateTeamInviteResponse(
                "3c9d1a2b-0000-4000-8000-000000000002", request.Email, request.Role,
                DateTimeOffset.UtcNow.AddDays(7), EmailDelivered: true));

        public Task<TeamWorkspaceKeyResponse?> GetWorkspaceKeyAsync(
            string workspaceId, CancellationToken ct = default)
            => KeyEndpointOffline
                ? throw new HttpRequestException("rede fora (teste)")
                : Task.FromResult(_key);

        public Task<TeamKeyPublication> PublishWorkspaceKeyAsync(
            string workspaceId, PublishTeamWorkspaceKeyRequest request, CancellationToken ct = default)
        {
            if (KeyEndpointOffline)
            {
                throw new HttpRequestException("rede fora (teste)");
            }

            if (_key is { } guardado)
            {
                return Task.FromResult(
                    string.Equals(guardado.WrappedWk, request.WrappedWk, StringComparison.Ordinal)
                        ? TeamKeyPublication.AlreadyPublished
                        : TeamKeyPublication.Divergent);
            }

            _key = new TeamWorkspaceKeyResponse(workspaceId, request.WrappedWk, request.WkVersion);
            return Task.FromResult(TeamKeyPublication.Stored);
        }

        public Task<TeamInviteContextResponse> GetInviteContextAsync(
            string inviteId, string codeHash, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AcceptTeamInviteResponse> AcceptInviteAsync(
            string inviteId, AcceptTeamInviteRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TeamMembersResponse> GetMembersAsync(
            string workspaceId, CancellationToken ct = default)
            => Task.FromResult(new TeamMembersResponse([]));

        public Task<TeamMemberRemoval> RemoveMemberAsync(
            string workspaceId, string userId, CancellationToken ct = default)
            => Task.FromResult(TeamMemberRemoval.Removed);
    }

    private static (SettingsViewModel Vm, TeamInviteService Service, WkWorkspaceKeyRing Ring, FakeTeamApi Api) New(
        SessionVaultKind sessionKind = SessionVaultKind.Team)
    {
        var api = new FakeTeamApi();
        var ring = TeamKeyRingFactory.New(RandomNumberGenerator.GetBytes(32));
        var service = new TeamInviteService(api, ring, sessionKind);
        var vm = new SettingsViewModel(
            new FakeStore(), team: new TeamContext(service, api, Workspace, sessionKind));
        return (vm, service, ring, api);
    }

    /// <summary>
    /// <b>O teste que fecha a janela.</b> Antes do primeiro convite o workspace é pessoal; depois do
    /// convite ele é de time — e o indicador acompanha, sem reiniciar o app.
    /// </summary>
    [Fact]
    public async Task DepoisDoPrimeiroConvite_OIndicadorPassaAAvisar_SemReiniciar()
    {
        var (vm, service, ring, _) = New();
        using (ring)
        {
            await vm.RefreshVaultScopeAsync();
            Assert.Equal(VaultScope.Personal, vm.VaultBadge.Scope);
            Assert.False(vm.VaultBadge.IsWarning);

            // Convidar é o ato que faz a chave do time nascer neste PC.
            await service.CreateInviteAsync(Workspace, "colega@innet.tec.br", TeamRoles.Operator);

            await vm.RefreshVaultScopeAsync();

            Assert.Equal(VaultScope.TeamPending, vm.VaultBadge.Scope);
            Assert.True(vm.VaultBadge.IsWarning);
        }
    }

    /// <summary>Sem conta na nuvem não há sondagem — e o estado local continua sendo a verdade.</summary>
    [Fact]
    public async Task SemConta_OIndicadorFicaNoLocal()
    {
        var vm = new SettingsViewModel(new FakeStore());

        await vm.RefreshVaultScopeAsync();

        Assert.Equal(VaultScope.LocalOnly, vm.VaultBadge.Scope);
        Assert.False(vm.CanManageTeam);
        Assert.False(vm.ManageTeamCommand.CanExecute(null));
    }

    /// <summary>
    /// Rede fora na reavaliação vira "não confirmado" na tela — nunca um "cofre pessoal" afirmado
    /// com uma confiança que o app não tem.
    /// </summary>
    [Fact]
    public async Task SemRede_OIndicadorFicaNaoConfirmado()
    {
        var (vm, _, ring, api) = New();
        using (ring)
        {
            api.KeyEndpointOffline = true;

            await vm.RefreshVaultScopeAsync();

            Assert.Equal(VaultScope.Unconfirmed, vm.VaultBadge.Scope);
            Assert.True(vm.VaultBadge.IsUnconfirmed);
        }
    }

    /// <summary>Com conta na nuvem, o botão que abre a tela de Equipe existe e está habilitado.</summary>
    [Fact]
    public void ComConta_OBotaoDaTelaDeEquipe_EstaHabilitado()
    {
        var (vm, _, ring, _) = New();
        using (ring)
        {
            Assert.True(vm.CanManageTeam);
            Assert.True(vm.ManageTeamCommand.CanExecute(null));

            int pedidos = 0;
            vm.TeamManagementRequested += (_, _) => pedidos++;
            vm.ManageTeamCommand.Execute(null);

            Assert.Equal(1, pedidos);
        }
    }

    // ── A verdade sobre ONDE o operador está (G2) ────────────────────────────────────────

    /// <summary>
    /// <b>Na sessão do cofre PESSOAL a tela não oferece convite.</b> O botão convidava para o
    /// workspace ATIVO — o cofre com os ~700 clientes do operador — e o convidado baixaria o cadastro
    /// inteiro. Um botão que só sabe recusar é pior que nenhum: ele ensina o operador que "às vezes
    /// dá erro", e é assim que a recusa vira ruído.
    /// </summary>
    [Fact]
    public void SessaoPESSOAL_NaoOfereceConvite_NemAListaDeMembros()
    {
        var (vm, _, ring, _) = New(SessionVaultKind.Personal);
        using (ring)
        {
            Assert.True(vm.CanManageTeam);       // a seção existe: há conta na nuvem.
            Assert.False(vm.IsTeamSession);
            Assert.False(vm.InviteToTeamCommand.CanExecute(null));
            Assert.False(vm.ManageTeamCommand.CanExecute(null));

            // …e a tela DIZ por quê, com o próximo passo. Sumir sem explicação seria a mesma falha
            // silenciosa por outro caminho.
            Assert.True(vm.HasTeamScopeNotice);
            Assert.Contains("pessoal", vm.TeamScopeNotice, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("crie um", vm.TeamScopeNotice, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A metade que impede "esconder tudo": na sessão de TIME o convite e a lista de membros são
    /// oferecidos, e o aviso de escopo some (aviso permanente é aviso que ninguém lê).
    /// </summary>
    [Fact]
    public void SessaoDeTIME_OfereceConviteEAListaDeMembros()
    {
        var (vm, _, ring, _) = New(SessionVaultKind.Team);
        using (ring)
        {
            Assert.True(vm.IsTeamSession);
            Assert.True(vm.InviteToTeamCommand.CanExecute(null));
            Assert.True(vm.ManageTeamCommand.CanExecute(null));
            Assert.False(vm.HasTeamScopeNotice);
        }
    }

    /// <summary>
    /// Cofre de time SEM a chave ainda é time: a administração continua acessível. Barrar aqui
    /// deixaria o operador sem como administrar o próprio time enquanto a chave não desce.
    /// </summary>
    [Fact]
    public void SessaoDeTimeSEMChave_CONTINUA_OferecendoAAdministracao()
    {
        var (vm, _, ring, _) = New(SessionVaultKind.TeamWithoutKey);
        using (ring)
        {
            Assert.True(vm.IsTeamSession);
            Assert.True(vm.InviteToTeamCommand.CanExecute(null));
            Assert.False(vm.HasTeamScopeNotice);
        }
    }

    /// <summary>
    /// <b>Criar time é oferecido na sessão PESSOAL</b> — é de onde todo operador parte, e criar não
    /// compartilha nada (nasce um workspace novo e vazio).
    /// </summary>
    [Fact]
    public void CriarTime_EOferecido_InclusiveNaSessaoPessoal()
    {
        var (vm, _, ring, _) = New(SessionVaultKind.Personal);
        using (ring)
        {
            Assert.True(vm.CreateTeamCommand.CanExecute(null));

            TeamInviteMode? pedido = null;
            vm.TeamInviteRequested += (_, mode) => pedido = mode;
            vm.CreateTeamCommand.Execute(null);

            Assert.Equal(TeamInviteMode.CreateTeam, pedido);
        }
    }

    /// <summary>
    /// <b>Criar o time chama o serviço, e o time nasce VAZIO.</b> O workspace criado é OUTRO (nunca
    /// o ativo), e o recado que fica na tela diz isso com todas as letras: quem tem ~700 clientes
    /// cadastrados precisa saber, antes de procurar, que eles não foram junto.
    /// </summary>
    [Fact]
    public async Task CriarTime_ChamaOServico_EDizQueONovoTimeEstaVAZIO()
    {
        var (vm, _, ring, api) = New(SessionVaultKind.Personal);
        using (ring)
        {
            var janela = new TeamInviteViewModel(vm.Team!, TeamInviteMode.CreateTeam);
            CreatedTeam? criado = null;
            janela.TeamCreated += (_, time) => criado = time;

            janela.TeamName = "Clientes do ISP";
            await janela.CreateTeamAsync();

            Assert.False(janela.HasError, janela.ErrorMessage);
            Assert.NotNull(api.CreatedWorkspace);
            Assert.Equal("Clientes do ISP", api.CreatedWorkspace.Name);

            // O time é um workspace PRÓPRIO: nunca o ativo (o cofre pessoal do operador).
            Assert.NotEqual(Workspace, api.CreatedWorkspace.Id);

            Assert.NotNull(criado);
            Assert.Equal(api.CreatedWorkspace.Id, criado.WorkspaceId);
            Assert.Equal("time:" + criado.WorkspaceId, criado.VaultWorkspaceId);

            Assert.Contains("VAZIO", janela.StatusMessage, StringComparison.Ordinal);
            Assert.Contains("cofre pessoal", janela.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Sem nome, nada é enviado — e o operador lê o motivo em vez de ver um botão inerte.</summary>
    [Fact]
    public async Task CriarTime_SemNome_NaoEnviaNada_EDizOQueFalta()
    {
        var (vm, _, ring, api) = New(SessionVaultKind.Personal);
        using (ring)
        {
            var janela = new TeamInviteViewModel(vm.Team!, TeamInviteMode.CreateTeam);

            await janela.CreateTeamAsync();

            Assert.True(janela.HasError);
            Assert.Contains("nome", janela.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Null(api.CreatedWorkspace);
        }
    }

    /// <summary>
    /// O indicador da tela de Equipe é a MESMA instância do shell. Duas cópias divergiriam, e a tela
    /// de Equipe é justamente onde a divergência seria mais cara de acreditar.
    /// </summary>
    [Fact]
    public void OIndicadorDaTelaDeEquipe_EODoShell()
    {
        var browser = new BrowserViewModel(
            new HostsViewModel(new InMemoryLocalStore(), NewLauncher(), "ws-local"),
            new KeychainViewModel(new InMemoryLocalStore(), new FakeVault(), "ws-local", "ws-local"),
            new LogsViewModel());
        var workspace = new WorkspaceViewModel(browser, new TabsViewModel());

        SettingsViewModel settings = workspace.CreateSettingsViewModel();

        Assert.Same(browser.Vault, settings.VaultBadge);
    }

    private static RemoteOps.Desktop.Sessions.SessionLauncher NewLauncher() =>
        new(new TabsViewModel(), winBox: null, flags: null, ssh: null, telnet: null, rdp: null, rdpCred: null);
}
