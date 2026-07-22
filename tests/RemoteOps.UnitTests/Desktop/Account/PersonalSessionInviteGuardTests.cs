using System;
using System.Collections.Generic;
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
/// <b>A última linha antes de o cadastro do cliente sair da máquina.</b>
///
/// <para>O botão de convite convidava para o workspace ATIVO — que, para o operador de ISP, é o
/// cofre PESSOAL com ~700 clientes. Como o <c>/sync</c> é escopado por workspace + membership, um
/// clique bastava: o colega aceitava e baixava nomes, endereços, grupos e fabricantes inteiros no
/// computador dele. Não é vazamento de senha (essas seguem cifradas), é de CADASTRO — e nenhum
/// indicador de cofre falava sobre isso.</para>
///
/// <para><b>Por que a guarda mora aqui, e não só na tela:</b> a tela é conveniência. O
/// <c>TeamInviteService</c> é o único ponto por onde o convite passa antes de virar rede — e é
/// também onde a chave do time NASCE. Uma guarda mais acima deixaria a chave nascer mesmo assim, e
/// é exatamente esse blob (<c>wk:time:{workspacePessoal}</c>) que envenena o boot seguinte.</para>
///
/// <para>O servidor também recusa sozinho desde o estágio G1 (422 <c>workspace.personal</c>). São
/// duas guardas de propósito: a do servidor sobrevive a um cliente adulterado; a daqui recusa
/// <b>sem tocar a rede</b> e diz ao operador o que fazer, em vez de devolver um HTTP cru.</para>
/// </summary>
public sealed class PersonalSessionInviteGuardTests
{
    /// <summary>O cofre PESSOAL do operador — o que tem os ~700 clientes.</summary>
    private const string WorkspacePessoal = "8f3b6f4a-0000-4000-8000-0000000000aa";

    /// <summary>O workspace do TIME, criado vazio.</summary>
    private const string WorkspaceDoTime = "8f3b6f4a-0000-4000-8000-0000000000bb";

    /// <summary>
    /// Transporte que ANOTA cada chamada. É a asserção que interessa: uma guarda que recusa depois
    /// de já ter falado com o servidor não impede nada — o convite já teria sido gravado.
    /// </summary>
    private sealed class SpyTeamApi : ITeamApi
    {
        public List<string> Calls { get; } = [];

        public Task<CreateTeamWorkspaceResponse> CreateWorkspaceAsync(
            CreateTeamWorkspaceRequest request, CancellationToken ct = default)
        {
            Calls.Add("create-workspace");
            WorkspaceKey = new TeamWorkspaceKeyResponse(request.Id, request.WrappedWk, request.WkVersion);
            return Task.FromResult(new CreateTeamWorkspaceResponse(request.Id, request.Name, "Owner"));
        }

        /// <summary>O convite que chegou ao servidor — e o workspace para o qual ele aponta.</summary>
        public string? InvitedWorkspaceId { get; private set; }

        public Task<CreateTeamInviteResponse> CreateInviteAsync(
            string workspaceId, CreateTeamInviteRequest request, CancellationToken ct = default)
        {
            Calls.Add("create-invite");
            InvitedWorkspaceId = workspaceId;
            return Task.FromResult(new CreateTeamInviteResponse(
                "3c9d1a2b-0000-4000-8000-000000000002", request.Email, request.Role,
                DateTimeOffset.UtcNow.AddDays(7), EmailDelivered: true));
        }

        public TeamWorkspaceKeyResponse? WorkspaceKey { get; set; }

        public Task<TeamWorkspaceKeyResponse?> GetWorkspaceKeyAsync(
            string workspaceId, CancellationToken ct = default)
        {
            Calls.Add("get-key");
            return Task.FromResult(WorkspaceKey);
        }

        /// <summary>
        /// O servidor recusa a publicação porque o workspace é o cofre pessoal (guarda do G1: 422 +
        /// <c>workspace.personal</c>). É o que um PC contaminado pelo botão antigo recebe no boot.
        /// </summary>
        public bool RefusesAsPersonalWorkspace { get; set; }

        public Task<TeamKeyPublication> PublishWorkspaceKeyAsync(
            string workspaceId, PublishTeamWorkspaceKeyRequest request, CancellationToken ct = default)
        {
            Calls.Add("publish-key");
            if (RefusesAsPersonalWorkspace)
            {
                throw new CloudSyncException(
                    HttpStatusCode.UnprocessableEntity, CloudRefusalReasons.PersonalWorkspace);
            }

            if (WorkspaceKey is { } guardado)
            {
                return Task.FromResult(
                    string.Equals(guardado.WrappedWk, request.WrappedWk, StringComparison.Ordinal)
                        ? TeamKeyPublication.AlreadyPublished
                        : TeamKeyPublication.Divergent);
            }

            WorkspaceKey = new TeamWorkspaceKeyResponse(workspaceId, request.WrappedWk, request.WkVersion);
            return Task.FromResult(TeamKeyPublication.Stored);
        }

        // Aceitar convite e administrar membros não fazem parte deste assunto.
        public Task<TeamInviteContextResponse> GetInviteContextAsync(
            string inviteId, string codeHash, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AcceptTeamInviteResponse> AcceptInviteAsync(
            string inviteId, AcceptTeamInviteRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TeamMembersResponse> GetMembersAsync(
            string workspaceId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TeamMemberRemoval> RemoveMemberAsync(
            string workspaceId, string userId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static (SpyTeamApi Api, WkWorkspaceKeyRing Ring, TeamInviteService Service) Sessao(
        SessionVaultKind kind)
    {
        var api = new SpyTeamApi();
        var ring = TeamKeyRingFactory.New(RandomNumberGenerator.GetBytes(32));
        return (api, ring, new TeamInviteService(api, ring, kind));
    }

    /// <summary>
    /// <b>O bloqueante, do lado do cliente.</b> Na sessão do cofre pessoal, convidar RECUSA — e a
    /// recusa é um texto que o operador entende, não um HTTP cru nem uma exceção de cripto.
    /// </summary>
    [Fact]
    public async Task ConvidarNaSessaoPESSOAL_RECUSA_ComMotivoAcionavel()
    {
        var (api, ring, service) = Sessao(SessionVaultKind.Personal);
        using (ring)
        {
            var erro = await Assert.ThrowsAsync<TeamInviteException>(
                () => service.CreateInviteAsync(
                    WorkspacePessoal, "colega@innet.tec.br", TeamRoles.Operator));

            // O recado diz ONDE ele está e O QUE fazer. Sem as duas metades, o operador tenta de
            // novo achando que foi a rede — que é o desfecho que esta fatia inteira combate.
            Assert.Equal(TeamInviteService.PersonalSessionRefusal, erro.Message);
            Assert.Contains("pessoal", erro.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("time", erro.Message, StringComparison.OrdinalIgnoreCase);

            // E nada saiu daqui: nem o convite, nem sequer a pergunta pela chave.
            Assert.Empty(api.Calls);
            Assert.Null(api.InvitedWorkspaceId);
        }
    }

    /// <summary>
    /// <b>A recusa acontece ANTES de a chave nascer.</b> A ordem não é estética: o
    /// <c>CreateInviteAsync</c> faz a WK do time nascer neste computador, e num cofre pessoal isso
    /// grava <c>wk:time:{workspacePessoal}</c> em disco. Esse blob é o que fazia o boot seguinte
    /// classificar o cofre pessoal como "de time" (regra 3 do <c>SessionVaultScopeResolver</c>) —
    /// uma recusa tardia deixaria o estrago plantado mesmo tendo dito "não".
    /// </summary>
    [Fact]
    public async Task ConvidarNaSessaoPESSOAL_NaoDeixaChaveDeTimeNoDiscoDoCofrePessoal()
    {
        var (_, ring, service) = Sessao(SessionVaultKind.Personal);
        using (ring)
        {
            await Assert.ThrowsAsync<TeamInviteException>(
                () => service.CreateInviteAsync(
                    WorkspacePessoal, "colega@innet.tec.br", TeamRoles.Operator));

            string cofreDoTime = AppRuntime.TeamVaultWorkspace(WorkspacePessoal);

            // A forma fica ESCRITA aqui: é este id que o resolvedor de escopo procura no disco, e
            // uma mudança silenciosa nele faria a asserção abaixo passar sem provar nada.
            Assert.Equal("time:" + WorkspacePessoal, cofreDoTime);

            using WorkspaceKey? plantada = await ring.TryGetWorkspaceKeyAsync(cofreDoTime);
            Assert.Null(plantada);
            Assert.Null(await ring.TryGetWrappedWorkspaceKeyAsync(cofreDoTime));
        }
    }

    /// <summary>
    /// <b>A metade que impede "recusar tudo".</b> Numa sessão de TIME o convite funciona, e o
    /// workspace que vai no pedido é o do TIME — nunca outro.
    /// </summary>
    [Fact]
    public async Task ConvidarNaSessaoDoTIME_FUNCIONA_EUsaOWorkspaceDoTIME()
    {
        var (api, ring, service) = Sessao(SessionVaultKind.Team);
        using (ring)
        {
            GeneratedTeamInvite convite = await service.CreateInviteAsync(
                WorkspaceDoTime, "colega@innet.tec.br", TeamRoles.Operator);

            Assert.NotEmpty(convite.Code);
            Assert.Equal(WorkspaceDoTime, api.InvitedWorkspaceId);
            Assert.NotEqual(WorkspacePessoal, api.InvitedWorkspaceId);
            Assert.Contains("create-invite", api.Calls);
        }
    }

    /// <summary>
    /// Cofre do time <b>sem a chave</b> ainda é cofre de time: o convite continua permitido. Barrar
    /// aqui seria trocar um vazamento por um app que não deixa administrar o próprio time enquanto a
    /// chave não desce — e o caminho já é fail-closed por outra via (sem chave local, o serviço vai
    /// ao servidor buscá-la e, se as chaves divergirem, recusa alto).
    /// </summary>
    [Fact]
    public async Task ConvidarNaSessaoDeTimeSEMChave_CONTINUA_Permitido()
    {
        var (api, ring, service) = Sessao(SessionVaultKind.TeamWithoutKey);
        using (ring)
        {
            await service.CreateInviteAsync(WorkspaceDoTime, "colega@innet.tec.br", TeamRoles.Operator);

            Assert.Equal(WorkspaceDoTime, api.InvitedWorkspaceId);
        }
    }

    /// <summary>
    /// <b>Criar time CONTINUA valendo na sessão pessoal</b> — é justamente ali que o operador começa.
    /// E o time nasce com workspace PRÓPRIO: o id sorteado nunca é o do cofre pessoal, então nem o
    /// convite nem o <c>/sync</c> têm como alcançar os ~700 equipamentos dele.
    /// </summary>
    [Fact]
    public async Task CriarTimeNaSessaoPESSOAL_FUNCIONA_ENasceComWorkspaceOUTRO()
    {
        var (api, ring, service) = Sessao(SessionVaultKind.Personal);
        using (ring)
        {
            CreatedTeam time = await service.CreateTeamAsync("Clientes do ISP");

            Assert.NotEqual(WorkspacePessoal, time.WorkspaceId);
            Assert.Equal("time:" + time.WorkspaceId, time.VaultWorkspaceId);
            Assert.Contains("create-workspace", api.Calls);

            // O cofre PESSOAL segue sem nenhuma chave de time — o time é outro workspace, e ponto.
            using WorkspaceKey? noPessoal = await ring.TryGetWorkspaceKeyAsync(
                AppRuntime.TeamVaultWorkspace(WorkspacePessoal));
            Assert.Null(noPessoal);
        }
    }

    /// <summary>
    /// <b>O PC já contaminado por um clique antigo recebe o recado CERTO.</b> Nas versões anteriores
    /// o botão de convite fazia a WK nascer sob <c>time:{workspacePessoal}</c>; no boot seguinte, o
    /// reparo da chave tenta publicá-la e o servidor recusa com 422 <c>workspace.personal</c>. Sem
    /// ler o motivo, o app escrevia <i>"(servidor fora de alcance)"</i> no painel de Logs — errado
    /// sobre a rede, e sobre a única coisa que o operador poderia consertar.
    /// </summary>
    [Fact]
    public async Task PCContaminado_RecebeORecadoCERTO_EmVezDeCulparARede()
    {
        var (api, ring, service) = Sessao(SessionVaultKind.Personal);
        using (ring)
        {
            // Encena o estrago do build anterior: chave de time gravada sob o cofre PESSOAL.
            using (WorkspaceKey _ = await ring
                .MintWorkspaceKeyAsync(AppRuntime.TeamVaultWorkspace(WorkspacePessoal)))
            {
            }

            api.RefusesAsPersonalWorkspace = true;

            var erro = await Assert.ThrowsAsync<TeamInviteException>(
                () => service.PublishOwnWrappedKeyAsync(WorkspacePessoal));

            Assert.Contains("COFRE PESSOAL", erro.Message, StringComparison.Ordinal);
            Assert.Contains("Nada foi perdido", erro.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("fora de alcance", erro.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// O contexto que a UI consome não entrega "o workspace do time" quando não existe time nenhum:
    /// na sessão pessoal ele é <c>null</c>. Quem esquecer a checagem falha na hora, em vez de
    /// convidar alguém para o acervo do operador.
    /// </summary>
    [Fact]
    public void OContextoNaoEntregaWorkspaceDeTimeNaSessaoPessoal()
    {
        var (api, ring, service) = Sessao(SessionVaultKind.Personal);
        using (ring)
        {
            var pessoal = new TeamContext(service, api, WorkspacePessoal, SessionVaultKind.Personal);
            Assert.False(pessoal.IsTeamSession);
            Assert.Null(pessoal.TeamWorkspaceId);

            var time = new TeamContext(service, api, WorkspaceDoTime, SessionVaultKind.Team);
            Assert.True(time.IsTeamSession);
            Assert.Equal(WorkspaceDoTime, time.TeamWorkspaceId);

            var semChave = new TeamContext(
                service, api, WorkspaceDoTime, SessionVaultKind.TeamWithoutKey);
            Assert.True(semChave.IsTeamSession);
            Assert.Equal(WorkspaceDoTime, semChave.TeamWorkspaceId);
        }
    }
}
