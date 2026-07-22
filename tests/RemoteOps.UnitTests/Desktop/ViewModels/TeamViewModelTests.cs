using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.ViewModels;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// A tela de Equipe (Fatia 1e): quem está no time, convidar e remover.
///
/// <para><b>O que estes testes guardam:</b> (1) que a remoção diz a VERDADE — corta o futuro, não
/// apaga o passado, e as senhas precisam ser trocadas nos equipamentos; (2) que NENHUM caminho de
/// erro some — lista que não carrega, remoção que falha e permissão negada viram texto na tela; e
/// (3) que uma lista vazia nunca é desenhada como "o time só tem você" quando na verdade a consulta
/// falhou. As três coisas são a mesma disciplina: nesta base, o defeito estrutural é a falha
/// silenciosa.</para>
/// </summary>
public sealed class TeamViewModelTests
{
    private const string Workspace = "8f3b6f4a-0000-4000-8000-000000000001";

    private static TeamMemberDto Member(
        string id, string email, string name, string role = TeamRoles.Operator, bool hasWk = true)
        => new(id, email, name, role, hasWk, 1);

    /// <summary>Servidor de time em memória, com os três desfechos reais da remoção.</summary>
    private sealed class FakeTeamApi : ITeamApi
    {
        public List<TeamMemberDto> Members { get; } = [];

        public List<string> Removed { get; } = [];

        /// <summary>Erro a lançar na listagem (rede fora, 403…). <c>null</c> = responde normal.</summary>
        public Func<Exception>? ListFailure { get; set; }

        /// <summary>Erro a lançar na remoção.</summary>
        public Func<Exception>? RemoveFailure { get; set; }

        /// <summary>Desfecho da remoção quando ela não lança.</summary>
        public TeamMemberRemoval RemoveOutcome { get; set; } = TeamMemberRemoval.Removed;

        /// <summary>Segura a listagem até o teste liberar — é como se observa o estado "carregando".</summary>
        public TaskCompletionSource? ListGate { get; set; }

        public int ListCalls { get; private set; }

        public async Task<TeamMembersResponse> GetMembersAsync(
            string workspaceId, CancellationToken ct = default)
        {
            ListCalls++;
            if (ListGate is not null)
            {
                await ListGate.Task;
            }

            if (ListFailure is not null)
            {
                throw ListFailure();
            }

            return new TeamMembersResponse([.. Members]);
        }

        public Task<TeamMemberRemoval> RemoveMemberAsync(
            string workspaceId, string userId, CancellationToken ct = default)
        {
            if (RemoveFailure is not null)
            {
                throw RemoveFailure();
            }

            if (RemoveOutcome == TeamMemberRemoval.Removed)
            {
                Removed.Add(userId);
                Members.RemoveAll(m => m.UserId == userId);
            }

            return Task.FromResult(RemoveOutcome);
        }

        public Task<CreateTeamInviteResponse> CreateInviteAsync(
            string workspaceId, CreateTeamInviteRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TeamInviteContextResponse> GetInviteContextAsync(
            string inviteId, string codeHash, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AcceptTeamInviteResponse> AcceptInviteAsync(
            string inviteId, AcceptTeamInviteRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TeamWorkspaceKeyResponse?> GetWorkspaceKeyAsync(
            string workspaceId, CancellationToken ct = default)
            => Task.FromResult<TeamWorkspaceKeyResponse?>(null);

        // A tela de membros não publica chave nenhuma — quem publica é o fluxo de convite e o
        // reparo de boot. Se um dia ela passar a chamar, é melhor estourar aqui do que fingir.
        public Task<TeamKeyPublication> PublishWorkspaceKeyAsync(
            string workspaceId, PublishTeamWorkspaceKeyRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static (TeamViewModel Vm, FakeTeamApi Api) New(params TeamMemberDto[] members)
    {
        var api = new FakeTeamApi();
        api.Members.AddRange(members);
        return (new TeamViewModel(api, Workspace), api);
    }

    // ── Listar ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Carrega_Membros_ComNomeEmailEPapel()
    {
        var (vm, _) = New(
            Member("u1", "dono@innet.tec.br", "Vagner", TeamRoles.Owner),
            Member("u2", "colega@innet.tec.br", "Marcos", TeamRoles.Operator));

        await vm.LoadAsync();

        Assert.False(vm.HasLoadError, vm.LoadError);
        Assert.True(vm.ShowMembers);
        Assert.Equal(2, vm.Members.Count);
        Assert.Equal("Vagner", vm.Members[0].DisplayName);
        Assert.Equal("dono@innet.tec.br", vm.Members[0].Email);

        // Papel em pt-BR, não o id cru do RBAC: "Owner" não quer dizer nada para quem está na rua.
        Assert.Equal("Dono", vm.Members[0].RoleLabel);
        Assert.Equal("Técnico", vm.Members[1].RoleLabel);
    }

    /// <summary>Conta sem nome cadastrado cai no e-mail — nunca numa linha em branco na lista.</summary>
    [Fact]
    public async Task MembroSemNome_MostraOEmail_EmVezDeLinhaEmBranco()
    {
        var (vm, _) = New(Member("u1", "colega@innet.tec.br", "   "));

        await vm.LoadAsync();

        Assert.Equal("colega@innet.tec.br", vm.Members[0].DisplayName);
    }

    /// <summary>
    /// Membro sem a chave do time enxerga a lista e não abre senha nenhuma. Isso aparece ESCRITO na
    /// linha dele — senão vira "a senha não abre" no meio de um atendimento, sem ninguém ligar as
    /// duas coisas.
    /// </summary>
    [Fact]
    public async Task MembroSemAChave_ApareceMarcado_NaLinhaDele()
    {
        var (vm, _) = New(
            Member("u1", "dono@innet.tec.br", "Vagner", TeamRoles.Owner),
            Member("u2", "novo@innet.tec.br", "Ana", TeamRoles.Operator, hasWk: false));

        await vm.LoadAsync();

        Assert.False(vm.Members[0].HasKeyWarning);
        Assert.True(vm.Members[1].HasKeyWarning);
        Assert.Contains("chave", vm.Members[1].KeyWarning, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>Lista que não carrega não vira lista vazia.</b> Sem esta guarda, a tela desenharia
    /// "nenhum membro" e o operador concluiria que perdeu o time.
    /// </summary>
    [Fact]
    public async Task ListaQueFalha_MostraOERRO_ENaoUmaListaVaziaMentindo()
    {
        var (vm, api) = New(Member("u1", "dono@innet.tec.br", "Vagner", TeamRoles.Owner));
        api.ListFailure = () => new HttpRequestException("rede fora (teste)");

        await vm.LoadAsync();

        Assert.True(vm.HasLoadError);
        Assert.NotEmpty(vm.LoadError);
        Assert.False(vm.ShowMembers);
        Assert.False(vm.IsEmpty);
        Assert.Empty(vm.Members);
    }

    /// <summary>403 tem recado próprio: o operador precisa saber a quem pedir, não "tente de novo".</summary>
    [Fact]
    public async Task ListaNegadaPorPermissao_DizOQueFazer()
    {
        var (vm, api) = New();
        api.ListFailure = () => new CloudSyncException(HttpStatusCode.Forbidden);

        await vm.LoadAsync();

        Assert.True(vm.HasLoadError);
        Assert.Contains("permissão", vm.LoadError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enquanto carrega, a tela diz "carregando" e NÃO diz "vazio". Spinner eterno e lista vazia
    /// mentindo são o mesmo defeito visto de dois ângulos.
    /// </summary>
    [Fact]
    public async Task EnquantoCarrega_NaoDizQueEstaVazio()
    {
        var (vm, api) = New(Member("u1", "dono@innet.tec.br", "Vagner", TeamRoles.Owner));
        api.ListGate = new TaskCompletionSource();

        Task carregando = vm.LoadAsync();

        Assert.True(vm.IsLoading);
        Assert.False(vm.IsEmpty);
        Assert.False(vm.ShowMembers);

        api.ListGate.SetResult();
        await carregando;

        Assert.False(vm.IsLoading);
        Assert.True(vm.ShowMembers);
    }

    /// <summary>
    /// Time sem NENHUM membro é impossível (quem pergunta já é membro). Se acontecer, a tela diz
    /// que é anormal em vez de desenhar um vazio tranquilo.
    /// </summary>
    [Fact]
    public async Task RespostaSemNinguem_EhTratadaComoAnormal_NaoComoNormal()
    {
        var (vm, _) = New();

        await vm.LoadAsync();

        Assert.True(vm.IsEmpty);
        Assert.False(vm.ShowMembers);
        Assert.NotEmpty(vm.EmptyMessage);
        Assert.Contains("não deveria", vm.EmptyMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── Remover ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>O texto que o operador tem de ler antes de confirmar.</b> Prometer menos do que a
    /// criptografia entrega seria enganá-lo num assunto de segurança: remover corta o acesso
    /// FUTURO, e a senha que a pessoa já viu continua com ela.
    /// </summary>
    [Fact]
    public void ATelaDeRemocao_DizAVerdadeInteira()
    {
        Assert.Contains(
            "Isso corta o acesso daqui pra frente", TeamViewModel.RemovalTruth, StringComparison.Ordinal);
        Assert.Contains(
            "Não apaga o que a pessoa já viu", TeamViewModel.RemovalTruth, StringComparison.Ordinal);
        Assert.Contains(
            "devem ser trocadas nos equipamentos", TeamViewModel.RemovalTruth, StringComparison.Ordinal);
    }

    /// <summary>Clicar em "Remover" ABRE a confirmação — nunca remove direto.</summary>
    [Fact]
    public async Task Remover_AbreAConfirmacao_ENaoRemoveNada()
    {
        var (vm, api) = New(
            Member("u1", "dono@innet.tec.br", "Vagner", TeamRoles.Owner),
            Member("u2", "colega@innet.tec.br", "Marcos"));
        await vm.LoadAsync();

        vm.RemoveCommand.Execute(vm.Members[1]);

        Assert.True(vm.IsRemovalConfirmVisible);
        Assert.Equal("u2", vm.RemovalTarget?.UserId);
        Assert.Empty(api.Removed);

        // E a verdade está no estado da confirmação, não escondida num tooltip.
        Assert.Equal(TeamViewModel.RemovalTruth, vm.RemovalWarning);
    }

    [Fact]
    public async Task Confirmar_RemoveESaiDaLista_ComRecadoNaTela()
    {
        var (vm, api) = New(
            Member("u1", "dono@innet.tec.br", "Vagner", TeamRoles.Owner),
            Member("u2", "colega@innet.tec.br", "Marcos"));
        await vm.LoadAsync();
        vm.RemoveCommand.Execute(vm.Members[1]);

        await vm.ConfirmRemoveAsync();

        Assert.Equal(["u2"], api.Removed);
        Assert.False(vm.IsRemovalConfirmVisible);
        Assert.DoesNotContain(vm.Members, m => m.UserId == "u2");
        Assert.True(vm.HasStatus);

        // O recado do sucesso REPETE a parte operacional: é o único momento em que o operador ainda
        // pode agir sobre os equipamentos.
        Assert.Contains("senha", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelar_FechaAConfirmacao_SemRemover()
    {
        var (vm, api) = New(Member("u2", "colega@innet.tec.br", "Marcos"));
        await vm.LoadAsync();
        vm.RemoveCommand.Execute(vm.Members[0]);

        vm.CancelRemoveCommand.Execute(null);

        Assert.False(vm.IsRemovalConfirmVisible);
        Assert.Null(vm.RemovalTarget);
        Assert.Empty(api.Removed);
    }

    /// <summary>Remoção que falha por rede: erro na tela, e a pessoa CONTINUA na lista.</summary>
    [Fact]
    public async Task RemocaoQueFalha_MostraOERRO_EMantemAPessoaNaLista()
    {
        var (vm, api) = New(Member("u2", "colega@innet.tec.br", "Marcos"));
        await vm.LoadAsync();
        vm.RemoveCommand.Execute(vm.Members[0]);
        api.RemoveFailure = () => new HttpRequestException("rede fora (teste)");

        await vm.ConfirmRemoveAsync();

        Assert.True(vm.HasActionError);
        Assert.NotEmpty(vm.ActionError);
        Assert.Contains(vm.Members, m => m.UserId == "u2");
    }

    /// <summary>
    /// Último dono: o servidor devolve 409 e a tela precisa dizer O QUE FAZER (promover outra
    /// pessoa) — "não foi possível remover" deixaria o operador tentando de novo para sempre.
    /// </summary>
    [Fact]
    public async Task UltimoDono_DizComoResolver()
    {
        var (vm, api) = New(Member("u1", "dono@innet.tec.br", "Vagner", TeamRoles.Owner));
        await vm.LoadAsync();
        vm.RemoveCommand.Execute(vm.Members[0]);
        api.RemoveOutcome = TeamMemberRemoval.LastOwner;

        await vm.ConfirmRemoveAsync();

        Assert.True(vm.HasActionError);
        Assert.Contains("dono", vm.ActionError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(vm.Members, m => m.UserId == "u1");
    }

    /// <summary>
    /// "Não era membro" NÃO pode virar "removido com sucesso": alguém removeu antes (outro
    /// administrador, outra janela), e a tela tem de recarregar em vez de fingir que fez.
    /// </summary>
    [Fact]
    public async Task NaoEraMembro_NaoDizQueRemoveu()
    {
        var (vm, api) = New(Member("u2", "colega@innet.tec.br", "Marcos"));
        await vm.LoadAsync();
        vm.RemoveCommand.Execute(vm.Members[0]);
        api.RemoveOutcome = TeamMemberRemoval.NotAMember;
        int chamadasAntes = api.ListCalls;

        await vm.ConfirmRemoveAsync();

        Assert.True(vm.HasActionError);
        Assert.DoesNotContain("Removid", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(api.ListCalls > chamadasAntes, "a lista precisa ser relida — ela está desatualizada");
    }

    /// <summary>Permissão negada na remoção: recado próprio, não o genérico de rede.</summary>
    [Fact]
    public async Task RemocaoNegadaPorPermissao_DizAQuemPedir()
    {
        var (vm, api) = New(Member("u2", "colega@innet.tec.br", "Marcos"));
        await vm.LoadAsync();
        vm.RemoveCommand.Execute(vm.Members[0]);
        api.RemoveFailure = () => new CloudSyncException(HttpStatusCode.Forbidden);

        await vm.ConfirmRemoveAsync();

        Assert.True(vm.HasActionError);
        Assert.Contains("permissão", vm.ActionError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Confirmar sem alvo é no-op — um duplo clique não pode remover a pessoa errada.</summary>
    [Fact]
    public async Task ConfirmarSemAlvo_NaoFazNada()
    {
        var (vm, api) = New(Member("u2", "colega@innet.tec.br", "Marcos"));
        await vm.LoadAsync();

        await vm.ConfirmRemoveAsync();

        Assert.Empty(api.Removed);
        Assert.False(vm.HasActionError);
    }

    // ── Convidar ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Convidar_PedeAJanelaDeConvite()
    {
        var (vm, _) = New();
        int pedidos = 0;
        vm.InviteRequested += (_, _) => pedidos++;

        vm.InviteCommand.Execute(null);

        Assert.Equal(1, pedidos);
    }

    // ── Indicador de cofre dentro da própria tela ────────────────────────────────────────

    /// <summary>
    /// A tela de Equipe também carrega o indicador: é onde o operador está justamente pensando em
    /// "o que é do time e o que é meu".
    /// </summary>
    [Fact]
    public void ATela_CarregaOIndicadorDeCofre()
    {
        var badge = new VaultBadgeViewModel();
        badge.Apply(VaultScope.TeamPending);
        var vm = new TeamViewModel(new FakeTeamApi(), Workspace, badge);

        Assert.Same(badge, vm.Vault);
        Assert.True(vm.Vault.IsWarning);
    }
}

/// <summary>
/// Papéis do time no cliente. A lista existe duplicada (o Desktop não referencia o assembly da
/// nuvem) e é ESTA amarra que impede a duplicata de envelhecer: um papel renomeado no servidor
/// aparece aqui, e não como 400 na frente do operador depois de o código já ter sido ditado.
/// </summary>
public sealed class TeamRolesTests
{
    [Fact]
    public void TodoPapelOferecido_EReconhecidoPeloServidor()
    {
        foreach (TeamRoles.Option option in TeamRoles.Options)
        {
            Assert.True(
                RemoteOps.Cloud.Rbac.Roles.IsKnown(option.Id),
                $"O papel '{option.Id}' não existe no RBAC do servidor.");
        }
    }

    [Fact]
    public void OPapelSugerido_EstaNaLista()
        => Assert.Contains(TeamRoles.Options, o => o.Id == TeamRoles.Default);

    /// <summary>Rótulo em pt-BR, sempre — e sem duplicar id nem rótulo.</summary>
    [Fact]
    public void CadaOpcao_TemRotuloEDescricao_Unicos()
    {
        Assert.Equal(TeamRoles.Options.Count, TeamRoles.Options.Select(o => o.Id).Distinct().Count());
        Assert.Equal(TeamRoles.Options.Count, TeamRoles.Options.Select(o => o.Label).Distinct().Count());
        Assert.All(TeamRoles.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Description)));
    }

    /// <summary>
    /// Papel que a lista não conhece devolve o ID CRU. Feio de propósito: um campo em branco na
    /// coluna de papel é indistinguível de binding quebrado, e esconderia do operador que a pessoa
    /// tem algum papel.
    /// </summary>
    [Fact]
    public void PapelDesconhecido_MostraOIdCru_EmVezDeVazio()
    {
        Assert.Equal("Auditor2", TeamRoles.Label("Auditor2"));
        Assert.Equal("Dono", TeamRoles.Label(TeamRoles.Owner));
        Assert.NotEmpty(TeamRoles.Label(null));
        Assert.NotEmpty(TeamRoles.Label("  "));
    }
}
