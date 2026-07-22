using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using RemoteOps.Desktop.Account;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.UnitTests.Desktop;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Render REAL (thread STA + tema de produção) do botão de <b>trocar de cofre / sair da conta</b> em
/// Configurações → Conta.
///
/// <para><b>O que este arquivo guarda:</b> que existe NA TELA um caminho até a escolha do cofre. Sem
/// ele, o operador com <c>account.amk</c> em disco — todos os que já usam o app — entra sempre no
/// mesmo cofre, e as quatro telas que mandam "escolha o time ao entrar" apontam para o nada.</para>
///
/// <para>Afirmam VISIBILIDADE EFETIVA e TEXTO, nunca "não lançou": binding quebrado no WPF não lança
/// — cai no valor padrão, e o padrão de <see cref="UIElement.Visibility"/> é
/// <see cref="Visibility.Visible"/>. Um teste de "não estourou" passaria com o botão ausente e com o
/// aviso da fila desenhado VAZIO, que são os dois modos reais de falhar aqui.</para>
/// </summary>
public sealed class SettingsVaultSwitchRenderTests
{
    private sealed class FakeSettingsStore : ISettingsStore
    {
        private AppSettings _current = new();

        public AppSettings Load() => _current;

        public void Save(AppSettings settings) => _current = settings;
    }

    private sealed class FakeVaultSwitch : IVaultSwitch
    {
        internal VaultSwitchBacklog Backlog { get; set; } = VaultSwitchBacklog.Empty;

        public Task<VaultSwitchBacklog> ReadBacklogAsync(CancellationToken ct = default)
            => Task.FromResult(Backlog);

        public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record Probe(Visibility Visibility, string Text, bool Enabled);

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
                    "SwitchVaultSection", "SwitchVaultButton", "SwitchVaultConfirmPanel",
                    "SwitchVaultConfirmDetailText", "SwitchVaultBacklogText",
                    "ConfirmSwitchVaultButton", "CancelSwitchVaultButton",
                })
                {
                    var element = (FrameworkElement?)window.FindName(name);

                    // Elemento ausente do XAML vira Collapsed: "o botão não existe" e "o botão está
                    // escondido" falham do mesmo jeito, que é o que interessa a quem olha o monitor.
                    probes[name] = element is null
                        ? new Probe(Visibility.Collapsed, string.Empty, Enabled: false)
                        : new Probe(
                            EffectiveVisibility(element),
                            TextOf(element),
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

    private static string TextOf(FrameworkElement element) => element switch
    {
        TextBlock tb => tb.Text,
        ContentControl { Content: string content } => content,
        _ => string.Concat(FindTexts(element)),
    };

    /// <summary>Visível DE VERDADE: o próprio elemento e todos os ancestrais.</summary>
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
    /// <b>O botão que faltava, na tela.</b> Visível, habilitado e com o MESMO texto que as quatro
    /// mensagens mandam o operador procurar — se o rótulo divergir da frase, ele procura um botão
    /// que não existe com aquele nome.
    /// </summary>
    [Fact]
    public void ComConta_OBotaoDeTrocarDeCofre_APARECE_ComOTextoReal()
    {
        var vm = new SettingsViewModel(new FakeSettingsStore(), vaultSwitch: new FakeVaultSwitch());

        var (error, probes) = RenderConta(vm);

        Assert.Null(error);
        Assert.Equal(Visibility.Visible, probes["SwitchVaultSection"].Visibility);
        Assert.Equal(Visibility.Visible, probes["SwitchVaultButton"].Visibility);
        Assert.True(probes["SwitchVaultButton"].Enabled);
        Assert.Equal(vm.SwitchVaultButtonText, probes["SwitchVaultButton"].Text);
        Assert.Equal(VaultSwitchText.ButtonLabel, probes["SwitchVaultButton"].Text);

        // A confirmação só aparece depois do clique: sair da conta por engano é caro.
        Assert.NotEqual(Visibility.Visible, probes["SwitchVaultConfirmPanel"].Visibility);
    }

    /// <summary>Sem conta na nuvem não há de onde sair: a seção inteira some.</summary>
    [Fact]
    public void SemConta_ASecaoDeTrocarDeCofre_NaoAPARECE()
    {
        var (error, probes) = RenderConta(new SettingsViewModel(new FakeSettingsStore()));

        Assert.Null(error);
        Assert.NotEqual(Visibility.Visible, probes["SwitchVaultSection"].Visibility);
        Assert.NotEqual(Visibility.Visible, probes["SwitchVaultButton"].Visibility);
    }

    /// <summary>
    /// ⚠️ <b>O aviso da fila aparece ANTES da troca, com o número desenhado.</b> O modo de falhar que
    /// este teste caça é o aviso visível e VAZIO (binding quebrado): a caixa aparece, o operador
    /// confirma achando que não há nada pendente, e o trabalho fica para trás.
    /// </summary>
    [Fact]
    public async Task ComFilaPendente_AConfirmacao_MOSTRA_ONumero()
    {
        var vm = new SettingsViewModel(
            new FakeSettingsStore(),
            vaultSwitch: new FakeVaultSwitch { Backlog = new VaultSwitchBacklog(12, CheckFailed: false) });
        await vm.OpenSwitchVaultAsync();

        var (error, probes) = RenderConta(vm);

        Assert.Null(error);
        Assert.Equal(Visibility.Visible, probes["SwitchVaultConfirmPanel"].Visibility);
        Assert.Equal(Visibility.Visible, probes["SwitchVaultBacklogText"].Visibility);
        Assert.Equal(vm.SwitchVaultBacklogText, probes["SwitchVaultBacklogText"].Text);
        Assert.Contains("12", probes["SwitchVaultBacklogText"].Text, StringComparison.Ordinal);
        Assert.Equal(Visibility.Visible, probes["ConfirmSwitchVaultButton"].Visibility);
        Assert.Equal(Visibility.Visible, probes["CancelSwitchVaultButton"].Visibility);

        // ⚠️ A senha é a condição para VOLTAR: sair da conta apaga o cache da AMK e, no boot
        // seguinte, cancelar o login encerra o app. Esse aviso não pode sumir da confirmação.
        Assert.Equal(Visibility.Visible, probes["SwitchVaultConfirmDetailText"].Visibility);
        Assert.Equal(vm.SwitchVaultConfirmDetail, probes["SwitchVaultConfirmDetailText"].Text);
        Assert.Contains(
            "senha da sua conta", probes["SwitchVaultConfirmDetailText"].Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A metade que impede "avisar sempre": fila medida e vazia não desenha aviso nenhum — só a
    /// confirmação. Aviso permanente é aviso que ninguém lê.
    /// </summary>
    [Fact]
    public async Task SemFilaPendente_AConfirmacao_APARECE_SemOAviso()
    {
        var vm = new SettingsViewModel(new FakeSettingsStore(), vaultSwitch: new FakeVaultSwitch());
        await vm.OpenSwitchVaultAsync();

        var (error, probes) = RenderConta(vm);

        Assert.Null(error);
        Assert.Equal(Visibility.Visible, probes["SwitchVaultConfirmPanel"].Visibility);
        Assert.NotEqual(Visibility.Visible, probes["SwitchVaultBacklogText"].Visibility);
    }
}
