using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using RemoteOps.Desktop.Account;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.Security.Account;
using RemoteOps.Security.Storage;
using RemoteOps.Sync.Remote;
using RemoteOps.UnitTests.Desktop;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Render REAL (thread STA + tema de produção) da janela de convite do time.
///
/// <para><b>O alvo principal é o AVISO.</b> "O código não vai no e-mail — mande por outro canal" é
/// o que faz do convite duas metades: sem ele, o operador cola o código no mesmo e-mail e qualquer
/// caixa de entrada invadida vira acesso ao cofre do time. Por isso o teste afirma que o texto está
/// VISÍVEL e desenhado, e não só que a janela abriu: binding quebrado no WPF não lança — cai no
/// default, e o default de <c>Visibility</c> é <c>Visible</c>, o que faria um teste ingênuo passar
/// com a caixa do aviso vazia na tela.</para>
/// </summary>
public sealed class TeamInviteWindowRenderTests
{
    private const string Workspace = "8f3b6f4a-0000-4000-8000-000000000001";

    /// <summary>API de time que não fala com ninguém: devolve um convite pronto.</summary>
    private sealed class StubTeamApi : ITeamApi
    {
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

        public Task<TeamInviteContextResponse> GetInviteContextAsync(
            string inviteId, string codeHash, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AcceptTeamInviteResponse> AcceptInviteAsync(
            string inviteId, AcceptTeamInviteRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TeamWorkspaceKeyResponse?> GetWorkspaceKeyAsync(
            string workspaceId, CancellationToken ct = default)
            => Task.FromResult<TeamWorkspaceKeyResponse?>(null);

        /// <summary>Aceita a publicação do embrulho do dono — é o que o convite faz antes de subir.</summary>
        public Task<TeamKeyPublication> PublishWorkspaceKeyAsync(
            string workspaceId, PublishTeamWorkspaceKeyRequest request, CancellationToken ct = default)
            => Task.FromResult(TeamKeyPublication.Stored);

        // A janela de convite não lista nem remove ninguém: quem faz isso é a TeamWindow (1e).
        public Task<TeamMembersResponse> GetMembersAsync(
            string workspaceId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TeamMemberRemoval> RemoveMemberAsync(
            string workspaceId, string userId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <param name="sessionKind">
    /// Que cofre a sessão abriu. O default é <see cref="SessionVaultKind.Team"/> porque a maioria
    /// destes testes encena o dono do time; a sessão PESSOAL tem teste próprio, e ali o alvo é a
    /// RECUSA aparecendo escrita na tela.
    /// </param>
    private static TeamInviteViewModel NewViewModel(
        TeamInviteMode mode,
        out WkWorkspaceKeyRing ring,
        SessionVaultKind sessionKind = SessionVaultKind.Team)
    {
        ring = TeamKeyRingFactory.New(new byte[32]);
        var api = new StubTeamApi();
        var service = new TeamInviteService(api, ring, sessionKind);
        return new TeamInviteViewModel(
            new TeamContext(service, api, Workspace, sessionKind),
            mode,
            copyToClipboard: _ => { });
    }

    private sealed record Probe(
        IReadOnlyList<string> VisibleTexts,
        Visibility WarningVisibility,
        string WarningText,
        string CodeText,
        Visibility GenerateButtonVisibility,
        Visibility AcceptButtonVisibility,
        Visibility CreateTeamButtonVisibility,
        Visibility EmptyTeamWarningVisibility,
        string EmptyTeamWarningText,
        Visibility ErrorBoxVisibility,
        string ErrorText);

    private static (Exception? Error, Probe Result) RenderAndProbe(TeamInviteViewModel vm)
    {
        var probe = new Probe([], Visibility.Collapsed, string.Empty, string.Empty,
            Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed,
            string.Empty, Visibility.Collapsed, string.Empty);

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

                var warning = window.FindName("WarningText") as TextBlock;
                var code = window.FindName("GeneratedCodeText") as TextBlock;
                var generate = window.FindName("GenerateButton") as Button;
                var accept = window.FindName("AcceptButton") as Button;
                var createTeam = window.FindName("CreateTeamButton") as Button;
                var emptyWarning = window.FindName("EmptyTeamWarningText") as TextBlock;
                var errorBox = window.FindName("ErrorBox") as FrameworkElement;
                var errorText = window.FindName("ErrorText") as TextBlock;

                probe = new Probe(
                    VisibleTexts: [.. VisibleTexts(window)],
                    WarningVisibility: IsEffectivelyVisible(warning) ? Visibility.Visible : Visibility.Collapsed,
                    WarningText: warning?.Text ?? string.Empty,
                    CodeText: code?.Text ?? string.Empty,
                    GenerateButtonVisibility: IsEffectivelyVisible(generate) ? Visibility.Visible : Visibility.Collapsed,
                    AcceptButtonVisibility: IsEffectivelyVisible(accept) ? Visibility.Visible : Visibility.Collapsed,
                    CreateTeamButtonVisibility: IsEffectivelyVisible(createTeam) ? Visibility.Visible : Visibility.Collapsed,
                    EmptyTeamWarningVisibility: IsEffectivelyVisible(emptyWarning) ? Visibility.Visible : Visibility.Collapsed,
                    EmptyTeamWarningText: emptyWarning?.Text ?? string.Empty,
                    ErrorBoxVisibility: IsEffectivelyVisible(errorBox) ? Visibility.Visible : Visibility.Collapsed,
                    ErrorText: errorText?.Text ?? string.Empty);
            }
            finally
            {
                window.Close();
            }
        });

        return (error, probe);
    }

    /// <summary>
    /// Visível DE VERDADE: o próprio elemento e todos os ancestrais. Checar só o
    /// <c>Visibility</c> do TextBlock passaria com o painel inteiro colapsado em volta dele.
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

    /// <summary>
    /// Antes de gerar não há código na tela — e, coerentemente, nem aviso: um aviso sobre um código
    /// que ainda não existe é ruído que ensina o operador a ignorar avisos.
    /// </summary>
    [Fact]
    public void ModoGerar_AntesDeGerar_MostraOFormulario_SemCodigo()
    {
        TeamInviteViewModel vm = NewViewModel(TeamInviteMode.Generate, out WkWorkspaceKeyRing ring);
        using (ring)
        {
            var (error, probe) = RenderAndProbe(vm);

            Assert.Null(error);
            Assert.Equal(Visibility.Visible, probe.GenerateButtonVisibility);
            Assert.Equal(Visibility.Collapsed, probe.AcceptButtonVisibility);
            Assert.Equal(Visibility.Collapsed, probe.WarningVisibility);
            Assert.Contains("Convidar alguém para o time", probe.VisibleTexts);
        }
    }

    /// <summary>
    /// <b>O teste que importa.</b> Depois de gerar, o código aparece na tela E o aviso de canal
    /// separado está VISÍVEL, com o texto inteiro — não uma caixa vazia com a borda vermelha.
    /// </summary>
    [Fact]
    public async Task ModoGerar_DepoisDeGerar_MostraOCodigo_EOAvisoDeCanalSeparado()
    {
        TeamInviteViewModel vm = NewViewModel(TeamInviteMode.Generate, out WkWorkspaceKeyRing ring);
        using (ring)
        {
            vm.Email = "colega@innet.tec.br";
            await vm.GenerateAsync();
            Assert.False(vm.HasError, vm.ErrorMessage);

            var (error, probe) = RenderAndProbe(vm);

            Assert.Null(error);
            Assert.Equal(Visibility.Visible, probe.WarningVisibility);
            Assert.Contains("NÃO VAI NO E-MAIL", probe.WarningText, StringComparison.Ordinal);
            Assert.Contains("outro canal", probe.WarningText, StringComparison.Ordinal);
            Assert.Equal(TeamInviteViewModel.OutOfBandWarning, probe.WarningText);

            // O código sorteado está DESENHADO (e é o mesmo do VM), não só na memória.
            Assert.Equal(vm.GeneratedCode, probe.CodeText);
            Assert.NotEmpty(probe.CodeText);
            Assert.Contains(probe.CodeText, probe.VisibleTexts);
        }
    }

    /// <summary>Modo aceitar: os dois campos do convidado, e nada do lado de quem convida.</summary>
    [Fact]
    public void ModoAceitar_MostraIdentificadorECodigo()
    {
        TeamInviteViewModel vm = NewViewModel(TeamInviteMode.Accept, out WkWorkspaceKeyRing ring);
        using (ring)
        {
            var (error, probe) = RenderAndProbe(vm);

            Assert.Null(error);
            Assert.Equal(Visibility.Visible, probe.AcceptButtonVisibility);
            Assert.Equal(Visibility.Collapsed, probe.GenerateButtonVisibility);
            Assert.Contains("Entrar num time", probe.VisibleTexts);
            Assert.Contains("Identificador do convite", probe.VisibleTexts);
            Assert.Contains("Código do convite", probe.VisibleTexts);
        }
    }

    // ── Criar o time ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>O aviso do time VAZIO tem de estar na tela ANTES do clique.</b> Quem tem ~700 clientes
    /// cadastrados espera vê-los do outro lado; o time é um workspace novo e nasce sem nada. Uma
    /// expectativa frustrada aqui vira o operador procurando os equipamentos e concluindo que a
    /// sincronização quebrou.
    ///
    /// <para>O teste afirma VISIBILIDADE e TEXTO: binding quebrado no WPF não lança — cai no default,
    /// e o default de <see cref="UIElement.Visibility"/> é <see cref="Visibility.Visible"/>. Um teste
    /// de "não estourou" passaria com a caixa do aviso desenhada e VAZIA.</para>
    /// </summary>
    [Fact]
    public void ModoCriarTime_MostraOAvisoDeTimeVAZIO_EOBotaoDeCriar()
    {
        TeamInviteViewModel vm = NewViewModel(
            TeamInviteMode.CreateTeam, out WkWorkspaceKeyRing ring, SessionVaultKind.Personal);
        using (ring)
        {
            var (error, probe) = RenderAndProbe(vm);

            Assert.Null(error);
            Assert.Equal(Visibility.Visible, probe.CreateTeamButtonVisibility);
            Assert.Equal(Visibility.Visible, probe.EmptyTeamWarningVisibility);
            Assert.Equal(TeamInviteViewModel.EmptyTeamWarning, probe.EmptyTeamWarningText);
            Assert.Contains("VAZIO", probe.EmptyTeamWarningText, StringComparison.Ordinal);
            Assert.Contains("NÃO vão junto", probe.EmptyTeamWarningText, StringComparison.Ordinal);

            // E os outros dois lados da janela ficam fora: um formulário de convite aberto junto
            // faria o operador achar que criar e convidar são o mesmo clique.
            Assert.Equal(Visibility.Collapsed, probe.GenerateButtonVisibility);
            Assert.Equal(Visibility.Collapsed, probe.AcceptButtonVisibility);
            Assert.Contains("Criar um time", probe.VisibleTexts);
            Assert.Contains("Nome do time", probe.VisibleTexts);
        }
    }

    /// <summary>
    /// <b>A recusa do cofre pessoal APARECE ESCRITA.</b> Se a janela de convite for aberta numa
    /// sessão pessoal (um caminho que a tela não oferece mais, mas que um refactor pode reabrir), o
    /// operador lê o motivo e o que fazer — nunca um "não foi possível concluir a operação", que o
    /// faria tentar de novo achando que foi a rede.
    /// </summary>
    [Fact]
    public async Task ModoGerar_NaSessaoPESSOAL_ARecusaAparecEscritaNaTela()
    {
        TeamInviteViewModel vm = NewViewModel(
            TeamInviteMode.Generate, out WkWorkspaceKeyRing ring, SessionVaultKind.Personal);
        using (ring)
        {
            vm.Email = "colega@innet.tec.br";
            await vm.GenerateAsync();

            var (error, probe) = RenderAndProbe(vm);

            Assert.Null(error);
            Assert.Equal(Visibility.Visible, probe.ErrorBoxVisibility);
            Assert.Equal(TeamInviteService.PersonalSessionRefusal, probe.ErrorText);
            Assert.Contains("COFRE PESSOAL", probe.ErrorText, StringComparison.Ordinal);

            // E nenhum código foi sorteado/desenhado: recusar é recusar.
            Assert.Equal(Visibility.Collapsed, probe.WarningVisibility);
            Assert.Empty(probe.CodeText);
        }
    }

    /// <summary>
    /// Erro na tela do convidado: a caixa vermelha aparece COM o texto. É o "código errado avisa" do
    /// ponto de vista de quem está olhando o monitor.
    /// </summary>
    [Fact]
    public async Task ModoAceitar_SemCodigo_MostraOErroNaTela()
    {
        TeamInviteViewModel vm = NewViewModel(TeamInviteMode.Accept, out WkWorkspaceKeyRing ring);
        using (ring)
        {
            vm.InviteId = "3c9d1a2b-0000-4000-8000-000000000002";
            await vm.AcceptAsync();
            Assert.True(vm.HasError);

            var (error, probe) = RenderAndProbe(vm);

            Assert.Null(error);
            Assert.Contains(
                probe.VisibleTexts,
                t => t.Contains("Informe o código", StringComparison.Ordinal));
        }
    }
}
