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

        // Criar TIME é fora do escopo destes testes (o alvo aqui é a tela, não o ciclo do
        // workspace). NotSupportedException e não um retorno de mentira: um fake que "cria" faria o
        // teste passar por caminhos que ele não exercita.
        public Task<CreateTeamWorkspaceResponse> CreateWorkspaceAsync(
            CreateTeamWorkspaceRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

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

    private static (SettingsViewModel Vm, TeamInviteService Service, WkWorkspaceKeyRing Ring, FakeTeamApi Api) New()
    {
        var api = new FakeTeamApi();
        var ring = TeamKeyRingFactory.New(RandomNumberGenerator.GetBytes(32));
        var service = new TeamInviteService(api, ring);
        var vm = new SettingsViewModel(
            new FakeStore(), team: new TeamContext(service, api, Workspace));
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
