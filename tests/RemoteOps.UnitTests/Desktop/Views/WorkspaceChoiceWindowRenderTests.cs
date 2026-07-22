using System;
using System.Collections.Generic;
using System.Linq;
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
/// Render REAL (thread STA + tema de produção) da tela de escolha do cofre.
///
/// <para><b>Afirma VISIBILIDADE e TEXTO, nunca só "não lançou".</b> Binding quebrado no WPF não
/// lança: cai no valor padrão. E o padrão de <c>Visibility</c> é <c>Visible</c> — ou seja, um teste
/// que só checasse "abriu sem exceção" passaria com a lista VAZIA e o botão morto na tela do
/// operador. Aqui a asserção é sobre o que está desenhado: os nomes dos cofres, o papel de cada um e
/// o botão ligado ao comando certo.</para>
/// </summary>
public sealed class WorkspaceChoiceWindowRenderTests
{
    private sealed record Probe(
        Visibility ListVisibility,
        int ItemsRendered,
        IReadOnlyList<string> Texts,
        string ButtonText,
        Visibility ButtonVisibility,
        bool ButtonEnabled,
        object? ButtonCommand);

    private static (Exception? Error, Probe Result) RenderAndProbe(WorkspaceChoiceViewModel vm)
    {
        var probe = new Probe(Visibility.Collapsed, 0, [], string.Empty, Visibility.Collapsed, false, null);

        Exception? error = StaThreadRunner.Run(() =>
        {
            var window = new WorkspaceChoiceWindow(vm)
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

                var list = FindByName<ListBox>(window, "WorkspaceList");
                var button = FindByName<Button>(window, "ConfirmButton");

                // O ItemsControl precisa ter REALIZADO os containers: é isso que prova que o
                // ItemsSource chegou. Um binding perdido deixaria a lista vazia e visível.
                int realized = 0;
                if (list is not null)
                {
                    list.UpdateLayout();
                    for (int i = 0; i < list.Items.Count; i++)
                    {
                        if (list.ItemContainerGenerator.ContainerFromIndex(i) is not null)
                        {
                            realized++;
                        }
                    }
                }

                probe = new Probe(
                    ListVisibility: list?.Visibility ?? Visibility.Collapsed,
                    ItemsRendered: realized,
                    Texts: [.. VisibleTexts(window)],
                    ButtonText: button?.Content as string ?? string.Empty,
                    ButtonVisibility: button?.Visibility ?? Visibility.Collapsed,
                    ButtonEnabled: button?.IsEnabled ?? false,
                    ButtonCommand: button?.Command);
            }
            finally
            {
                window.Close();
            }
        });

        return (error, probe);
    }

    private static T? FindByName<T>(FrameworkElement root, string name)
        where T : FrameworkElement => root.FindName(name) as T;

    /// <summary>Só o texto REALMENTE desenhado: elemento colapsado não conta como "está na tela".</summary>
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

    private static WorkspaceChoiceViewModel TwoWorkspaces() => new(
    [
        new AccountWorkspace("ws-pessoal", "Meu cofre", "Owner"),
        new AccountWorkspace("ws-time", "Innet Telecom", "Manager"),
    ]);

    [Fact]
    public void Tela_Mostra_Os_Dois_Cofres_Com_Nome_E_Papel()
    {
        WorkspaceChoiceViewModel vm = TwoWorkspaces();

        var (error, probe) = RenderAndProbe(vm);

        Assert.Null(error);
        Assert.Equal(Visibility.Visible, probe.ListVisibility);
        Assert.Equal(2, probe.ItemsRendered);

        // Os NOMES estão desenhados — não é uma lista vazia visível.
        Assert.Contains("Meu cofre", probe.Texts);
        Assert.Contains("Innet Telecom", probe.Texts);

        // E o papel de cada um, que é o que diz o que a pessoa pode fazer lá dentro.
        Assert.Contains("Seu papel: Owner", probe.Texts);
        Assert.Contains("Seu papel: Manager", probe.Texts);
    }

    /// <summary>
    /// O texto que explica a consequência da escolha tem de estar VISÍVEL: é ele que evita o
    /// cadastro do host do cliente no cofre pessoal por engano.
    /// </summary>
    [Fact]
    public void Tela_Explica_Que_O_Cadastro_Vai_Para_O_Cofre_Escolhido()
    {
        WorkspaceChoiceViewModel vm = TwoWorkspaces();

        var (error, probe) = RenderAndProbe(vm);

        Assert.Null(error);
        Assert.Contains(probe.Texts, t => t.Contains("Em qual cofre", StringComparison.Ordinal));

        // O texto desenhado é comparado com a CONSTANTE da VM, e não com uma substring escrita à
        // mão. Substring passa com o binding perdido desde que outro TextBlock qualquer contenha o
        // trecho — e passava mesmo quando a explicação mandava o operador para um controle que não
        // existe ("saia da conta"). Igualdade com a constante amarra as duas pontas.
        Assert.Contains(WorkspaceChoiceViewModel.ExplanationText, probe.Texts);
    }

    /// <summary>
    /// O botão está ligado ao comando REAL do VM — comando nulo (binding perdido) apareceria
    /// habilitado e não faria nada ao clicar, que é o pior desfecho possível numa tela de escolha.
    /// Clicar de verdade (com a janela viva, na thread da UI) fecha a janela e fixa a escolha.
    /// </summary>
    [Fact]
    public void Botao_Confirmar_Esta_Ligado_Ao_Comando_E_Escolhe_O_Selecionado()
    {
        WorkspaceChoiceViewModel vm = TwoWorkspaces();
        vm.Selected = vm.Workspaces.First(w => w.Workspace.Id == "ws-time");

        bool confirmou = false;
        vm.Confirmed += (_, _) => confirmou = true;

        object? command = null;
        string buttonText = string.Empty;
        Visibility buttonVisibility = Visibility.Collapsed;
        bool enabled = false;

        Exception? error = StaThreadRunner.Run(() =>
        {
            var window = new WorkspaceChoiceWindow(vm)
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

                var button = FindByName<Button>(window, "ConfirmButton");
                command = button?.Command;
                buttonText = button?.Content as string ?? string.Empty;
                buttonVisibility = button?.Visibility ?? Visibility.Collapsed;
                enabled = button?.IsEnabled ?? false;

                // Clique REAL pelo comando bindado — é o que exercita o handler da janela também.
                button!.Command!.Execute(button.CommandParameter);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Null(error);
        Assert.Equal(Visibility.Visible, buttonVisibility);
        Assert.Equal("Entrar neste cofre", buttonText);
        Assert.True(enabled);
        Assert.Same(vm.ConfirmCommand, command);
        Assert.True(confirmou);
        Assert.Equal("ws-time", vm.Chosen!.Id);
    }
}
