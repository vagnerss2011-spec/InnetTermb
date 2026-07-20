using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RemoteOps.Desktop.Credentials;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Update;
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
    private readonly IInlineCredentialService _inlineCreds;

    // TabsHost.ItemsSource: item 0 é o próprio WorkspaceViewModel (aba fixa "Hosts"), os
    // demais espelham Tabs.Tabs (uma entrada por sessão). Montado em código porque WPF não
    // aceita um Binding declarativo como filho direto de CompositeCollection ("Binding só
    // pode ser definido em uma DependencyProperty de um DependencyObject" — falha em
    // runtime, não em tempo de compilação XAML).
    private readonly ObservableCollection<object> _tabItems = [];

    public MainWindow(WorkspaceViewModel viewModel, ILocalStore store, IInlineCredentialService inlineCreds)
    {
        InitializeComponent();
        DataContext = viewModel;
        _store = store;
        _inlineCreds = inlineCreds;

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

        // O clique no indicador da barra é o ÚNICO caminho para o diálogo de atualização.
        viewModel.Browser.Update.ApplyRequested += async (_, check) => await ConfirmAndApplyUpdateAsync(check);

        // Clique no aviso "N alterações não subiram" → lista do que se perdeu.
        viewModel.Browser.Sync.ConflictsRequested += (_, _) =>
            new SyncConflictsWindow(viewModel.Browser.Sync) { Owner = this }.ShowDialog();

        Loaded += async (_, _) =>
        {
            // O watch é armado ANTES dos awaits, de propósito. Se ficasse depois, bastava o
            // InitializeAsync lançar (o LoadAsync do store não tem try/catch) ou a primeira checagem
            // pendurar numa rede com captive-portal — cenário já documentado em App.OnStartup, que por
            // isso põe teto na checagem — para a lambda morrer antes de armar o timer. O app abriria
            // normalmente e a verificação periódica simplesmente NUNCA existiria naquela sessão, sem
            // nenhum sinal: a feature morta em silêncio, que é o defeito recorrente deste app.
            // Não há corrida: o primeiro tick só ocorre daqui a 3h.
            StartUpdateWatch();

            await viewModel.InitializeAsync();
            await viewModel.Browser.Update.CheckAsync();
        };

        // Parar o timer no fechamento não é higiene opcional: recurso que não solta pendura o processo,
        // e processo pendurado segura o mutex de instância única — o app deixa de abrir até o operador
        // reiniciar o Windows (foi o que aconteceu na v1.4.0; ver CHANGELOG 1.4.1).
        Closed += (_, _) => _updateWatch?.Stop();

        viewModel.Browser.SettingsRequested += (_, _) => OpenSettings();
        // "Verificar atualizações" abre já na aba Atualização (onde estão "Verificar agora" / "Baixar
        // e instalar") — antes caía na aba padrão "Aparência", igual a "Configurações", enganando o rótulo.
        viewModel.Browser.UpdatesRequested += (_, _) => OpenSettings(initialTab: "Atualização");
        viewModel.Browser.AboutRequested += (_, _) =>
            MessageBox.Show(this, viewModel.AppVersionText, "Sobre o RemoteOps", MessageBoxButton.OK, MessageBoxImage.Information);

        viewModel.Browser.Hosts.NewHostRequested += (_, groupId) => OpenHostEditor(existing: null, groupId);
        viewModel.Browser.Hosts.EditHostRequested += (_, asset) => OpenHostEditor(existing: asset.Asset, asset.Asset.GroupId);
        viewModel.Browser.Hosts.NewGroupRequested += (_, _) => OpenNewGroupDialog();
        viewModel.Browser.Hosts.LaunchFailed += (_, message) => Dispatcher.Invoke(() =>
            MessageBox.Show(this, message, "Conectar", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    private WorkspaceViewModel Vm => (WorkspaceViewModel)DataContext;

    // Re-verificação periódica enquanto o app está aberto. Antes a checagem só rodava no Loaded — e
    // como este é um console de operação que fica aberto o dia inteiro, uma versão publicada durante o
    // expediente NUNCA era anunciada. Intervalo é opção de código (YAGNI: não vai à tela).
    //
    // 30min, não 3h: com 3h o operador publicava uma versão e ficava metade do expediente sem o aviso
    // aparecer, o que na prática é indistinguível de "não funciona" — foi o que ele reportou. O custo
    // de checar é um GET no feed público; 48 checagens/dia por máquina é irrelevante perto de ficar
    // sem saber que existe correção disponível.
    private static readonly TimeSpan UpdateWatchInterval = TimeSpan.FromMinutes(30);
    private DispatcherTimer? _updateWatch;

    private void StartUpdateWatch()
    {
        // DispatcherTimer (e não Timer de fundo): o tick escreve em propriedades ligadas a binding,
        // então já nasce na UI thread e dispensa marshalling.
        _updateWatch = new DispatcherTimer { Interval = UpdateWatchInterval };
        _updateWatch.Tick += async (_, _) => await Vm.Browser.Update.CheckAsync();
        _updateWatch.Start();
    }

    /// <summary>
    /// Confirma e aplica a atualização — disparado SÓ pelo clique do operador no indicador da barra de
    /// status (<c>UpdateNotificationViewModel.ApplyRequested</c>).
    ///
    /// <para>Antes isto era um diálogo automático no <c>Loaded</c>. Virou clique por um motivo de
    /// segurança operacional: um modal que rouba foco num console de rede pode aparecer enquanto o
    /// operador digita num equipamento em produção, e o <c>Enter</c> destinado ao roteador confirmaria
    /// "atualizar agora", reiniciando o app no meio de uma manutenção.</para>
    /// </summary>
    private async Task ConfirmAndApplyUpdateAsync(UpdateCheckResult check)
    {
        if (check.AvailableVersion is not { } available)
        {
            return;
        }

        // Pausa a verificação periódica enquanto o diálogo está aberto e a aplicação em voo.
        // Motivo concreto: o VelopackUpdateService aplica o ÚLTIMO resultado de checagem que ele viu
        // (estado interno compartilhado), NÃO o objeto passado por parâmetro. Como o DispatcherTimer
        // dispara durante o loop de mensagens de um modal, um tick podia trocar esse estado enquanto o
        // operador lia o diálogo — e ele acabaria instalando uma versão diferente da que confirmou, ou
        // (se a checagem nova não achasse pacote) autorizando algo que silenciosamente não aconteceria.
        _updateWatch?.Stop();
        try
        {
            var answer = MessageBox.Show(
                this,
                $"Nova versão {available} disponível (você está na {check.CurrentVersion}).\n\n" +
                "Baixar e instalar agora? O RemoteOps reinicia sozinho ao concluir.",
                "Atualização disponível",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes && !await Vm.TryApplyUpdateAsync(check))
            {
                MessageBox.Show(
                    this,
                    "Não foi possível baixar/aplicar a atualização agora. Tente mais tarde em Configurações → Atualização.",
                    "Atualização",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            // Em caso de sucesso o Velopack reinicia o app e isto não chega a importar; no "Não" e em
            // qualquer falha, a verificação periódica precisa voltar.
            _updateWatch?.Start();
        }
    }

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

    private void OpenSettings(string? initialTab = null)
    {
        var window = new SettingsWindow(Vm.CreateSettingsViewModel(), initialTab) { Owner = this };
        window.ShowDialog();
        Vm.Browser.RefreshChangelogBadge();
    }

    private void OpenHostEditor(Contracts.Assets.Asset? existing, string? groupId)
    {
        var editorVm = new HostEditorViewModel(_store, WorkspaceViewModel.WorkspaceId, existing, groupId, _inlineCreds);
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
