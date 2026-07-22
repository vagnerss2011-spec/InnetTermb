using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Security.Account;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Sync.Remote;
using RemoteOps.UnitTests.Sync;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Account;

/// <summary>
/// Os dois lados do convite, com a cripto de verdade: o dono embrulha a WK do time sob a chave
/// derivada do código, e o convidado a reabre e a re-embrulha sob a PRÓPRIA AMK.
///
/// <para><b>O que estes testes guardam, acima de tudo:</b> que o código nunca chega ao servidor
/// (senão o E2EE morre em silêncio) e que a WK entra na máquina do convidado ANTES de qualquer
/// operação de cofre (senão o ring sorteia uma chave aleatória e o cofre do time bifurca).</para>
/// </summary>
public sealed class TeamInviteServiceTests
{
    private const string Workspace = "8f3b6f4a-0000-4000-8000-000000000001";
    private const string InviteId = "3c9d1a2b-0000-4000-8000-000000000002";

    private static byte[] Amk() => RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// Servidor de time em memória que imita as regras que o CLIENTE enxerga do backend real
    /// (<c>RemoteOps.Cloud/Teams/InviteService.cs</c>): compara HASHES, recusa tudo com a mesma cara
    /// (400 genérico) e nunca vê o código. Registra a ORDEM das chamadas — é o que prova a sequência
    /// do aceite.
    /// </summary>
    private sealed class FakeTeamApi : ITeamApi
    {
        private CreateTeamInviteRequest? _invite;

        public List<string> Calls { get; } = [];

        /// <summary>Tudo o que o cliente mandou no corpo — a superfície onde um vazamento apareceria.</summary>
        public List<string> BodyStrings { get; } = [];

        public AcceptTeamInviteRequest? Accepted { get; private set; }

        /// <summary>O corpo do convite criado — de onde o teste tira o blob sem adivinhar formato.</summary>
        public CreateTeamInviteRequest? Created => _invite;

        /// <summary>
        /// Avaliado NO INSTANTE do aceite. Checar depois não serviria: o que interessa é se a WK já
        /// estava no ring naquele momento, e é exatamente isso que a ordem errada estragaria.
        /// </summary>
        public Func<bool>? ProbeOnAccept { get; set; }

        public bool KeyPresentAtAccept { get; private set; }

        public bool Expired { get; set; }

        public string WorkspaceName { get; set; } = "Innet Telecom";

        public bool SessionRefreshRequired { get; set; }

        /// <summary>Ids já usados — é o que faz o 409 do servidor real ser reproduzível aqui.</summary>
        public HashSet<string> ExistingWorkspaceIds { get; } = [];

        /// <summary>O time criado nesta sessão, para o teste conferir sem adivinhar formato.</summary>
        public CreateTeamWorkspaceRequest? CreatedWorkspace { get; private set; }

        /// <summary>
        /// O servidor recusa a criação do time. <c>null</c> = aceita. Existe para exercitar o
        /// desfecho em que a WK já nasceu em disco e o workspace NÃO existe — o estado que o
        /// <c>CreateTeamAsync</c> tem de denunciar em vez de fingir sucesso.
        /// </summary>
        public HttpStatusCode? WorkspaceCreationRefusal { get; set; }

        public Task<CreateTeamWorkspaceResponse> CreateWorkspaceAsync(
            CreateTeamWorkspaceRequest request, CancellationToken ct = default)
        {
            Calls.Add("create-workspace");
            BodyStrings.AddRange([request.Id, request.Name, request.WrappedWk]);
            if (WorkspaceCreationRefusal is { } status)
            {
                throw new CloudSyncException(status);
            }

            if (!ExistingWorkspaceIds.Add(request.Id))
            {
                throw new CloudSyncException(HttpStatusCode.Conflict);
            }

            CreatedWorkspace = request;

            // Como no backend real: a membership Owner nasce COM o embrulho, então o GET da chave
            // responde no instante seguinte.
            WorkspaceKey = new TeamWorkspaceKeyResponse(request.Id, request.WrappedWk, request.WkVersion);
            return Task.FromResult(new CreateTeamWorkspaceResponse(request.Id, request.Name, "Owner"));
        }

        public Task<CreateTeamInviteResponse> CreateInviteAsync(
            string workspaceId, CreateTeamInviteRequest request, CancellationToken ct = default)
        {
            Calls.Add("create");
            BodyStrings.AddRange([request.Email, request.Role, request.CodeHash, request.WrappedWkByInvite]);
            _invite = request;
            return Task.FromResult(new CreateTeamInviteResponse(
                InviteId, request.Email, request.Role, DateTimeOffset.UtcNow.AddDays(7), EmailDelivered: true));
        }

        public Task<TeamInviteContextResponse> GetInviteContextAsync(
            string inviteId, string codeHash, CancellationToken ct = default)
        {
            Calls.Add("context");
            BodyStrings.Add(codeHash);

            // Recusa ÚNICA, como o servidor real: expirado, código errado e inexistente têm a mesma cara.
            if (_invite is null || Expired || !string.Equals(_invite.CodeHash, codeHash, StringComparison.Ordinal))
            {
                throw new CloudSyncException(HttpStatusCode.BadRequest);
            }

            return Task.FromResult(new TeamInviteContextResponse(
                Workspace, WorkspaceName, _invite.Role, _invite.WrappedWkByInvite, _invite.WkVersion));
        }

        public Task<AcceptTeamInviteResponse> AcceptInviteAsync(
            string inviteId, AcceptTeamInviteRequest request, CancellationToken ct = default)
        {
            Calls.Add("accept");
            BodyStrings.AddRange([request.CodeHash, request.WrappedWk]);
            KeyPresentAtAccept = ProbeOnAccept?.Invoke() ?? false;
            Accepted = request;
            return Task.FromResult(new AcceptTeamInviteResponse(
                Workspace, WorkspaceName, _invite!.Role, _invite.WkVersion, SessionRefreshRequired));
        }

        /// <summary>O que <c>GET /workspaces/{id}/key</c> devolve. <c>null</c> = 404 = cofre pessoal.</summary>
        public TeamWorkspaceKeyResponse? WorkspaceKey { get; set; }

        /// <summary>Servidor fora do ar na hora de perguntar a chave (a rede cai em campo).</summary>
        public bool KeyEndpointOffline { get; set; }

        public Task<TeamWorkspaceKeyResponse?> GetWorkspaceKeyAsync(
            string workspaceId, CancellationToken ct = default)
        {
            Calls.Add("key");
            if (KeyEndpointOffline)
            {
                throw new HttpRequestException("rede indisponível (teste)");
            }

            return Task.FromResult(WorkspaceKey);
        }

        /// <summary>
        /// Mesma regra do backend real (<c>TeamService.PublishWorkspaceKeyAsync</c>): a primeira
        /// gravação vence, o MESMO blob de novo é no-op e um blob DIFERENTE é conflito. O servidor
        /// compara BYTES porque é só o que ele pode fazer — ele não tem AMK nenhuma para saber se
        /// dois embrulhos guardam a mesma chave.
        /// </summary>
        public Task<TeamKeyPublication> PublishWorkspaceKeyAsync(
            string workspaceId, PublishTeamWorkspaceKeyRequest request, CancellationToken ct = default)
        {
            Calls.Add("publish");
            BodyStrings.Add(request.WrappedWk);
            if (KeyEndpointOffline)
            {
                throw new HttpRequestException("rede indisponível (teste)");
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

        // Membros: fora do escopo destes testes (o alvo aqui é a cripto do convite).
        public Task<TeamMembersResponse> GetMembersAsync(
            string workspaceId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TeamMemberRemoval> RemoveMemberAsync(
            string workspaceId, string userId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed record Side(
        FakeTeamApi Api,
        WkWorkspaceKeyRing Ring,
        TeamInviteService Service,
        byte[] Amk,
        FakeSyncMetadataStore Metadata);

    /// <summary>
    /// Um lado (dono ou convidado) com chaveiro próprio. Desde o 1h não há mais bandeira de
    /// "pode criar chave": o fail-closed é do TIPO — o cofre enxerga só o <c>IWorkspaceKeyRing</c>,
    /// que não tem como fazer chave nascer, e quem funda o time chama o tipo concreto.
    /// </summary>
    /// <param name="sessionKind">
    /// O cofre que a sessão abriu. O default é <see cref="SessionVaultKind.Team"/> porque é o que
    /// estes testes encenam: o <c>Workspace</c> daqui É o workspace do time. A sessão PESSOAL tem
    /// arquivo próprio (<c>PersonalSessionInviteGuardTests</c>), onde o assunto é a RECUSA.
    /// </param>
    private static Side NewSide(
        FakeTeamApi? api = null,
        byte[]? amk = null,
        SessionVaultKind sessionKind = SessionVaultKind.Team)
    {
        api ??= new FakeTeamApi();
        amk ??= Amk();
        var ring = TeamKeyRingFactory.New(amk);
        var metadata = new FakeSyncMetadataStore();
        return new Side(
            api, ring, new TeamInviteService(api, ring, sessionKind, metadata), amk, metadata);
    }

    // ── Criar o TIME (1g/1h) ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>O time nasce como workspace PRÓPRIO, e já com a chave publicada.</b> É esta ordem que
    /// mantém os ~700 equipamentos do operador fora do alcance do convidado: o convite passa a ser
    /// emitido contra um workspace novo e vazio, e não contra o pessoal do dono (onde o
    /// <c>/sync</c> entregaria nomes de cliente, IPs, grupos e vendors inteiros).
    ///
    /// <para>E a chave sobe no MESMO pedido que cria o workspace: o time nunca existe sem embrulho
    /// guardado — o estado em que o segundo computador do dono sortearia outra WK.</para>
    /// </summary>
    [Fact]
    public async Task CriarTime_NasceComOWorkspaceProprio_EComAChaveJaGuardadaNoServidor()
    {
        var api = new FakeTeamApi();
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing _ = dono.Ring;

        CreatedTeam time = await dono.Service.CreateTeamAsync("Clientes do ISP");

        Assert.Equal("Clientes do ISP", time.Name);
        Assert.True(Guid.TryParse(time.WorkspaceId, out Guid _1));
        Assert.NotEqual(Guid.Empty, _1);
        Assert.Equal("time:" + time.WorkspaceId, time.VaultWorkspaceId);

        // O embrulho da chave subiu junto — o GET responde no instante seguinte.
        Assert.NotNull(api.CreatedWorkspace);
        Assert.Equal(time.WorkspaceId, api.CreatedWorkspace.Id);
        Assert.Equal(api.CreatedWorkspace.WrappedWk, api.WorkspaceKey!.WrappedWk);

        // E a chave existe LOCALMENTE, sob a identidade prefixada do cofre do time.
        using WorkspaceKey? local = await dono.Ring.TryGetWorkspaceKeyAsync(time.VaultWorkspaceId);
        Assert.NotNull(local);
        Assert.Equal(32, local.Key.Length);
    }

    /// <summary>
    /// A WK nunca vai em claro para o servidor — nem no pedido que cria o time. O que sobe é o
    /// embrulho sob a AMK, e o servidor não tem AMK nenhuma para abri-lo.
    /// </summary>
    [Fact]
    public async Task CriarTime_AChaveNuncaVaiEmClaroParaOServidor()
    {
        var api = new FakeTeamApi();
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing _ = dono.Ring;

        CreatedTeam time = await dono.Service.CreateTeamAsync("Clientes do ISP");
        using WorkspaceKey? wk = await dono.Ring.TryGetWorkspaceKeyAsync(time.VaultWorkspaceId);

        string wkBase64 = Convert.ToBase64String(wk!.Key.ToArray());
        Assert.DoesNotContain(wkBase64, api.BodyStrings);
    }

    /// <summary>
    /// Servidor recusou: o time <b>não existe</b>, e o recado diz isso em pt-BR. A WK local fica
    /// órfã e INERTE (nenhum workspace a referencia) — apagá-la seria pior, porque apagar chave é
    /// irreversível e ela não faz mal ali. A próxima tentativa sorteia outro GUID.
    /// </summary>
    [Fact]
    public async Task CriarTime_ServidorRecusa_ESTOURA_EmVezDeFingirQueOTimeExiste()
    {
        var api = new FakeTeamApi();
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing _ = dono.Ring;

        api.WorkspaceCreationRefusal = HttpStatusCode.Conflict;

        var erro = await Assert.ThrowsAsync<TeamInviteException>(
            () => dono.Service.CreateTeamAsync("Clientes do ISP"));

        Assert.Contains("cofre pessoal não mudou", erro.Message, StringComparison.Ordinal);
        Assert.Null(api.CreatedWorkspace);

        // E o servidor segue sem chave nenhuma: nada meio-criado ficou para trás lá.
        Assert.Null(api.WorkspaceKey);
    }

    // ── O cursor de segredos, quando a chave ATERRISSA (1k) ──────────────────────────────

    /// <summary>
    /// <b>Depois de a chave do time chegar, o cursor volta a ZERO.</b> Sem isto: um envelope
    /// <c>WkRootedV1</c> que desceu ANTES da chave é PULADO (raiz divergente), o cursor avança por
    /// cima dele — ele avança por PÁGINA, não por item — e a senha fica atrás do cursor para sempre,
    /// sem nunca ter dado erro. O re-pull é idempotente por construção, então zerar custa um ciclo e
    /// devolve tudo.
    /// </summary>
    [Fact]
    public async Task AposRestaurarAWk_OCursorVoltaAZero()
    {
        var api = new FakeTeamApi();
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing _ = dono.Ring;

        // O time existe e a chave está no servidor (outro device do mesmo dono já a publicou).
        CreatedTeam time = await dono.Service.CreateTeamAsync("Clientes do ISP");

        // MESMA conta, outro computador: a AMK é portável, o embrulho em disco não é.
        Side outroPc = NewSide(api, dono.Amk);
        using WkWorkspaceKeyRing __ = outroPc.Ring;

        // Neste PC o canal de segredos já andou: o cursor está adiantado e os envelopes do time que
        // desceram nesse intervalo foram pulados (não havia chave para abri-los).
        await outroPc.Metadata.SaveSecretsCursorAsync(time.WorkspaceId, 4200);
        Assert.Equal(4200, await outroPc.Metadata.GetSecretsCursorAsync(time.WorkspaceId));

        Assert.Equal(
            TeamKeyRestore.Restored, await outroPc.Service.TryRestoreTeamKeyAsync(time.WorkspaceId));

        Assert.Equal(0, await outroPc.Metadata.GetSecretsCursorAsync(time.WorkspaceId));
    }

    /// <summary>
    /// Sem embrulho desta conta no servidor (404), nada aterrissa — e o cursor NÃO é mexido.
    /// Zerá-lo aqui obrigaria todo boot de quem só tem cofre pessoal a rebaixar o acervo inteiro.
    /// </summary>
    [Fact]
    public async Task SemChaveNoServidor_OCursorNaoEhMexido()
    {
        var api = new FakeTeamApi();
        Side conta = NewSide(api);
        using WkWorkspaceKeyRing _ = conta.Ring;

        await conta.Metadata.SaveSecretsCursorAsync(Workspace, 77);

        Assert.Equal(
            TeamKeyRestore.NoWrapForThisAccount,
            await conta.Service.TryRestoreTeamKeyAsync(Workspace));
        Assert.Equal(77, await conta.Metadata.GetSecretsCursorAsync(Workspace));
    }

    // ── Lado do DONO ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>A fronteira E2EE.</b> O servidor recebe hash e blob — nunca o código, em nenhuma forma
    /// (nem o texto com hífens, nem sem, nem os bytes em base64). É este teste que impede um refactor
    /// de "simplificar" o convite entregando a chave do time ao servidor.
    /// </summary>
    [Fact]
    public async Task Dono_GeraConvite_OCodigoNuncaVaiParaOServidor()
    {
        Side dono = NewSide();
        using WkWorkspaceKeyRing _ = dono.Ring;

        GeneratedTeamInvite invite = await dono.Service.CreateInviteAsync(
            Workspace, "colega@innet.tec.br", "Manager");

        Assert.NotEmpty(invite.Code);
        string semHifen = invite.Code.Replace("-", string.Empty);
        string base64DosBytes = Convert.ToBase64String(RecoveryKeyCodec.Parse(invite.Code));

        foreach (string body in dono.Api.BodyStrings)
        {
            Assert.DoesNotContain(invite.Code, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(semHifen, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(base64DosBytes, body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// O convite carrega a WK do TIME (a mesma que o cofre do dono usa), embrulhada sob o código.
    /// Se ele carregasse outra chave, o colega entraria no time e não abriria nada.
    /// </summary>
    [Fact]
    public async Task Dono_GeraConvite_QueCarregaAWkDoProprioCofre()
    {
        Side dono = NewSide();
        using WkWorkspaceKeyRing ring = dono.Ring;

        GeneratedTeamInvite invite = await dono.Service.CreateInviteAsync(
            Workspace, "colega@innet.tec.br", "Manager");

        string vaultWorkspace = AppRuntimeTeamMirror.TeamVaultWorkspace(Workspace);
        using WorkspaceKey? wkDoDono = await ring.TryGetWorkspaceKeyAsync(vaultWorkspace);
        Assert.NotNull(wkDoDono);

        byte[] doConvite = TeamInviteCrypto.UnwrapWorkspaceKey(
            Convert.FromBase64String(dono.Api.Created!.WrappedWkByInvite), invite.Code, Workspace);
        Assert.Equal(wkDoDono.Key.ToArray(), doConvite);
    }

    /// <summary>Gerar dois convites não troca a chave do time — senão o primeiro convidado perderia o cofre.</summary>
    [Fact]
    public async Task Dono_GeraDoisConvites_AChaveDoTimeNaoMuda()
    {
        Side dono = NewSide();
        using WkWorkspaceKeyRing ring = dono.Ring;
        string vaultWorkspace = AppRuntimeTeamMirror.TeamVaultWorkspace(Workspace);

        await dono.Service.CreateInviteAsync(Workspace, "a@innet.tec.br", "Manager");
        using WorkspaceKey? primeira = await ring.TryGetWorkspaceKeyAsync(vaultWorkspace);

        await dono.Service.CreateInviteAsync(Workspace, "b@innet.tec.br", "Manager");
        using WorkspaceKey? segunda = await ring.TryGetWorkspaceKeyAsync(vaultWorkspace);

        Assert.Equal(primeira!.Key.ToArray(), segunda!.Key.ToArray());
    }

    /// <summary>
    /// <b>O bloqueante da revisão adversarial.</b> Até aqui, o único caminho que gravava
    /// <c>Membership.WrappedWk</c> era o ACEITE do convite: quem CRIAVA o time nunca subia o próprio
    /// embrulho, e <c>GET /workspaces/{id}/key</c> devolvia 404 <b>para o dono</b>. Não era caso de
    /// borda — era o caminho normal dele.
    ///
    /// <para>Agora o embrulho do dono sobe no instante em que a WK do time NASCE neste computador,
    /// sem depender de ninguém aceitar nada. E ele é o embrulho sob a AMK DELE: o segundo computador
    /// da mesma conta abre com ele.</para>
    /// </summary>
    [Fact]
    public async Task Dono_AoCriarOTime_SobeOProprioEmbrulho_SemNinguemAceitar()
    {
        var api = new FakeTeamApi();
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing ring = dono.Ring;
        string vaultWorkspace = AppRuntimeTeamMirror.TeamVaultWorkspace(Workspace);

        await dono.Service.CreateInviteAsync(Workspace, "colega@innet.tec.br", "Manager");

        // O servidor tem a chave DO DONO guardada — e ninguém aceitou convite nenhum.
        Assert.NotNull(api.WorkspaceKey);
        Assert.DoesNotContain("accept", api.Calls);

        // ...e ela sobe ANTES do convite: se o embrulho não entrasse, o convite não podia existir,
        // senão o time nasceria com uma chave que o próprio dono não recupera no outro PC.
        Assert.Equal("publish", api.Calls[api.Calls.IndexOf("create") - 1]);

        // É o embrulho sob a AMK DELE: o segundo computador da mesma conta o abre e chega na MESMA WK.
        using var segundoDevice = TeamKeyRingFactory.New(dono.Amk);
        await segundoDevice.RestoreWrappedWorkspaceKeyAsync(
            vaultWorkspace, Convert.FromBase64String(api.WorkspaceKey.WrappedWk));

        using WorkspaceKey? aqui = await ring.TryGetWorkspaceKeyAsync(vaultWorkspace);
        using WorkspaceKey? la = await segundoDevice.TryGetWorkspaceKeyAsync(vaultWorkspace);
        Assert.Equal(aqui!.Key.ToArray(), la!.Key.ToArray());
    }

    /// <summary>
    /// <b>A bifurcação do segundo device.</b> Quem convida de um PC NOVO (ring vazio) com o time já
    /// existindo no servidor NÃO pode sortear outra WK — o convite levaria uma chave nova e o
    /// convidado entraria num "time" que não abre nada dos outros, sem erro nenhum. O caminho certo:
    /// restaurar a WK do servidor e embrulhar ELA no convite.
    /// </summary>
    [Fact]
    public async Task Convidar_NoSegundoDevice_UsaAChaveDoServidor_NaoSorteiaOutra()
    {
        var api = new FakeTeamApi();
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing ringPrimeiro = dono.Ring;
        string vaultWorkspace = AppRuntimeTeamMirror.TeamVaultWorkspace(Workspace);

        await dono.Service.CreateInviteAsync(Workspace, "a@innet.tec.br", "Manager");
        using WorkspaceKey wkOriginal = (await ringPrimeiro.TryGetWorkspaceKeyAsync(vaultWorkspace))!;

        // O servidor guarda o embrulho da conta (o mesmo blob da membership).
        byte[] wrapped = await ringPrimeiro.ImportWorkspaceKeyAsync(
            vaultWorkspace, wkOriginal.Key.ToArray());
        api.WorkspaceKey = new TeamWorkspaceKeyResponse(Workspace, Convert.ToBase64String(wrapped), 1);

        // Mesma conta (mesma AMK), device novo: ring vazio.
        using var ringNovo = TeamKeyRingFactory.New(dono.Amk);
        var servicoNovo = new TeamInviteService(api, ringNovo, SessionVaultKind.Team);
        GeneratedTeamInvite invite = await servicoNovo.CreateInviteAsync(
            Workspace, "b@innet.tec.br", "Manager");

        byte[] doConvite = TeamInviteCrypto.UnwrapWorkspaceKey(
            Convert.FromBase64String(api.Created!.WrappedWkByInvite), invite.Code, Workspace);
        Assert.Equal(wkOriginal.Key.ToArray(), doConvite);
        CryptographicOperations.ZeroMemory(doConvite);
    }

    /// <summary>
    /// Sem conseguir perguntar ao servidor se o time já tem chave, convidar de um ring vazio NÃO
    /// sorteia: "não sei" não pode virar "pode sortear". A falha sobe (o POST do convite exigiria a
    /// mesma rede logo em seguida) e o ring continua sem chave — nada nasceu no escuro.
    /// </summary>
    [Fact]
    public async Task Convidar_SemConseguirPerguntarAoServidor_NaoSorteiaChave()
    {
        var api = new FakeTeamApi { KeyEndpointOffline = true };
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing ring = dono.Ring;

        await Assert.ThrowsAsync<HttpRequestException>(
            () => dono.Service.CreateInviteAsync(Workspace, "a@innet.tec.br", "Manager"));

        Assert.Null(await ring.TryGetWorkspaceKeyAsync(
            AppRuntimeTeamMirror.TeamVaultWorkspace(Workspace)));
    }

    /// <summary>
    /// <b>O segundo computador do dono RESTAURA em vez de sortear.</b> O operador tem dois PCs de
    /// verdade. Sem o embrulho do dono no servidor, o PC novo respondia "não é time" (404) e o
    /// chaveiro sorteava uma WK₂ — o cofre do time bifurcava de forma determinística, e o indicador
    /// ainda dizia "cofre pessoal" para ele.
    /// </summary>
    [Fact]
    public async Task SegundoDeviceDoDono_RESTAURA_AMesmaChave_EmVezDeSortear()
    {
        var api = new FakeTeamApi();
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing primeiro = dono.Ring;
        string vaultWorkspace = AppRuntimeTeamMirror.TeamVaultWorkspace(Workspace);

        await dono.Service.CreateInviteAsync(Workspace, "colega@innet.tec.br", "Manager");
        using WorkspaceKey wkOriginal = (await primeiro.TryGetWorkspaceKeyAsync(vaultWorkspace))!;

        // O SEGUNDO PC do dono: mesma conta (mesma AMK), disco limpo.
        using var segundo = TeamKeyRingFactory.New(dono.Amk);
        var servico = new TeamInviteService(api, segundo, SessionVaultKind.Team);

        // O indicador não pode dizer "cofre pessoal" aqui — e a mesma pergunta traz a chave de volta.
        Assert.Equal(WorkspaceKindFact.Team, await servico.ProbeWorkspaceKindAsync(Workspace));

        using WorkspaceKey? restaurada = await segundo.TryGetWorkspaceKeyAsync(vaultWorkspace);
        Assert.NotNull(restaurada);
        Assert.Equal(wkOriginal.Key.ToArray(), restaurada.Key.ToArray());
    }

    // ── Publicação do embrulho (o bloqueante) ────────────────────────────────────────────

    /// <summary>
    /// O reparo de boot: chave no disco, servidor sem nada guardado para mim → sobe. É o caminho de
    /// quem já tinha o time criado antes desta correção existir, e o único jeito de fechar aquele
    /// buraco sem obrigar o operador a convidar alguém só para consertar o segundo PC.
    /// </summary>
    [Fact]
    public async Task Boot_ComChaveLocal_ESemNadaNoServidor_PublicaOEmbrulho()
    {
        var api = new FakeTeamApi();
        Side conta = NewSide(api);
        using WkWorkspaceKeyRing ring = conta.Ring;
        string vaultWorkspace = AppRuntimeTeamMirror.TeamVaultWorkspace(Workspace);

        // Chave do time no disco (como ficaria depois de um convite gerado por uma versão antiga),
        // e nenhum embrulho no servidor.
        byte[] wk = RandomNumberGenerator.GetBytes(32);
        await ring.ImportWorkspaceKeyAsync(vaultWorkspace, wk);
        Assert.Null(api.WorkspaceKey);

        Assert.Equal(TeamKeyUpload.Published, await conta.Service.PublishOwnWrappedKeyAsync(Workspace));

        Assert.NotNull(api.WorkspaceKey);
        using var outroPc = TeamKeyRingFactory.New(conta.Amk);
        await outroPc.RestoreWrappedWorkspaceKeyAsync(
            vaultWorkspace, Convert.FromBase64String(api.WorkspaceKey.WrappedWk));
        using WorkspaceKey? la = await outroPc.TryGetWorkspaceKeyAsync(vaultWorkspace);
        Assert.Equal(wk, la!.Key.ToArray());
    }

    /// <summary>
    /// Republicar é no-op, e é o caso NORMAL: o reparo roda a cada boot. Se cada boot gravasse de
    /// novo (ou virasse conflito), o alarme de bifurcação tocaria todo dia e ninguém mais o ouviria.
    /// </summary>
    [Fact]
    public async Task Republicar_OMesmoEmbrulho_NaoRegrava()
    {
        var api = new FakeTeamApi();
        Side conta = NewSide(api);
        using WkWorkspaceKeyRing ring = conta.Ring;

        await ring.ImportWorkspaceKeyAsync(
            AppRuntimeTeamMirror.TeamVaultWorkspace(Workspace), RandomNumberGenerator.GetBytes(32));

        Assert.Equal(TeamKeyUpload.Published, await conta.Service.PublishOwnWrappedKeyAsync(Workspace));
        Assert.Equal(TeamKeyUpload.AlreadyOnServer, await conta.Service.PublishOwnWrappedKeyAsync(Workspace));
        Assert.Equal(TeamKeyUpload.AlreadyOnServer, await conta.Service.PublishOwnWrappedKeyAsync(Workspace));
    }

    /// <summary>
    /// <b>Gravação dupla divergente NÃO troca em silêncio.</b> O servidor recusa (409, blobs opacos:
    /// ele só sabe que já tem um embrulho DIFERENTE) e o cliente — que TEM a AMK — baixa o embrulho
    /// guardado e compara as chaves de verdade. Chaves diferentes = bifurcação, e isso vira recado
    /// alto em pt-BR. O que não pode acontecer é a troca calada: o blob guardado é o que os OUTROS
    /// computadores desta conta vão restaurar.
    /// </summary>
    [Fact]
    public async Task Publicar_ComChaveDiferenteDaGuardada_RECUSA_ENaoTrocaOQueEstaNoServidor()
    {
        var api = new FakeTeamApi();
        byte[] amk = Amk();
        string vaultWorkspace = AppRuntimeTeamMirror.TeamVaultWorkspace(Workspace);

        // PC nº 1 publica a chave DE VERDADE do time.
        using var pc1 = TeamKeyRingFactory.New(amk);
        await pc1.ImportWorkspaceKeyAsync(vaultWorkspace, RandomNumberGenerator.GetBytes(32));
        await new TeamInviteService(api, pc1, SessionVaultKind.Team).PublishOwnWrappedKeyAsync(Workspace);
        string guardadoAntes = api.WorkspaceKey!.WrappedWk;

        // PC nº 2, mesma conta, com OUTRA chave no disco (a bifurcação já aconteceu neste device).
        using var pc2 = TeamKeyRingFactory.New(amk);
        await pc2.ImportWorkspaceKeyAsync(vaultWorkspace, RandomNumberGenerator.GetBytes(32));

        var erro = await Assert.ThrowsAsync<TeamInviteException>(
            () => new TeamInviteService(api, pc2, SessionVaultKind.Team).PublishOwnWrappedKeyAsync(Workspace));

        Assert.Contains("chave", erro.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(guardadoAntes, api.WorkspaceKey!.WrappedWk);
    }

    /// <summary>
    /// Sem chave de time neste computador não há nada a publicar — e, principalmente, <b>não se vai
    /// à rede</b>. É o caso de todo mundo que só tem cofre pessoal: o reparo de boot não pode custar
    /// um round-trip para quem nunca vai ter time.
    /// </summary>
    [Fact]
    public async Task Publicar_SemChaveLocal_NaoFazNada_ENemBateNoServidor()
    {
        var api = new FakeTeamApi();
        Side conta = NewSide(api);
        using WkWorkspaceKeyRing ring = conta.Ring;

        Assert.Equal(TeamKeyUpload.NoLocalKey, await conta.Service.PublishOwnWrappedKeyAsync(Workspace));
        Assert.Empty(api.Calls);
    }

    // ── Lado do CONVIDADO ────────────────────────────────────────────────────────────────

    /// <summary>Código certo: a WK do time chega inteira na máquina do convidado.</summary>
    [Fact]
    public async Task Convidado_ComCodigoCerto_GanhaAMesmaWkDoDono()
    {
        var api = new FakeTeamApi();
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing ringDono = dono.Ring;
        GeneratedTeamInvite invite = await dono.Service.CreateInviteAsync(
            Workspace, "colega@innet.tec.br", "Manager");

        Side convidado = NewSide(api);
        using WkWorkspaceKeyRing ringConvidado = convidado.Ring;

        AcceptedTeamInvite aceito = await convidado.Service.AcceptInviteAsync(invite.InviteId, invite.Code);

        string vaultWorkspace = AppRuntimeTeamMirror.TeamVaultWorkspace(Workspace);
        Assert.Equal(vaultWorkspace, aceito.VaultWorkspaceId);
        Assert.Equal(Workspace, aceito.WorkspaceId);
        Assert.Equal("Innet Telecom", aceito.WorkspaceName);

        using WorkspaceKey? doDono = await ringDono.TryGetWorkspaceKeyAsync(vaultWorkspace);
        using WorkspaceKey? doConvidado = await ringConvidado.TryGetWorkspaceKeyAsync(vaultWorkspace);
        Assert.NotNull(doConvidado);
        Assert.Equal(doDono!.Key.ToArray(), doConvidado.Key.ToArray());
    }

    /// <summary>
    /// <b>A ORDEM.</b> A WK entra no ring ANTES de o aceite ser gravado no servidor — e não pode ser
    /// o contrário: entre "sou membro" e "tenho a chave" existe uma janela em que qualquer operação
    /// de cofre faria o ring sortear uma WK aleatória, e o cofre do time bifurcaria em silêncio.
    /// </summary>
    [Fact]
    public async Task Convidado_ImportaAChave_ANTES_DeAceitarNoServidor()
    {
        var api = new FakeTeamApi();
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing ringDono = dono.Ring;
        GeneratedTeamInvite invite = await dono.Service.CreateInviteAsync(
            Workspace, "colega@innet.tec.br", "Manager");

        Side convidado = NewSide(api);
        using WkWorkspaceKeyRing ring = convidado.Ring;
        string vaultWorkspace = AppRuntimeTeamMirror.TeamVaultWorkspace(Workspace);

        api.Calls.Clear();
        api.ProbeOnAccept = () =>
            ring.TryGetWorkspaceKeyAsync(vaultWorkspace).GetAwaiter().GetResult() is not null;

        await convidado.Service.AcceptInviteAsync(invite.InviteId, invite.Code);

        Assert.True(
            api.KeyPresentAtAccept,
            "o aceite subiu ANTES de a WK estar no ring — a ordem errada abre a janela da bifurcação");
        Assert.Equal(new[] { "context", "accept" }, api.Calls);
    }

    /// <summary>
    /// Código errado: o servidor recusa (os hashes não batem) e o cliente AVISA em pt-BR. E, o que
    /// mais importa: nada é importado — o ring do convidado continua vazio.
    /// </summary>
    [Fact]
    public async Task Convidado_ComCodigoErrado_Avisa_ENaoImportaNada()
    {
        var api = new FakeTeamApi();
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing ringDono = dono.Ring;
        await dono.Service.CreateInviteAsync(Workspace, "colega@innet.tec.br", "Manager");

        Side convidado = NewSide(api);
        using WkWorkspaceKeyRing ring = convidado.Ring;

        var erro = await Assert.ThrowsAsync<TeamInviteException>(
            () => convidado.Service.AcceptInviteAsync(InviteId, TeamInviteCrypto.GenerateCode()));

        Assert.Contains("código", erro.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await ring.TryGetWorkspaceKeyAsync(AppRuntimeTeamMirror.TeamVaultWorkspace(Workspace)));
    }

    /// <summary>Convite expirado: mesma recusa do servidor, mesma mensagem acionável na tela.</summary>
    [Fact]
    public async Task Convidado_ComConviteExpirado_Avisa()
    {
        var api = new FakeTeamApi { Expired = true };
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing ringDono = dono.Ring;
        GeneratedTeamInvite invite = await dono.Service.CreateInviteAsync(
            Workspace, "colega@innet.tec.br", "Manager");

        Side convidado = NewSide(api);
        using WkWorkspaceKeyRing ring = convidado.Ring;

        var erro = await Assert.ThrowsAsync<TeamInviteException>(
            () => convidado.Service.AcceptInviteAsync(invite.InviteId, invite.Code));

        Assert.Contains("convite", erro.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expirado", erro.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Código digitado com caractere que não existe no alfabeto: falha AQUI, com mensagem de gente,
    /// sem bater no servidor. Um <c>FormatException</c> cru subindo até a UI viraria "Não foi
    /// possível concluir a operação" — e o colega nunca saberia que só faltou reler o código.
    /// </summary>
    [Fact]
    public async Task Convidado_ComCodigoMalformado_Avisa_ENemBateNoServidor()
    {
        var api = new FakeTeamApi();
        Side convidado = NewSide(api);
        using WkWorkspaceKeyRing ring = convidado.Ring;

        var erro = await Assert.ThrowsAsync<TeamInviteException>(
            () => convidado.Service.AcceptInviteAsync(InviteId, "0000-1111-!!!!"));

        Assert.Contains("código", erro.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(api.Calls);
    }

    /// <summary>
    /// O <c>WrappedWk</c> que sobe está sob a AMK do CONVIDADO — é o que faz o segundo device dele
    /// abrir o cofre. Se subisse o embrulho do convite, quem tivesse o código (que anda pelo
    /// WhatsApp) abriria o cofre do time para sempre.
    /// </summary>
    [Fact]
    public async Task Convidado_SobeOEmbrulhoSobAPropriaAmk_NaoODoConvite()
    {
        var api = new FakeTeamApi();
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing ringDono = dono.Ring;
        GeneratedTeamInvite invite = await dono.Service.CreateInviteAsync(
            Workspace, "colega@innet.tec.br", "Manager");

        Side convidado = NewSide(api);
        using WkWorkspaceKeyRing ring = convidado.Ring;
        await convidado.Service.AcceptInviteAsync(invite.InviteId, invite.Code);

        byte[] subiu = Convert.FromBase64String(api.Accepted!.WrappedWk);

        // Não é o blob do convite (aquele abre com o código; este NÃO pode).
        Assert.ThrowsAny<CryptographicException>(
            () => TeamInviteCrypto.UnwrapWorkspaceKey(subiu, invite.Code, Workspace));

        // E abre no SEGUNDO device do convidado: mesma AMK, disco limpo.
        using var segundoDevice = TeamKeyRingFactory.New(convidado.Amk);
        string vaultWorkspace = AppRuntimeTeamMirror.TeamVaultWorkspace(Workspace);
        await segundoDevice.RestoreWrappedWorkspaceKeyAsync(vaultWorkspace, subiu);

        using WorkspaceKey? doDono = await ringDono.TryGetWorkspaceKeyAsync(vaultWorkspace);
        using WorkspaceKey? noSegundo = await segundoDevice.TryGetWorkspaceKeyAsync(vaultWorkspace);
        Assert.Equal(doDono!.Key.ToArray(), noSegundo!.Key.ToArray());
    }

    /// <summary>O aviso de sessão obsoleta do servidor chega inteiro à UI — silenciá-lo dá 403 "sem motivo".</summary>
    [Fact]
    public async Task Convidado_RecebeOAvisoDeQueASessaoPrecisaSerRenovada()
    {
        var api = new FakeTeamApi { SessionRefreshRequired = true };
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing ringDono = dono.Ring;
        GeneratedTeamInvite invite = await dono.Service.CreateInviteAsync(
            Workspace, "colega@innet.tec.br", "Manager");

        Side convidado = NewSide(api);
        using WkWorkspaceKeyRing ring = convidado.Ring;

        AcceptedTeamInvite aceito = await convidado.Service.AcceptInviteAsync(invite.InviteId, invite.Code);

        Assert.True(aceito.SessionRefreshRequired);
    }

    // ── "Em qual cofre eu estou?" (1e) ───────────────────────────────────────────────────
    //
    // É a pergunta que alimenta o indicador do shell. Errar a resposta é caro nos DOIS sentidos:
    // dizer "time" num cofre pessoal faz o operador cadastrar o cliente achando que compartilhou;
    // dizer "pessoal" num workspace de time esconde dele que o colega não vai abrir as senhas.

    /// <summary>
    /// Com a chave do time JÁ no disco a resposta sai sem rede nenhuma. É o caminho de todo boot
    /// depois do primeiro — e o que faz o indicador continuar honesto no cliente sem sinal.
    /// </summary>
    [Fact]
    public async Task ETime_RespondeDoDisco_SemPerguntarAoServidor()
    {
        var api = new FakeTeamApi();
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing ring = dono.Ring;

        // Convidar é o ato que faz a WK do time nascer neste PC.
        await dono.Service.CreateInviteAsync(Workspace, "colega@innet.tec.br", "Manager");
        api.Calls.Clear();

        Assert.Equal(WorkspaceKindFact.Team, await dono.Service.ProbeWorkspaceKindAsync(Workspace));
        Assert.DoesNotContain("key", api.Calls);
    }

    /// <summary>
    /// Sem chave local, só o servidor sabe — e a MESMA pergunta restaura a chave neste device. É o
    /// segundo PC do membro: a AMK é portável, o embrulho gravado em disco não é.
    /// </summary>
    [Fact]
    public async Task ETime_SemChaveLocal_PerguntaAoServidor_ERestauraAChave()
    {
        var api = new FakeTeamApi();
        Side dono = NewSide(api);
        using WkWorkspaceKeyRing ringDono = dono.Ring;
        await dono.Service.CreateInviteAsync(Workspace, "colega@innet.tec.br", "Manager");

        // Mesma conta (mesma AMK), device NOVO: ring vazio.
        byte[] amk = dono.Amk;
        using var ringNovo = TeamKeyRingFactory.New(amk);
        var servico = new TeamInviteService(api, ringNovo, SessionVaultKind.Team);

        // O servidor guarda o embrulho da membership — que é o que o ring do dono produziu.
        byte[] wrapped = await dono.Ring.ImportWorkspaceKeyAsync(
            RemoteOps.Desktop.Account.AppRuntime.TeamVaultWorkspace(Workspace),
            (await dono.Ring.TryGetWorkspaceKeyAsync(
                RemoteOps.Desktop.Account.AppRuntime.TeamVaultWorkspace(Workspace)))!.Key.ToArray());
        api.WorkspaceKey = new TeamWorkspaceKeyResponse(Workspace, Convert.ToBase64String(wrapped), 1);

        Assert.Equal(WorkspaceKindFact.Team, await servico.ProbeWorkspaceKindAsync(Workspace));
        Assert.Contains("key", api.Calls);

        // Restaurou de verdade: a próxima pergunta já sai do disco.
        WorkspaceKey? agoraLocal = await ringNovo.TryGetWorkspaceKeyAsync(
            RemoteOps.Desktop.Account.AppRuntime.TeamVaultWorkspace(Workspace));
        Assert.NotNull(agoraLocal);
        agoraLocal.Dispose();
    }

    /// <summary>
    /// ⚠️ <b>404 NÃO é "cofre pessoal" — é "NÃO SEI".</b> O servidor devolve 404 quando <b>a SUA
    /// CONTA</b> não guarda embrulho naquele workspace (<c>TeamService.GetWorkspaceKeyAsync</c>), o
    /// que acontece tanto num cofre pessoal quanto num TIME cujo embrulho do dono nunca subiu. E um
    /// 404 de INFRAESTRUTURA (proxy sem a rota, URL errada, cliente novo × backend velho) chega
    /// exatamente igual.
    ///
    /// <para>Enquanto isto respondia <c>false</c>, a ausência virava afirmação: o resolvedor de
    /// escopo gravava <c>sync-local.owner</c> com o GUID do TIME e o banco com os ~700 equipamentos
    /// do operador passava a subir para os colegas. Quem afirma que um workspace é pessoal é o
    /// <c>kind</c> que vem na lista do login — nunca a falta de uma chave.</para>
    /// </summary>
    [Fact]
    public async Task Servidor404_NaoAfirma_CofrePessoal_Responde_NAO_SEI()
    {
        var api = new FakeTeamApi { WorkspaceKey = null };
        Side conta = NewSide(api);
        using WkWorkspaceKeyRing ring = conta.Ring;

        Assert.Equal(WorkspaceKindFact.Unknown, await conta.Service.ProbeWorkspaceKindAsync(Workspace));
    }

    /// <summary>
    /// O MESMO 404, visto pelo contrato de restauração: "o servidor não guarda embrulho SEU aqui".
    /// A distinção é o contrato, e não só a chamada — enquanto o retorno fosse <c>bool</c> com o doc
    /// dizendo "false = cofre PESSOAL", o próximo chamador recriaria o defeito.
    /// </summary>
    [Fact]
    public async Task Restauracao404_Diz_SemEmbrulhoParaEstaConta_ENao_EhPessoal()
    {
        var api = new FakeTeamApi { WorkspaceKey = null };
        Side conta = NewSide(api);
        using WkWorkspaceKeyRing ring = conta.Ring;

        Assert.Equal(
            TeamKeyRestore.NoWrapForThisAccount,
            await conta.Service.TryRestoreTeamKeyAsync(Workspace));
    }

    /// <summary>
    /// <b>Sem rede, a resposta NÃO é "pessoal".</b> Engolir a falha aqui devolveria <c>false</c> e o
    /// indicador diria "cofre pessoal" com toda a confiança — o operador cadastraria o cliente sem
    /// nunca ver o aviso. A exceção sobe, e quem chama transforma isso em "não confirmado".
    /// </summary>
    [Fact]
    public async Task SemRede_NaoFingeQueECofrePessoal()
    {
        var api = new FakeTeamApi { KeyEndpointOffline = true };
        Side conta = NewSide(api);
        using WkWorkspaceKeyRing ring = conta.Ring;

        await Assert.ThrowsAsync<HttpRequestException>(
            () => conta.Service.ProbeWorkspaceKindAsync(Workspace));
    }

    /// <summary>
    /// Espelho do <c>AppRuntime</c> (internal do Desktop) para o teste dizer explicitamente QUAL é a
    /// identidade do cofre do time. Amarrado ao real em <see cref="Mirror_BateComOAppRuntime"/>.
    /// </summary>
    private static class AppRuntimeTeamMirror
    {
        internal static string TeamVaultWorkspace(string serverWorkspaceId) => "time:" + serverWorkspaceId;
    }

    /// <summary>
    /// O cofre do time NÃO pode compartilhar identidade com o cofre pessoal ("ws-local") nem com o
    /// workspace da chave do banco ("local"): as três são raízes diferentes sobre o MESMO arquivo de
    /// cofre, e colidir aqui é perda de chave, não erro de leitura.
    /// </summary>
    [Fact]
    public void Mirror_BateComOAppRuntime()
    {
        Assert.Equal(
            AppRuntimeTeamMirror.TeamVaultWorkspace(Workspace),
            RemoteOps.Desktop.Account.AppRuntime.TeamVaultWorkspace(Workspace));

        Assert.DoesNotContain(
            RemoteOps.Desktop.Account.AppRuntime.TeamVaultWorkspace(Workspace),
            RemoteOps.Desktop.Account.AppRuntime.VaultWorkspaces);
    }

    /// <summary>
    /// ⚠️ <b>Boot.</b> O workspace do time tem que entrar na lista que a ativação percorre — fora
    /// dela o app trava na abertura (já mordeu antes). E os dois de sempre continuam lá: tirar o
    /// "local" (chave do banco SQLCipher) é o mesmo travamento por outro caminho.
    /// </summary>
    [Fact]
    public void BootList_IncluiOTime_SemPerderOsDeSempre()
    {
        IReadOnlyList<string> comTime =
            RemoteOps.Desktop.Account.AppRuntime.VaultWorkspacesFor(Workspace);

        Assert.Contains(RemoteOps.Desktop.Account.AppRuntime.CredentialsWorkspace, comTime);
        Assert.Contains(RemoteOps.Desktop.Account.AppRuntime.DbWorkspace, comTime);
        Assert.Contains(RemoteOps.Desktop.Account.AppRuntime.TeamVaultWorkspace(Workspace), comTime);

        // Sem time (cofre pessoal), a lista é exatamente a de hoje — nada muda para quem não tem time.
        Assert.Equal(
            RemoteOps.Desktop.Account.AppRuntime.VaultWorkspaces,
            RemoteOps.Desktop.Account.AppRuntime.VaultWorkspacesFor(null));
    }
}
