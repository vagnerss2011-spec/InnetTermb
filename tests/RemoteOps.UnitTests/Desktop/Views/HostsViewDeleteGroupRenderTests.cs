using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.UnitTests.Desktop;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Renderização REAL (thread STA + tema de produção) do "Excluir grupo" no menu de contexto do card.
///
/// <para>Afirma VISIBILIDADE, TEXTO e o COMANDO ligado — não apenas "não lançou". Binding quebrado no
/// WPF não lança: cai no valor padrão, e o padrão de <c>Command</c> é <c>null</c> — um MenuItem com
/// comando nulo aparece HABILITADO e não faz nada ao clicar. Um teste que só checasse "abriu sem
/// exceção" passaria com o menu completamente morto.</para>
///
/// <para>O menu vive num <c>Popup</c> FORA da árvore visual do card, e o comando mora no
/// <see cref="HostsViewModel"/> (o DataContext do card é o <see cref="GroupCardViewModel"/>): é
/// exatamente o tipo de ponte de binding que compila liso e quebra na tela do operador.</para>
/// </summary>
public sealed class HostsViewDeleteGroupRenderTests
{
    private static SessionLauncher Launcher() =>
        new(new TabsViewModel(), winBox: null, flags: null, ssh: null, telnet: null, rdp: null, rdpCred: null);

    private sealed record MenuProbe(
        bool FoundCard,
        bool MenuOpened,
        string ItemText,
        Visibility ItemVisibility,
        bool ItemEnabledBeforeTarget,
        bool ItemEnabledAfterTarget,
        object? ItemCommand);

    /// <summary>
    /// Monta a view de verdade, acha o card do grupo, dispara o clique-direito (o mesmo evento que o
    /// code-behind escuta para fixar o alvo), abre o menu e inspeciona o item.
    /// </summary>
    private static (Exception? Error, MenuProbe Probe) RenderAndProbe(HostsViewModel vm)
    {
        var probe = new MenuProbe(false, false, string.Empty, Visibility.Collapsed, true, false, null);

        Exception? error = StaThreadRunner.Run(() =>
        {
            var view = new HostsView { DataContext = vm };
            var window = new Window
            {
                Content = view,
                Width = 900,
                Height = 600,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                vm.LoadAsync().GetAwaiter().GetResult();
                window.UpdateLayout();

                Border? card = FindCardBorder(view);
                if (card?.ContextMenu is not { } menu)
                {
                    return;
                }

                MenuItem item = menu.Items.OfType<MenuItem>().Single();

                // Antes de escolher o alvo o item tem de estar DESABILITADO — é isso que prova que o
                // Command chegou de verdade (comando nulo ficaria habilitado).
                menu.PlacementTarget = card;
                menu.IsOpen = true;
                window.UpdateLayout();
                menu.UpdateLayout();
                bool enabledBefore = item.IsEnabled;
                menu.IsOpen = false;

                // Clique-direito no card: o code-behind fixa SelectedGroup antes do menu abrir.
                card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                {
                    RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
                });

                menu.IsOpen = true;
                window.UpdateLayout();
                menu.UpdateLayout();

                probe = new MenuProbe(
                    FoundCard: true,
                    MenuOpened: menu.IsOpen,
                    ItemText: string.Concat(Texts(item)),
                    ItemVisibility: item.Visibility,
                    ItemEnabledBeforeTarget: enabledBefore,
                    ItemEnabledAfterTarget: item.IsEnabled,
                    ItemCommand: item.Command);

                menu.IsOpen = false;
            }
            finally
            {
                window.Close();
            }
        });

        return (error, probe);
    }

    /// <summary>Acha o Border do card do grupo (o único com ContextMenu e DataContext de card).</summary>
    private static Border? FindCardBorder(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Border { ContextMenu: not null, DataContext: GroupCardViewModel } border)
            {
                return border;
            }

            if (FindCardBorder(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb)
            {
                yield return tb.Text;
            }

            foreach (string nested in Texts(child))
            {
                yield return nested;
            }
        }
    }

    [Fact]
    public async Task Card_De_Grupo_Mostra_Excluir_Grupo_Ligado_Ao_Comando()
    {
        var store = new InMemoryLocalStore();
        await store.AddGroupAsync("ws-local", "Innet");
        var vm = new HostsViewModel(store, Launcher(), "ws-local");

        var (error, probe) = RenderAndProbe(vm);

        Assert.Null(error);
        Assert.True(probe.FoundCard, "O card do grupo não expôs ContextMenu na árvore visual.");
        Assert.True(probe.MenuOpened);
        Assert.Equal(Visibility.Visible, probe.ItemVisibility);
        Assert.Contains("Excluir grupo", probe.ItemText, StringComparison.Ordinal);

        // A ponte card → HostsViewModel funcionou: é o MESMO comando, não um binding perdido.
        Assert.Same(vm.DeleteGroupCommand, probe.ItemCommand);
        Assert.False(probe.ItemEnabledBeforeTarget); // sem alvo: desabilitado
        Assert.True(probe.ItemEnabledAfterTarget);   // clique-direito fixou o alvo
        Assert.Same(vm.Groups.Single(), vm.SelectedGroup);
    }

    /// <summary>
    /// Exclusão pelo menu, com a UI VIVA: o card sai da tela e a lista fica coerente. Mutar as
    /// <c>ObservableCollection</c> bindadas (o <see cref="HostsViewModel.LoadAsync"/> recria os cards)
    /// enquanto o <c>ItemsControl</c> está realizado é a superfície clássica de crash de runtime WPF.
    /// </summary>
    [Fact]
    public async Task Excluir_Pelo_Menu_Remove_O_Card_Da_Tela()
    {
        var store = new InMemoryLocalStore();
        await store.AddGroupAsync("ws-local", "Temporário");
        await store.AddGroupAsync("ws-local", "Innet");
        var vm = new HostsViewModel(store, Launcher(), "ws-local");
        vm.DeleteGroupConfirmationRequested += (_, req) => req.Confirmed = true;

        int cardsDepois = -1;
        Exception? error = StaThreadRunner.Run(() =>
        {
            var view = new HostsView { DataContext = vm };
            var window = new Window
            {
                Content = view,
                Width = 900,
                Height = 600,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                vm.LoadAsync().GetAwaiter().GetResult();
                window.UpdateLayout();

                vm.SelectedGroup = vm.Groups.Single(c => c.Name == "Temporário");
                vm.DeleteGroupAsync().GetAwaiter().GetResult();
                window.UpdateLayout();

                cardsDepois = CountCards(view);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Null(error);
        Assert.Equal(1, cardsDepois); // só "Innet" continua desenhado
        Assert.Equal("Innet", vm.Groups.Single().Name);
    }

    private static int CountCards(DependencyObject root)
    {
        int total = 0;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Border { ContextMenu: not null, DataContext: GroupCardViewModel })
            {
                total++;
            }

            total += CountCards(child);
        }

        return total;
    }
}
