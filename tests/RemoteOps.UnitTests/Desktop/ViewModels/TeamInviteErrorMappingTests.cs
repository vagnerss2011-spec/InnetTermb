using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Security.Account;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// <b>O recado que a janela de convite mostra quando o servidor recusa</b> — e, em particular, por
/// que o 422 é reconhecido pelo MOTIVO e não pelo número.
///
/// <para>O status 422 nasceu nesta fatia com um desfecho só (<c>workspace.personal</c>), então casar
/// o número funciona <i>hoje</i>. Ele para de funcionar no dia em que um segundo desfecho usar 422 —
/// que é o uso normal desse status: pedido bem formado, conteúdo inválido. A partir dali o operador
/// leria <i>"você está no seu cofre pessoal, crie um time"</i> para um e-mail digitado errado, iria
/// criar um segundo time, e o time certo continuaria sem o convite. O cliente já LÊ o
/// <c>reason</c> do ProblemDetails (<c>TeamApiClient.FailAsync</c>) e o serviço já casa por ele
/// (<c>TeamInviteService.PublishOwnWrappedKeyAsync</c>): a tela era o único ponto que ainda afirmava
/// um motivo que não leu.</para>
///
/// <para><c>Reason</c> <c>null</c> significa <b>"o servidor não disse"</b> — nunca "não é aquele
/// motivo" (está escrito no próprio <see cref="CloudSyncException.Reason"/>). Por isso o caso sem
/// motivo cai no recado GENÉRICO com o número: ele não afirma nada que o app não sabe.</para>
/// </summary>
public sealed class TeamInviteErrorMappingTests
{
    private const string WorkspaceDoTime = "8f3b6f4a-0000-4000-8000-0000000000bb";

    /// <summary>
    /// Servidor de time que só sabe fazer uma coisa: recusar o convite com o status e o motivo que o
    /// teste mandar. As demais rotas respondem o mínimo para o fluxo chegar até o POST do convite.
    /// </summary>
    private sealed class RecusaTeamApi(HttpStatusCode status, string? reason) : ITeamApi
    {
        private TeamWorkspaceKeyResponse? _key;

        public Task<CreateTeamInviteResponse> CreateInviteAsync(
            string workspaceId, CreateTeamInviteRequest request, CancellationToken ct = default)
            => throw new CloudSyncException(status, reason);

        public Task<CreateTeamWorkspaceResponse> CreateWorkspaceAsync(
            CreateTeamWorkspaceRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TeamWorkspaceKeyResponse?> GetWorkspaceKeyAsync(
            string workspaceId, CancellationToken ct = default)
            => Task.FromResult(_key);

        public Task<TeamKeyPublication> PublishWorkspaceKeyAsync(
            string workspaceId, PublishTeamWorkspaceKeyRequest request, CancellationToken ct = default)
        {
            // Como o backend real: sem embrulho guardado, o primeiro PUT grava.
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

    /// <summary>Sessão de TIME (é a única em que o convite chega à rede) com o servidor recusando.</summary>
    private static async Task<(string Erro, WkWorkspaceKeyRing Ring)> ConvidarComRecusaAsync(
        HttpStatusCode status, string? reason)
    {
        var api = new RecusaTeamApi(status, reason);
        WkWorkspaceKeyRing ring = TeamKeyRingFactory.New(RandomNumberGenerator.GetBytes(32));
        var service = new TeamInviteService(api, ring, SessionVaultKind.Team);
        var team = new TeamContext(service, api, WorkspaceDoTime, SessionVaultKind.Team);

        var vm = new TeamInviteViewModel(team, TeamInviteMode.Generate, copyToClipboard: _ => { })
        {
            Email = "colega@innet.tec.br",
        };

        await vm.GenerateAsync();
        return (vm.ErrorMessage, ring);
    }

    /// <summary>
    /// <b>O achado.</b> Um 422 sem motivo é validação — e o app não tem como saber qual. Afirmar
    /// "você está no seu cofre pessoal" aqui manda o operador criar um segundo time para resolver um
    /// e-mail digitado errado, e o time certo segue sem o convite.
    /// </summary>
    [Fact]
    public async Task Erro422_SemMotivo_NAO_AfirmaQueEhCofrePessoal()
    {
        (string erro, WkWorkspaceKeyRing ring) =
            await ConvidarComRecusaAsync(HttpStatusCode.UnprocessableEntity, reason: null);
        using (ring)
        {
            Assert.NotEqual(TeamInviteService.PersonalSessionRefusal, erro);

            // E o recado genérico ainda diz o que houve: recusa do servidor, com o número. Um erro
            // sem texto nenhum seria a falha silenciosa por outro caminho.
            Assert.Contains("422", erro, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Mesmo raciocínio com um motivo QUE EXISTE e não é este. O <c>reason</c> é vocabulário fechado
    /// e aditivo: um motivo novo do servidor não pode ser lido como o único que o cliente conhecia.
    /// </summary>
    [Fact]
    public async Task Erro422_ComOUTRO_Motivo_NAO_AfirmaQueEhCofrePessoal()
    {
        (string erro, WkWorkspaceKeyRing ring) = await ConvidarComRecusaAsync(
            HttpStatusCode.UnprocessableEntity, reason: "invite.email-invalid");
        using (ring)
        {
            Assert.NotEqual(TeamInviteService.PersonalSessionRefusal, erro);
            Assert.Contains("422", erro, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>A metade que impede "generalizar tudo".</b> Com o motivo que o servidor realmente manda
    /// (guarda do G1), a tela continua dizendo a frase acionável — que é o ponto de ela existir.
    /// </summary>
    [Fact]
    public async Task Erro422_ComOMotivoDoServidor_DIZ_QueEhCofrePessoal()
    {
        (string erro, WkWorkspaceKeyRing ring) = await ConvidarComRecusaAsync(
            HttpStatusCode.UnprocessableEntity, CloudRefusalReasons.PersonalWorkspace);
        using (ring)
        {
            Assert.Equal(TeamInviteService.PersonalSessionRefusal, erro);
        }
    }

    /// <summary>
    /// O motivo sozinho também não basta: ele só vale no status em que o servidor o emite. Um
    /// <c>reason</c> ecoado noutro status é resposta que este cliente não entende, e a frase
    /// específica seria de novo uma afirmação sem lastro.
    /// </summary>
    [Fact]
    public async Task OutroStatus_ComOMesmoMotivo_NAO_ViraARecusaDeCofrePessoal()
    {
        (string erro, WkWorkspaceKeyRing ring) = await ConvidarComRecusaAsync(
            HttpStatusCode.BadRequest, CloudRefusalReasons.PersonalWorkspace);
        using (ring)
        {
            Assert.NotEqual(TeamInviteService.PersonalSessionRefusal, erro);
            Assert.Contains("400", erro, StringComparison.Ordinal);
        }
    }
}
