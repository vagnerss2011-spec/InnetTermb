using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Security.Account;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Account;

/// <summary>
/// <b>O reparo do boot num cofre de time SEM a chave.</b>
///
/// <para>O estado <c>TeamWithoutKey</c> é o mais incômodo do app: os equipamentos aparecem e nenhuma
/// senha do time abre. O aviso na tela mandava "conecte-se à internet" — mas o único reparo que
/// rodava no boot (<c>PublishOwnWrappedKeyAsync</c>) só <b>PUBLICA</b>, e sem chave local ele sai
/// antes de tocar a rede. Ou seja: conectar não curava nada, e o texto prometia uma cura que não
/// existia. Este é o pior tipo de mentira desta base — a que faz o operador esperar em vez de agir.</para>
///
/// <para>Estes testes fixam as DUAS metades: o boot passa a <b>buscar</b> a chave guardada na conta
/// (<c>TryRestoreTeamKeyAsync</c>), e o desfecho — sucesso <b>e</b> falha — fica ESCRITO no painel de
/// Logs. Silêncio aqui seria a falha muda de sempre, só que no assunto em que o operador mais precisa
/// saber o que aconteceu.</para>
/// </summary>
public sealed class TeamKeyBootRepairTests
{
    private const string WorkspaceDoTime = "9a1c4e2b-0000-4000-8000-0000000000cc";
    private const string WorkspacePessoal = "9a1c4e2b-0000-4000-8000-0000000000dd";

    /// <summary>Painel de Logs de mentira: guarda o que o operador leria na tela.</summary>
    private sealed class RecordingLog : IUiLogSink
    {
        public List<string> Lines { get; } = [];

        public void Emit(string line) => Lines.Add(line);

        public string All => string.Join("\n", Lines);
    }

    private sealed class FakeTeamApi : ITeamApi
    {
        public List<string> Calls { get; } = [];

        /// <summary>O que o servidor guarda para esta conta. <c>null</c> = 404 (nada guardado).</summary>
        public TeamWorkspaceKeyResponse? WorkspaceKey { get; set; }

        /// <summary>O servidor está fora: toda consulta de chave estoura.</summary>
        public bool Offline { get; set; }

        public Task<TeamWorkspaceKeyResponse?> GetWorkspaceKeyAsync(
            string workspaceId, CancellationToken ct = default)
        {
            Calls.Add("get-key");
            return Offline
                ? throw new CloudSyncException(System.Net.HttpStatusCode.ServiceUnavailable)
                : Task.FromResult(WorkspaceKey);
        }

        public Task<TeamKeyPublication> PublishWorkspaceKeyAsync(
            string workspaceId, PublishTeamWorkspaceKeyRequest request, CancellationToken ct = default)
        {
            Calls.Add("publish-key");
            if (Offline)
            {
                throw new CloudSyncException(System.Net.HttpStatusCode.ServiceUnavailable);
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

        public Task<CreateTeamWorkspaceResponse> CreateWorkspaceAsync(
            CreateTeamWorkspaceRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<CreateTeamInviteResponse> CreateInviteAsync(
            string workspaceId, CreateTeamInviteRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

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

    /// <summary>
    /// O embrulho da WK do time <b>como o servidor o guarda</b>: nascido noutro computador da MESMA
    /// conta (mesma AMK). É exatamente o que o segundo PC do operador precisa restaurar.
    /// </summary>
    private static string EmbrulhoNoServidor(byte[] amk)
    {
        using WkWorkspaceKeyRing outroPc = TeamKeyRingFactory.New(amk);
        string cofre = AppRuntime.TeamVaultWorkspace(WorkspaceDoTime);
        using (WorkspaceKey _ = outroPc.MintWorkspaceKeyAsync(cofre).GetAwaiter().GetResult())
        {
        }

        byte[] wrapped = outroPc.TryGetWrappedWorkspaceKeyAsync(cofre).GetAwaiter().GetResult()!;
        return Convert.ToBase64String(wrapped);
    }

    private static (FakeTeamApi Api, WkWorkspaceKeyRing Ring, TeamContext Team, RecordingLog Log) Cenario(
        SessionVaultKind kind, byte[] amk, string workspaceId = WorkspaceDoTime)
    {
        var api = new FakeTeamApi();
        WkWorkspaceKeyRing ring = TeamKeyRingFactory.New(new InMemoryWorkspaceKeyStore(), amk);
        var service = new TeamInviteService(api, ring, kind);
        return (api, ring, new TeamContext(service, api, workspaceId, kind), new RecordingLog());
    }

    /// <summary>
    /// <b>O conserto.</b> Sessão de time sem a chave + chave guardada na conta: o boot BUSCA, o cofre
    /// do time passa a abrir neste computador, e o painel de Logs diz que deu certo.
    /// </summary>
    [Fact]
    public async Task SessaoSEMChave_OBootBUSCA_AChaveGuardadaNaConta()
    {
        byte[] amk = RandomNumberGenerator.GetBytes(32);
        var (api, ring, team, log) = Cenario(SessionVaultKind.TeamWithoutKey, amk);
        using (ring)
        {
            api.WorkspaceKey = new TeamWorkspaceKeyResponse(
                WorkspaceDoTime, EmbrulhoNoServidor(amk), 1);

            TeamVaultReadiness readiness = await new TeamKeyBootRepair(log).RunAsync(team);

            Assert.Equal(TeamVaultReadiness.KeyHere, readiness);

            // A prova que importa não é o enum: é o cofre do time ABRINDO aqui.
            using WorkspaceKey? agora = await ring.TryGetWorkspaceKeyAsync(
                AppRuntime.TeamVaultWorkspace(WorkspaceDoTime));
            Assert.NotNull(agora);

            // E o operador LÊ o desfecho — senão a chave chega e ninguém fica sabendo.
            Assert.Contains("[time]", log.All, StringComparison.Ordinal);
            Assert.Contains("chave", log.All, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// <b>A conta ainda não guarda a chave</b> (o convite não foi aceito, ou o dono nunca publicou).
    /// O boot não tem o que restaurar — e DIZ isso. Silêncio aqui deixaria o operador esperando um
    /// conserto automático que nunca vem.
    /// </summary>
    [Fact]
    public async Task SemChaveNaConta_OBootDIZ_QueNaoHaOQueRestaurar()
    {
        byte[] amk = RandomNumberGenerator.GetBytes(32);
        var (api, ring, team, log) = Cenario(SessionVaultKind.TeamWithoutKey, amk);
        using (ring)
        {
            api.WorkspaceKey = null;

            TeamVaultReadiness readiness = await new TeamKeyBootRepair(log).RunAsync(team);

            Assert.Equal(TeamVaultReadiness.StillWithoutKey, readiness);
            Assert.Contains("get-key", api.Calls);
            Assert.NotEmpty(log.Lines);
            Assert.Contains("convite", log.All, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// <b>Servidor fora.</b> A falha também é desfecho, e também vai para os Logs — com a diferença
    /// de que aqui o operador precisa saber que foi a REDE, e que o app tenta de novo na próxima
    /// abertura. "Não sei" nunca pode virar silêncio nem virar afirmação.
    /// </summary>
    [Fact]
    public async Task ServidorFora_OBootESCREVE_AFalha_EmVezDeEngolir()
    {
        byte[] amk = RandomNumberGenerator.GetBytes(32);
        var (api, ring, team, log) = Cenario(SessionVaultKind.TeamWithoutKey, amk);
        using (ring)
        {
            api.Offline = true;

            TeamVaultReadiness readiness = await new TeamKeyBootRepair(log).RunAsync(team);

            Assert.Equal(TeamVaultReadiness.StillWithoutKey, readiness);
            Assert.NotEmpty(log.Lines);
            Assert.Contains("[time]", log.All, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>A metade que impede "buscar sempre".</b> Numa sessão de time COM a chave, o boot não vai
    /// buscar chave nenhuma — ele publica o embrulho (o reparo do 1e′) e pronto. Uma ida a mais aqui
    /// custaria round-trip em todo boot de quem já está com tudo certo.
    /// </summary>
    [Fact]
    public async Task SessaoDeTIME_ComChaveAqui_NaoBuscaDeNovo_ESoPUBLICA()
    {
        byte[] amk = RandomNumberGenerator.GetBytes(32);
        var (api, ring, team, log) = Cenario(SessionVaultKind.Team, amk);
        using (ring)
        {
            using (WorkspaceKey _ = await ring.MintWorkspaceKeyAsync(
                AppRuntime.TeamVaultWorkspace(WorkspaceDoTime)))
            {
            }

            TeamVaultReadiness readiness = await new TeamKeyBootRepair(log).RunAsync(team);

            Assert.Equal(TeamVaultReadiness.KeyHere, readiness);
            Assert.DoesNotContain("get-key", api.Calls);
            Assert.Contains("publish-key", api.Calls);
            Assert.Contains("registrada", log.All, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// <b>Quem só tem cofre pessoal não paga nada por isto.</b> Nenhuma ida à rede, nenhuma linha no
    /// painel de Logs — é a maioria da frota, e um reparo que os incomodasse seria ruído puro.
    /// </summary>
    [Fact]
    public async Task SessaoPESSOAL_NaoTocaARede_ENaoPoluiOsLogs()
    {
        byte[] amk = RandomNumberGenerator.GetBytes(32);
        var (api, ring, team, log) = Cenario(SessionVaultKind.Personal, amk, WorkspacePessoal);
        using (ring)
        {
            TeamVaultReadiness readiness = await new TeamKeyBootRepair(log).RunAsync(team);

            Assert.Equal(TeamVaultReadiness.NotTeamSession, readiness);
            Assert.Empty(api.Calls);
            Assert.Empty(log.Lines);
        }
    }

    /// <summary>
    /// <b>O texto do aviso parou de mentir.</b> Ele dizia "conecte-se à internet uma vez para o
    /// RemoteOps buscar a chave" enquanto nada no boot buscava chave nenhuma. Agora o boot busca —
    /// mas <b>na abertura</b>, porque o cofre da sessão é decidido uma vez (1i). O aviso precisa
    /// dizer as três coisas verdadeiras: o app tenta a cada abertura, reabrir é o que dá nova
    /// chance, e o desfecho de cada tentativa está escrito no painel de Logs.
    /// </summary>
    [Fact]
    public void OAvisoDaChaveQueFALTA_DizOQueREALMENTE_Resolve()
    {
        string aviso = VaultBadgeViewModel.TeamVaultNotActiveWarning;

        Assert.Contains("abr", aviso, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("de novo", aviso, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aceite o convite", aviso, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Logs", aviso, StringComparison.Ordinal);

        // A promessa antiga, byte a byte: só conectar NÃO resolvia, porque nada no boot buscava a
        // chave. Se alguém a trouxer de volta sem trazer um reparo junto, este teste fica vermelho.
        Assert.DoesNotContain(
            "Conecte-se à internet uma vez para o RemoteOps buscar a chave",
            aviso,
            StringComparison.Ordinal);
    }
}
