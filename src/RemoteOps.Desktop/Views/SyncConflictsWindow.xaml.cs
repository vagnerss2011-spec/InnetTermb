using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

/// <summary>
/// Lista o que NÃO subiu para a nuvem e permite dispensar o aviso.
///
/// <para>Por que esta janela existe: o app registrava conflitos desde sempre e mostrava a contagem na
/// barra ("Sincronizado (18 conflito(s))"), mas nunca houve como VER o que eram nem como limpar — a
/// tabela não era apagada por nada, então o número só crescia e incluía cicatrizes de bugs já
/// corrigidos. Em campo isso apareceu como "18 conflitos numa máquina e 0 na outra", sem ação possível.</para>
///
/// <para>Não há "resolver" de verdade a oferecer: a alteração local já foi descartada quando o
/// servidor rejeitou o envio (política <i>record &amp; advance</i>) e a versão da nuvem já sobrescreveu
/// a local. Oferecer "manter a minha" seria mentira. O que a janela faz de honesto é EXPLICAR o que se
/// perdeu, para o operador refazer se ainda importar, e então dispensar o aviso.</para>
/// </summary>
public partial class SyncConflictsWindow : Window
{
    private readonly SyncStatusViewModel _sync;

    public SyncConflictsWindow(SyncStatusViewModel sync)
    {
        InitializeComponent();
        _sync = sync;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        IReadOnlyList<SyncConflictItem> items = await _sync.LoadConflictsAsync();
        ConflictList.ItemsSource = items;

        bool vazio = items.Count == 0;
        EmptyLabel.Visibility = vazio ? Visibility.Visible : Visibility.Collapsed;
        DismissButton.IsEnabled = !vazio;
    }

    private async void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        DismissButton.IsEnabled = false;
        await _sync.DismissConflictsAsync();
        await ReloadAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
