using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.Sync.Remote;
using RemoteOps.UnitTests.Desktop;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Render REAL (thread STA + tema de produção) da tela de Equipe.
///
/// <para><b>Por que estes testes existem em vez de um "não lançou":</b> binding quebrado no WPF NÃO
/// lança — cai no valor default, e o default de <c>Visibility</c> é <c>Visible</c>. Um teste ingênuo
/// passaria com a lista vazia, o aviso de remoção em branco e a caixa de erro desenhada sem texto.
/// Aqui a asserção é sobre o TEXTO que ficou na tela e sobre visibilidade EFETIVA (o elemento e
/// todos os ancestrais).</para>
/// </summary>
public sealed class TeamWindowRenderTests
{
    private const string Workspace = "8f3b6f4a-0000-4000-8000-000000000001";

    private sealed class StubTeamApi : ITeamApi
    {
        public List<TeamMemberDto> Members { get; } = [];

        public Func<Exception>? ListFailure { get; set; }

        public Task<TeamMembersResponse> GetMembersAsync(
            string workspaceId, CancellationToken ct = default)
            => ListFailure is not null
                ? throw ListFailure()
                : Task.FromResult(new TeamMembersResponse([.. Members]));

        public Task<TeamMemberRemoval> RemoveMemberAsync(
            string workspaceId, string userId, CancellationToken ct = default)
            => Task.FromResult(TeamMemberRemoval.Removed);

        // Criar TIME é fora do escopo destes testes (o alvo aqui é a tela, não o ciclo do
        // workspace). NotSupportedException e não um retorno de mentira: um fake que "cria" faria o
        // teste passar por caminhos que ele não exercita.
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

        public Task<TeamWorkspaceKeyResponse?> GetWorkspaceKeyAsync(
            string workspaceId, CancellationToken ct = default)
            => Task.FromResult<TeamWorkspaceKeyResponse?>(null);

        /// <summary>Aceita a publicação do embrulho — o painel de convite desta tela passa por aqui.</summary>
        public Task<TeamKeyPublication> PublishWorkspaceKeyAsync(
            string workspaceId, PublishTeamWorkspaceKeyRequest request, CancellationToken ct = default)
            => Task.FromResult(TeamKeyPublication.Stored);
    }

    private sealed record Probe(
        IReadOnlyList<string> VisibleTexts,
        bool MembersListVisible,
        int RealizedRows,
        bool RemovalPanelVisible,
        string RemovalWarningText,
        bool LoadErrorVisible,
        string LoadErrorText,
        bool VaultBadgeVisible,
        string VaultBadgeText);

    private static Probe RenderAndProbe(TeamViewModel vm, Action<TeamWindow>? interact = null)
    {
        Probe probe = new([], false, 0, false, string.Empty, false, string.Empty, false, string.Empty);

        Exception? error = StaThreadRunner.Run(() =>
        {
            var window = new TeamWindow(vm)
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                interact?.Invoke(window);
                window.UpdateLayout();

                var list = window.FindName("MembersList") as ListBox;
                var removalPanel = window.FindName("RemovalConfirmPanel") as FrameworkElement;
                var removalWarning = window.FindName("RemovalWarningText") as TextBlock;
                var loadError = window.FindName("LoadErrorPanel") as FrameworkElement;
                var loadErrorText = window.FindName("LoadErrorText") as TextBlock;
                var badge = window.FindName("VaultBadgeText") as TextBlock;

                probe = new Probe(
                    VisibleTexts: [.. VisibleTexts(window)],
                    MembersListVisible: IsEffectivelyVisible(list),
                    RealizedRows: RealizedRows(list),
                    RemovalPanelVisible: IsEffectivelyVisible(removalPanel),
                    RemovalWarningText: removalWarning?.Text ?? string.Empty,
                    LoadErrorVisible: IsEffectivelyVisible(loadError),
                    LoadErrorText: loadErrorText?.Text ?? string.Empty,
                    VaultBadgeVisible: IsEffectivelyVisible(badge),
                    VaultBadgeText: badge?.Text ?? string.Empty);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(error is null, error?.ToString());
        return probe;
    }

    /// <summary>Containers REALIZADOS — prova que as linhas foram desenhadas, não só bindadas.</summary>
    private static int RealizedRows(ListBox? list)
    {
        if (list is null)
        {
            return 0;
        }

        int realized = 0;
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem)
            {
                realized++;
            }
        }

        return realized;
    }

    /// <summary>
    /// Visível DE VERDADE: o próprio elemento e todos os ancestrais. Checar só o
    /// <c>Visibility</c> do elemento passaria com o painel inteiro colapsado em volta dele.
    /// </summary>
    private static bool IsEffectivelyVisible(FrameworkElement? element)
    {
        if (element is null)
        {
            return false;
        }

        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return true;
    }

    private static IEnumerable<string> VisibleTexts(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Visibility: not Visibility.Visible })
            {
                continue;
            }

            if (child is TextBlock tb)
            {
                yield return tb.Text;
            }

            foreach (string nested in VisibleTexts(child))
            {
                yield return nested;
            }
        }
    }

    private static async Task<TeamViewModel> Loaded(StubTeamApi api, VaultBadgeViewModel? badge = null)
    {
        var vm = new TeamViewModel(api, Workspace, badge);
        await vm.LoadAsync();
        return vm;
    }

    // ── A lista ──────────────────────────────────────────────────────────────────────────

    /// <summary>Os membros são DESENHADOS: nome, e-mail e papel em pt-BR, um container por linha.</summary>
    [Fact]
    public async Task ListaDeMembros_EhDesenhada_ComNomeEmailEPapel()
    {
        var api = new StubTeamApi();
        api.Members.AddRange([
            new("u1", "dono@innet.tec.br", "Vagner", TeamRoles.Owner, true, 1),
            new("u2", "colega@innet.tec.br", "Marcos", TeamRoles.Operator, true, 1),
        ]);

        Probe probe = RenderAndProbe(await Loaded(api));

        Assert.True(probe.MembersListVisible);
        Assert.Equal(2, probe.RealizedRows);
        Assert.Contains("Vagner", probe.VisibleTexts);
        Assert.Contains("dono@innet.tec.br", probe.VisibleTexts);
        Assert.Contains(probe.VisibleTexts, t => t.Contains("Dono", StringComparison.Ordinal));
        Assert.Contains(probe.VisibleTexts, t => t.Contains("Técnico", StringComparison.Ordinal));
    }

    /// <summary>Quem ainda não tem a chave aparece marcado NA LINHA, não num tooltip.</summary>
    [Fact]
    public async Task MembroSemChave_ApareceEscrito_NaLinha()
    {
        var api = new StubTeamApi();
        api.Members.Add(new("u2", "novo@innet.tec.br", "Ana", TeamRoles.Operator, false, 1));

        Probe probe = RenderAndProbe(await Loaded(api));

        Assert.Contains(
            probe.VisibleTexts,
            t => t.Contains("chave do time", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Lista que não carrega: a caixa de erro aparece COM texto, e a lista some. Uma lista vazia
    /// desenhada aqui diria ao operador que ele perdeu o time.
    /// </summary>
    [Fact]
    public async Task ListaQueFalha_DesenhaOERRO_ENaoUmaListaVazia()
    {
        var api = new StubTeamApi { ListFailure = () => new HttpRequestException("rede fora (teste)") };

        Probe probe = RenderAndProbe(await Loaded(api));

        Assert.True(probe.LoadErrorVisible);
        Assert.NotEmpty(probe.LoadErrorText);
        Assert.False(probe.MembersListVisible);
        Assert.Equal(0, probe.RealizedRows);
    }

    [Fact]
    public async Task ListaNegadaPorPermissao_DesenhaORecadoCerto()
    {
        var api = new StubTeamApi { ListFailure = () => new CloudSyncException(HttpStatusCode.Forbidden) };

        Probe probe = RenderAndProbe(await Loaded(api));

        Assert.True(probe.LoadErrorVisible);
        Assert.Contains("permissão", probe.LoadErrorText, StringComparison.OrdinalIgnoreCase);
    }

    // ── A remoção ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>O teste que mais importa.</b> Ao remover, a verdade inteira está VISÍVEL na tela — não
    /// num tooltip, não num log: corta o futuro, não apaga o passado, troque as senhas.
    /// </summary>
    [Fact]
    public async Task AoRemover_AVerdadeInteira_FicaVISIVEL_NaTela()
    {
        var api = new StubTeamApi();
        api.Members.Add(new("u2", "colega@innet.tec.br", "Marcos", TeamRoles.Operator, true, 1));
        TeamViewModel vm = await Loaded(api);
        vm.RemoveCommand.Execute(vm.Members[0]);

        Probe probe = RenderAndProbe(vm);

        Assert.True(probe.RemovalPanelVisible);
        Assert.Equal(TeamViewModel.RemovalTruth, probe.RemovalWarningText);
        Assert.Contains("Isso corta o acesso daqui pra frente", probe.RemovalWarningText, StringComparison.Ordinal);
        Assert.Contains("trocadas nos equipamentos", probe.RemovalWarningText, StringComparison.Ordinal);
        Assert.Contains(probe.RemovalWarningText, probe.VisibleTexts);

        // E o nome de quem vai sair está na frente do operador — remover a pessoa errada é
        // irreversível para o acesso dela.
        Assert.Contains(probe.VisibleTexts, t => t.Contains("Marcos", StringComparison.Ordinal));
    }

    /// <summary>Antes de clicar em remover, a confirmação NÃO está na tela.</summary>
    [Fact]
    public async Task AntesDeRemover_AConfirmacaoNaoApareceu()
    {
        var api = new StubTeamApi();
        api.Members.Add(new("u2", "colega@innet.tec.br", "Marcos", TeamRoles.Operator, true, 1));

        Probe probe = RenderAndProbe(await Loaded(api));

        Assert.False(probe.RemovalPanelVisible);
    }

    /// <summary>
    /// O botão "Remover" de dentro da linha está ligado ao comando REAL e leva a pessoa daquela
    /// linha — clicar nele de verdade, na thread da UI, abre a confirmação daquele membro.
    /// </summary>
    [Fact]
    public async Task ClicarEmRemover_NaLinha_AbreAConfirmacaoDAQUELAPessoa()
    {
        var api = new StubTeamApi();
        api.Members.AddRange([
            new("u1", "dono@innet.tec.br", "Vagner", TeamRoles.Owner, true, 1),
            new("u2", "colega@innet.tec.br", "Marcos", TeamRoles.Operator, true, 1),
        ]);
        TeamViewModel vm = await Loaded(api);

        Exception? error = StaThreadRunner.Run(() =>
        {
            var window = new TeamWindow(vm)
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                var list = (ListBox)window.FindName("MembersList")!;
                var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1)!;
                Button remove = Descendants<Button>(row)
                    .First(b => b.Command is not null && ReferenceEquals(b.Command, vm.RemoveCommand));

                remove.Command!.Execute(remove.CommandParameter);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(error is null, error?.ToString());
        Assert.True(vm.IsRemovalConfirmVisible);
        Assert.Equal("u2", vm.RemovalTarget?.UserId);
    }

    // ── O indicador de cofre dentro da tela ──────────────────────────────────────────────

    /// <summary>
    /// A tela de Equipe carrega o indicador de cofre desenhado. É aqui que o operador está pensando
    /// "o que é do time e o que é meu" — e o indicador tem de dizer a verdade DESTA sessão, inclusive
    /// quando ela é "cofre do time, mas a chave ainda não chegou".
    /// </summary>
    [Fact]
    public async Task OIndicadorDeCofre_EstaDesenhado_NaTelaDeEquipe()
    {
        var badge = new VaultBadgeViewModel();
        badge.Apply(VaultScope.TeamPending);
        var api = new StubTeamApi();
        api.Members.Add(new("u1", "dono@innet.tec.br", "Vagner", TeamRoles.Owner, true, 1));

        Probe probe = RenderAndProbe(await Loaded(api, badge));

        Assert.True(probe.VaultBadgeVisible);
        Assert.Equal(badge.Label, probe.VaultBadgeText);
        Assert.Contains(
            probe.VisibleTexts,
            t => t.Contains("chave", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// O seletor de papel do convite é um <c>ComboBox</c>: o popup dele precisa ABRIR com os itens
    /// desenhados e legíveis. Item de popup sem estilo implícito cai no Aero2 CLARO — texto branco
    /// em fundo branco — e o teste que não abre o popup não vê nada disso.
    /// </summary>
    [Fact]
    public void SeletorDePapel_AbreOPopup_ComOsItensDesenhados()
    {
        using var ring = TeamKeyRingFactory.New(new byte[32]);
        var vm = new TeamInviteViewModel(
            new RemoteOps.Desktop.Account.TeamInviteService(new StubTeamApi(), ring),
            Workspace,
            TeamInviteMode.Generate,
            copyToClipboard: _ => { });

        var textos = new List<string>();
        int itens = 0;
        bool popupAberto = false;

        Exception? error = StaThreadRunner.Run(() =>
        {
            var window = new TeamInviteWindow(vm)
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                var combo = (ComboBox)window.FindName("RoleField")!;
                combo.IsDropDownOpen = true;
                window.UpdateLayout();
                combo.UpdateLayout();

                popupAberto = combo.IsDropDownOpen;
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    if (combo.ItemContainerGenerator.ContainerFromIndex(i) is ComboBoxItem item)
                    {
                        itens++;
                        textos.AddRange(Descendants<TextBlock>(item).Select(t => t.Text));
                    }
                }

                combo.IsDropDownOpen = false;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(error is null, error?.ToString());
        Assert.True(popupAberto);
        Assert.Equal(TeamRoles.Options.Count, itens);
        Assert.Contains("Técnico", textos);
        Assert.Contains("Dono", textos);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
