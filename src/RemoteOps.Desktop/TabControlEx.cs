using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace RemoteOps.Desktop;

/// <summary>
/// TabControl que mantém vivo o conteúdo de TODAS as abas já visitadas, em vez de destruir e
/// recriar o conteúdo da aba selecionada a cada troca (comportamento padrão do WPF, que usa um
/// único ContentPresenter com ContentSource="SelectedContent").
///
/// Por que existe: a aba de sessão hospeda estado pesado e vivo — TerminalTabView com WebView2 +
/// xterm.js e uma sessão SSH em andamento. Com o TabControl padrão, trocar para "Hosts" e voltar
/// DESTRUÍA a TerminalTabView e criava uma nova, vazia (o xterm recriado só mostra o que chega
/// DEPOIS): o terminal voltava "preto"/sem histórico e a sessão parecia fechada. Aqui cada item
/// ganha o seu próprio ContentPresenter, criado sob demanda (na 1ª vez que a aba é selecionada) e
/// mantido no visual tree; a troca de aba só alterna a Visibility. Fechar a aba (item removido da
/// coleção) remove o presenter e aí sim a View é descarregada (WebView2 limpo em OnUnloaded).
///
/// Requer um template com um <see cref="Panel"/> chamado "PART_ItemsHolder" no lugar do
/// ContentPresenter padrão (ver MainWindow.xaml). Mesmo padrão do "TabControlEx" clássico de WPF.
/// </summary>
public class TabControlEx : TabControl
{
    private Panel? _itemsHolder;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _itemsHolder = GetTemplateChild("PART_ItemsHolder") as Panel;
        UpdateVisibleContent();
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);

        if (_itemsHolder is null)
        {
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _itemsHolder.Children.Clear();
        }
        else if (e.OldItems is not null)
        {
            foreach (object removed in e.OldItems)
            {
                if (FindPresenter(removed) is { } cp)
                {
                    _itemsHolder.Children.Remove(cp);
                }
            }
        }

        UpdateVisibleContent();
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        UpdateVisibleContent();
    }

    // Cria (uma vez) o presenter da aba selecionada e deixa só ele visível; os demais ficam
    // Collapsed, porém VIVOS no visual tree — o WebView2/xterm de cada terminal continua intacto.
    private void UpdateVisibleContent()
    {
        if (_itemsHolder is null)
        {
            return;
        }

        object? selected = SelectedItem;
        if (selected is not null && FindPresenter(selected) is null)
        {
            _itemsHolder.Children.Add(new ContentPresenter { Content = selected });
        }

        foreach (ContentPresenter cp in _itemsHolder.Children)
        {
            cp.Visibility = Equals(cp.Content, selected) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private ContentPresenter? FindPresenter(object item)
    {
        if (_itemsHolder is null)
        {
            return null;
        }

        foreach (ContentPresenter cp in _itemsHolder.Children)
        {
            if (Equals(cp.Content, item))
            {
                return cp;
            }
        }

        return null;
    }
}
