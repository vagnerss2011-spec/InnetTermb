using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Security.Account;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Sync.Remote;

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

        public Task<TeamWorkspaceKeyResponse?> GetWorkspaceKeyAsync(
            string workspaceId, CancellationToken ct = default)
        {
            Calls.Add("key");
            return Task.FromResult<TeamWorkspaceKeyResponse?>(null);
        }
    }

    private sealed record Side(FakeTeamApi Api, WkWorkspaceKeyRing Ring, TeamInviteService Service, byte[] Amk);

    private static Side NewSide(FakeTeamApi? api = null, bool allowKeyCreation = true)
    {
        api ??= new FakeTeamApi();
        byte[] amk = Amk();
        var ring = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), amk, allowKeyCreation);
        return new Side(api, ring, new TeamInviteService(api, ring), amk);
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
            Convert.FromBase64String(dono.Api.Created!.WrappedWkByInvite), invite.Code);
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

        Side convidado = NewSide(api, allowKeyCreation: false);
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

        Side convidado = NewSide(api, allowKeyCreation: false);
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

        Side convidado = NewSide(api, allowKeyCreation: false);
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

        Side convidado = NewSide(api, allowKeyCreation: false);
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
        Side convidado = NewSide(api, allowKeyCreation: false);
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

        Side convidado = NewSide(api, allowKeyCreation: false);
        using WkWorkspaceKeyRing ring = convidado.Ring;
        await convidado.Service.AcceptInviteAsync(invite.InviteId, invite.Code);

        byte[] subiu = Convert.FromBase64String(api.Accepted!.WrappedWk);

        // Não é o blob do convite (aquele abre com o código; este NÃO pode).
        Assert.ThrowsAny<CryptographicException>(() => TeamInviteCrypto.UnwrapWorkspaceKey(subiu, invite.Code));

        // E abre no SEGUNDO device do convidado: mesma AMK, disco limpo.
        using var segundoDevice = new WkWorkspaceKeyRing(
            new InMemoryWorkspaceKeyStore(), convidado.Amk, allowKeyCreation: false);
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

        Side convidado = NewSide(api, allowKeyCreation: false);
        using WkWorkspaceKeyRing ring = convidado.Ring;

        AcceptedTeamInvite aceito = await convidado.Service.AcceptInviteAsync(invite.InviteId, invite.Code);

        Assert.True(aceito.SessionRefreshRequired);
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
