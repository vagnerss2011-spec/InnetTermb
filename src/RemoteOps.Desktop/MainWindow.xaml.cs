using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;

namespace RemoteOps.Desktop;

/// <summary>
/// Shell Termius (Fase 1): TabControl com a aba fixa "Hosts" (BrowserView) + uma aba por
/// sessão aberta (Tabs.Tabs). Substitui o antigo DockPanel+Menu+Grid (Sidebar/HostList/
/// Inspector/MainViewModel, removidos na Task 13).
/// </summary>
public partial class MainWindow : Window
{
    private readonly ILocalStore _store;

    // TabsHost.ItemsSource: item 0 é o próprio WorkspaceViewModel (aba fixa "Hosts"), os
    // demais espelham Tabs.Tabs (uma entrada por sessão). Montado em código porque WPF não
    // aceita um Binding declarativo como filho direto de CompositeCollection ("Binding só
    // pode ser definido em uma DependencyProperty de um DependencyObject" — falha em
    // runtime, não em tempo de compilação XAML).
    private readonly ObservableCollection<object> _tabItems = [];

    public MainWindow(WorkspaceViewModel viewModel, ILocalStore store)
    {
        InitializeComponent();
        DataContext = viewModel;
        _store = store;

        _tabItems.Add(viewModel);
        foreach (var tab in viewModel.Tabs.Tabs)
        {
            _tabItems.Add(tab);
        }
        viewModel.Tabs.Tabs.CollectionChanged += TabsOnCollectionChanged;
        // TabsViewModel.OpenTab/OpenTerminalTab/OpenRdpTab/OpenNdeskTab fazem Tabs.Add(tab)
        // ANTES de ActiveTab = tab, então o CollectionChanged acima roda com ActiveTab ainda
        // no valor antigo — a seleção da aba nova precisa ouvir ActiveTab diretamente.
        viewModel.Tabs.PropertyChanged += TabsOnPropertyChanged;
        TabsHost.ItemsSource = _tabItems;
        TabsHost.SelectionChanged += TabsHost_SelectionChanged;

        Loaded += async (_, _) => await viewModel.InitializeAsync();

        viewModel.Browser.SettingsRequested += (_, _) => OpenSettings();
        viewModel.Browser.UpdatesRequested += (_, _) => OpenSettings();
        viewModel.Browser.AboutRequested += (_, _) =>
            MessageBox.Show(this, viewModel.AppVersionText, "Sobre o RemoteOps", MessageBoxButton.OK, MessageBoxImage.Information);

        viewModel.Browser.Hosts.NewHostRequested += (_, groupId) => OpenHostEditor(existing: null, groupId);
        viewModel.Browser.Hosts.EditHostRequested += (_, asset) => OpenHostEditor(existing: asset.Asset, asset.Asset.GroupId);
        viewModel.Browser.Hosts.NewGroupRequested += (_, _) => OpenNewGroupDialog();
        viewModel.Browser.Hosts.LaunchFailed += (_, message) => Dispatcher.Invoke(() =>
            MessageBox.Show(this, message, "Conectar", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    private WorkspaceViewModel Vm => (WorkspaceViewModel)DataContext;

    // Espelha Tabs.Tabs (abrir/fechar sessão) em _tabItems, mantendo o item 0 (Hosts) fixo.
    private void TabsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems != null:
                int insertAt = e.NewStartingIndex + 1; // +1: item 0 é a aba Hosts
                for (int i = 0; i < e.NewItems.Count; i++)
                {
                    _tabItems.Insert(insertAt + i, e.NewItems[i]!);
                }
                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems != null:
                foreach (var removed in e.OldItems)
                {
                    _tabItems.Remove(removed!);
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                while (_tabItems.Count > 1)
                {
                    _tabItems.RemoveAt(_tabItems.Count - 1);
                }
                break;
        }
    }

    // Abrir uma sessão (SessionLauncher) faz Tabs.Add(tab) e só depois ActiveTab = tab, então
    // a seleção visual da aba nova precisa reagir à mudança de ActiveTab em si — o
    // CollectionChanged acima roda cedo demais para isso (ActiveTab ainda é o valor antigo).
    private void TabsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabsViewModel.ActiveTab) && Vm.Tabs.ActiveTab is { } active)
        {
            TabsHost.SelectedItem = active;
        }
    }

    // Selecionar uma aba de sessão pelo TabControl (clique do usuário) também deve atualizar
    // Tabs.ActiveTab, para Ctrl+W (Tabs.CloseActiveTabCommand) fechar a aba certa.
    private void TabsHost_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabsHost.SelectedItem is SessionTabViewModel selected && !ReferenceEquals(Vm.Tabs.ActiveTab, selected))
        {
            Vm.Tabs.ActiveTab = selected;
        }
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(Vm.CreateSettingsViewModel()) { Owner = this };
        window.ShowDialog();
        Vm.Browser.RefreshChangelogBadge();
    }

    private void OpenHostEditor(Contracts.Assets.Asset? existing, string? groupId)
    {
        var editorVm = new HostEditorViewModel(_store, WorkspaceViewModel.WorkspaceId, existing, groupId);
        var dialog = new HostEditorDialog(editorVm) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _ = Vm.Browser.Hosts.ReloadAfterEditAsync();
        }
    }

    private void OpenNewGroupDialog()
    {
        var groupVm = new NewGroupViewModel(_store, WorkspaceViewModel.WorkspaceId, parentGroupId: null);
        var dialog = new NewGroupDialog(groupVm) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _ = Vm.Browser.Hosts.ReloadAfterEditAsync();
        }
    }
}

/// <summary>
/// Seleciona o cabeçalho de aba pelo tipo do item: <see cref="WorkspaceViewModel"/> (aba fixa
/// "Hosts", sem botão fechar) ou <see cref="SessionTabViewModel"/> (abas de sessão, com botão
/// fechar exceto quando pinada — ver TabsView.xaml equivalente).
/// </summary>
public sealed class MainTabHeaderTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HostsTemplate { get; set; }
    public DataTemplate? SessionTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) => item switch
    {
        WorkspaceViewModel => HostsTemplate,
        SessionTabViewModel => SessionTemplate,
        _ => base.SelectTemplate(item, container),
    };
}
