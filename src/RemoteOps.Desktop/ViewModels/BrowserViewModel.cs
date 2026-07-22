using System;
using System.Linq;
using RemoteOps.Desktop.Changelog;
using RemoteOps.Desktop.Infrastructure;

namespace RemoteOps.Desktop.ViewModels;

public enum BrowserSection { Hosts, Keychain, Logs }

public sealed class BrowserViewModel : BaseViewModel
{
    private BrowserSection _activeSection = BrowserSection.Hosts;
    private readonly IChangelogSource? _changelogSource;
    private readonly ISettingsStore? _settingsStore;

    public BrowserViewModel(
        HostsViewModel hosts,
        KeychainViewModel keychain,
        LogsViewModel logs,
        IChangelogSource? changelogSource = null,
        ISettingsStore? settingsStore = null,
        SyncStatusViewModel? sync = null,
        UpdateNotificationViewModel? update = null)
    {
        Hosts = hosts;
        Keychain = keychain;
        Logs = logs;
        _changelogSource = changelogSource;
        _settingsStore = settingsStore;
        Sync = sync ?? new SyncStatusViewModel();
        Update = update ?? new UpdateNotificationViewModel(updateService: null);
        ShowHostsCommand = new RelayCommand(() => ActiveSection = BrowserSection.Hosts);
        ShowKeychainCommand = new RelayCommand(() => { ActiveSection = BrowserSection.Keychain; _ = keychain.LoadAsync(); });
        ShowLogsCommand = new RelayCommand(() => ActiveSection = BrowserSection.Logs);
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        CheckUpdatesCommand = new RelayCommand(() => UpdatesRequested?.Invoke(this, EventArgs.Empty));
        AboutCommand = new RelayCommand(() => AboutRequested?.Invoke(this, EventArgs.Empty));
    }

    public HostsViewModel Hosts { get; }
    public KeychainViewModel Keychain { get; }
    public LogsViewModel Logs { get; }

    /// <summary>Estado do cloud sync + "Sincronizar agora" no shell (Fase 2, item B). Nunca null: sem
    /// nuvem, é um <see cref="SyncStatusViewModel"/> desabilitado que só mostra "Offline".</summary>
    public SyncStatusViewModel Sync { get; }

    /// <summary>Aviso discreto de versão nova na barra de status. Nunca null: sem serviço de update
    /// (build fora do pacote instalado), é uma VM inerte que nunca acende.</summary>
    public UpdateNotificationViewModel Update { get; }

    /// <summary>
    /// Em qual cofre o operador está (Fatia 1e). Nunca null: sem conta na nuvem é o
    /// <see cref="VaultScope.LocalOnly"/>, que continua sendo a verdade — o cofre é pessoal e nunca
    /// sai deste PC.
    ///
    /// <para>Mora no <see cref="BrowserViewModel"/> porque o shell é o único lugar visível o TEMPO
    /// TODO: o erro caro desta fatia é cadastrar o equipamento de um cliente achando que está no
    /// cofre do time, e um aviso que só aparece na tela de Equipe chega tarde demais.</para>
    /// </summary>
    public VaultBadgeViewModel Vault { get; } = new();

    /// <summary>
    /// O que ficou parado na fila do cofre que esta sessão NÃO abriu (um banco por escopo, 1j).
    /// Nunca null: sem outro cofre na máquina ele fica calado, que é a verdade para quem tem um só.
    ///
    /// <para>Mora ao lado do <see cref="Vault"/>, e pelo mesmo motivo: a barra do shell é o único
    /// lugar visível o tempo todo. Um aviso de "isto não subiu" que só aparecesse ao abrir a tela de
    /// sincronização chegaria depois de o operador já ter concluído que estava tudo certo.</para>
    /// </summary>
    public OtherVaultOutboxViewModel OtherVaultOutbox { get; } = new();

    public BrowserSection ActiveSection
    {
        get => _activeSection;
        private set
        {
            Set(ref _activeSection, value);
            RaisePropertyChanged(nameof(IsHosts));
            RaisePropertyChanged(nameof(IsKeychain));
            RaisePropertyChanged(nameof(IsLogs));
        }
    }

    public bool IsHosts => _activeSection == BrowserSection.Hosts;
    public bool IsKeychain => _activeSection == BrowserSection.Keychain;
    public bool IsLogs => _activeSection == BrowserSection.Logs;

    /// <summary>Há versão de changelog não vista? (pontinho no avatar).</summary>
    public bool HasUnreadChangelog
    {
        get
        {
            if (_changelogSource is null || _settingsStore is null)
            {
                return false;
            }

            string? latest = ChangelogVersioning.Latest(_changelogSource.Load().Select(e => e.Version));
            return latest is not null && ChangelogVersioning.IsNewer(latest, _settingsStore.Load().LastSeenChangelogVersion);
        }
    }

    /// <summary>Reavalia o badge (chamar quando o modal de Configurações fecha).</summary>
    public void RefreshChangelogBadge() => RaisePropertyChanged(nameof(HasUnreadChangelog));

    public RelayCommand ShowHostsCommand { get; }
    public RelayCommand ShowKeychainCommand { get; }
    public RelayCommand ShowLogsCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand AboutCommand { get; }

    public event EventHandler? SettingsRequested;
    public event EventHandler? UpdatesRequested;
    public event EventHandler? AboutRequested;
}
