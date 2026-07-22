using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using RemoteOps.Desktop.Account;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.Security.Account;
using RemoteOps.Sync.Remote;
using RemoteOps.UnitTests.Desktop;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Render REAL (thread STA + tema de produção) da seção <b>Equipe</b> em Configurações → Conta.
///
/// <para><b>O que estes testes guardam:</b> que a tela não oferece "Convidar alguém…" numa sessão de
/// cofre PESSOAL — onde o convite entregaria os ~700 clientes do operador — e que ela DIZ por quê,
/// em vez de simplesmente sumir. E que existe um caminho para criar o time, com o aviso de que ele
/// nasce vazio.</para>
///
/// <para>Afirmam VISIBILIDADE e TEXTO, nunca "não lançou": binding quebrado no WPF NÃO lança — cai no
/// valor padrão, e o padrão de <see cref="UIElement.Visibility"/> é <see cref="Visibility.Visible"/>.
/// Um teste de "não estourou" passaria com o botão de convite desenhado numa sessão pessoal, que é
/// exatamente o defeito. Chave de <c>DynamicResource</c> inexistente é a outra armadilha que só
/// aparece aqui (já mordeu com <c>Brush.Accent</c> e <c>Brush.Status.Warning</c>).</para>
/// </summary>
public sealed class SettingsTeamSectionRenderTests
{
    private const string Workspace = "8f3b6f4a-0000-4000-8000-000000000001";

    private sealed class FakeSettingsStore : ISettingsStore
    {
        private AppSettings _current = new();

        public AppSettings Load() => _current;

        public void Save(AppSettings settings) => _current = settings;
    }

    /// <summary>Transporte inerte: estes testes olham a TELA, não a rede.</summary>
    private sealed class StubTeamApi : ITeamApi
    {
        public Task<CreateTeamWorkspaceResponse> CreateWorkspaceAsync(
            CreateTeamWorkspaceRequest request, CancellationToken ct = default)
            => Task.FromResult(new CreateTeamWorkspaceResponse(request.Id, request.Name, "Owner"));

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

        public Task<TeamKeyPublication> PublishWorkspaceKeyAsync(
            string workspaceId, PublishTeamWorkspaceKeyRequest request, CancellationToken ct = default)
            => Task.FromResult(TeamKeyPublication.Stored);

        public Task<TeamMembersResponse> GetMembersAsync(
            string workspaceId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TeamMemberRemoval> RemoveMemberAsync(
            string workspaceId, string userId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static SettingsViewModel BuildVm(SessionVaultKind kind, out WkWorkspaceKeyRing ring)
    {
        ring = TeamKeyRingFactory.New(RandomNumberGenerator.GetBytes(32));
        var api = new StubTeamApi();
        var service = new TeamInviteService(api, ring, kind);
        return new SettingsViewModel(
            new FakeSettingsStore(), team: new TeamContext(service, api, Workspace, kind));
    }

    private sealed record Probe(Visibility Visibility, string Text, bool Enabled);

    /// <summary>
    /// Abre as Configurações JÁ na aba Conta (o TabControl só realiza a aba selecionada) e devolve o
    /// estado REAL dos elementos nomeados da seção de Equipe. Elemento ausente do XAML vira
    /// <see cref="Visibility.Collapsed"/> — assim "o botão não existe" e "o botão está escondido"
    /// falham do mesmo jeito, que é o que interessa a quem olha o monitor.
    /// </summary>
    private static (Exception? Error, Dictionary<string, Probe> Probes) RenderConta(SettingsViewModel vm)
    {
        var probes = new Dictionary<string, Probe>(StringComparer.Ordinal);

        Exception? error = StaThreadRunner.Run(() =>
        {
            var window = new SettingsWindow(vm, initialTab: "Conta")
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                foreach (string name in new[]
                {
                    "TeamSection", "ManageTeamButton", "InviteToTeamButton", "CreateTeamButton",
                    "JoinTeamButton", "TeamScopeNoticePanel", "TeamScopeNoticeText",
                })
                {
                    var element = (FrameworkElement?)window.FindName(name);
                    probes[name] = element is null
                        ? new Probe(Visibility.Collapsed, string.Empty, Enabled: false)
                        : new Probe(
                            EffectiveVisibility(element),
                            element is TextBlock tb ? tb.Text : string.Concat(FindTexts(element)),
                            element.IsEnabled);
                }
            }
            finally
            {
                window.Close();
            }
        });

        return (error, probes);
    }

    /// <summary>
    /// Visível DE VERDADE: o próprio elemento e todos os ancestrais. Olhar só o
    /// <see cref="UIElement.Visibility"/> do botão passaria com a seção inteira colapsada em volta.
    /// </summary>
    private static Visibility EffectiveVisibility(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return Visibility.Collapsed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return Visibility.Visible;
    }

    private static IEnumerable<string> FindTexts(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb)
            {
                yield return tb.Text + " ";
            }

            foreach (string nested in FindTexts(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// <b>O teste que fecha o vazamento na tela.</b> Sessão do cofre PESSOAL: nada de "Convidar
    /// alguém…" nem de "Ver a equipe…". No lugar deles, a explicação — e o caminho para criar o time.
    /// </summary>
    [Fact]
    public void SessaoPESSOAL_NaoDESENHA_OConvite_EExplicaOMotivo()
    {
        SettingsViewModel vm = BuildVm(SessionVaultKind.Personal, out WkWorkspaceKeyRing ring);
        using (ring)
        {
            var (error, probes) = RenderConta(vm);

            Assert.Null(error);
            Assert.Equal(Visibility.Visible, probes["TeamSection"].Visibility);

            Assert.NotEqual(Visibility.Visible, probes["InviteToTeamButton"].Visibility);
            Assert.NotEqual(Visibility.Visible, probes["ManageTeamButton"].Visibility);

            // O caminho de saída fica NA TELA: criar o time e (para quem foi convidado) entrar num.
            Assert.Equal(Visibility.Visible, probes["CreateTeamButton"].Visibility);
            Assert.True(probes["CreateTeamButton"].Enabled);
            Assert.Equal(Visibility.Visible, probes["JoinTeamButton"].Visibility);

            // E o motivo está ESCRITO, não subentendido pelo botão que sumiu.
            Assert.Equal(Visibility.Visible, probes["TeamScopeNoticePanel"].Visibility);
            Assert.Equal(vm.TeamScopeNotice, probes["TeamScopeNoticeText"].Text);
            Assert.Contains(
                "cofre pessoal", probes["TeamScopeNoticeText"].Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A metade que impede "esconder tudo": na sessão de TIME o convite e a lista de membros são
    /// desenhados, e o aviso de escopo NÃO aparece — aviso permanente é aviso que ninguém lê.
    /// </summary>
    [Fact]
    public void SessaoDeTIME_DESENHA_OConvite_ESemOAvisoDeEscopo()
    {
        SettingsViewModel vm = BuildVm(SessionVaultKind.Team, out WkWorkspaceKeyRing ring);
        using (ring)
        {
            var (error, probes) = RenderConta(vm);

            Assert.Null(error);
            Assert.Equal(Visibility.Visible, probes["InviteToTeamButton"].Visibility);
            Assert.True(probes["InviteToTeamButton"].Enabled);
            Assert.Equal(Visibility.Visible, probes["ManageTeamButton"].Visibility);
            Assert.Equal(Visibility.Visible, probes["CreateTeamButton"].Visibility);

            Assert.NotEqual(Visibility.Visible, probes["TeamScopeNoticePanel"].Visibility);
        }
    }

    /// <summary>Sem conta na nuvem a seção inteira some: sem servidor não há time nenhum.</summary>
    [Fact]
    public void SemConta_ASecaoInteiraSome()
    {
        var vm = new SettingsViewModel(new FakeSettingsStore());

        var (error, probes) = RenderConta(vm);

        Assert.Null(error);
        Assert.NotEqual(Visibility.Visible, probes["TeamSection"].Visibility);
        Assert.NotEqual(Visibility.Visible, probes["CreateTeamButton"].Visibility);
    }
}
