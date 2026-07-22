using System;
using System.Threading.Tasks;
using RemoteOps.Desktop.Account;
using RemoteOps.Desktop.Changelog;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Reporting;
using RemoteOps.Desktop.Update;
using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.ViewModels;

public sealed class WorkspaceViewModel : BaseViewModel
{
    /// <summary>Workspace único local (Fase 1 — sem multi-workspace na UI ainda).</summary>
    public const string WorkspaceId = "ws-local";

    private readonly ISettingsStore? _settingsStore;
    private readonly IUpdateService? _updateService;
    private readonly IChangelogSource? _changelogSource;
    private readonly IBugReportComposer? _bugReportComposer;
    private string _syncStatus = "Offline";

    public WorkspaceViewModel(
        BrowserViewModel browser,
        TabsViewModel tabs,
        ISettingsStore? settingsStore = null,
        IUpdateService? updateService = null,
        IChangelogSource? changelogSource = null,
        IBugReportComposer? bugReportComposer = null)
    {
        Browser = browser;
        Tabs = tabs;
        _settingsStore = settingsStore;
        _updateService = updateService;
        _changelogSource = changelogSource;
        _bugReportComposer = bugReportComposer;
    }

    /// <summary>
    /// Fábrica LAZY do cliente autenticado de 2FA. O App a atribui DEPOIS de ativar a conta (quando há
    /// tokens); fica null em modo local. É lazy porque este VM é um singleton do DI, resolvido no
    /// startup ANTES de a conta existir — ler a fábrica só ao abrir Configurações evita a ordem.
    /// </summary>
    public Func<IMfaApi?>? MfaApiFactory { get; set; }

    /// <summary>
    /// Fábrica LAZY do serviço de "Reenviar tudo para a nuvem". Lazy pelo MESMO motivo da
    /// <see cref="MfaApiFactory"/>: o serviço precisa do orquestrador da sessão de sync, que só sobe
    /// no fim do startup (ou depois, quando a conta é ativada) — e este VM é singleton do DI,
    /// resolvido antes disso. Null (ou fábrica que devolve null) = modo local: a seção some das
    /// Configurações, porque não há para onde reenviar.
    /// </summary>
    public Func<CloudResyncService?>? CloudResyncFactory { get; set; }

    /// <summary>
    /// Fábrica LAZY do convite de time (Fatia 1). Lazy pelo MESMO motivo das duas acima: precisa dos
    /// tokens da conta E do chaveiro de time, que só existem depois da ativação. Null (ou fábrica que
    /// devolve null) = modo local, e a seção de Equipe some das Configurações.
    /// </summary>
    public Func<TeamInviteContext?>? TeamInviteFactory { get; set; }

    public BrowserViewModel Browser { get; }
    public TabsViewModel Tabs { get; }

    /// <summary>
    /// Status do cloud sync (ADR-013), atualizado por App.OnSyncStatusChanged. A Fase 1 do
    /// shell Termius ainda não tem um indicador visual para isto (o antigo topo com o ponto
    /// de status saiu com o DockPanel/Menu); a propriedade fica pronta para a próxima tarefa
    /// de UI que expuser sync na navegação por abas.
    /// </summary>
    public string SyncStatus
    {
        get => _syncStatus;
        set => Set(ref _syncStatus, value);
    }

    public Task InitializeAsync() => Browser.Hosts.LoadAsync();

    public SettingsViewModel CreateSettingsViewModel()
    {
        ISettingsStore store = _settingsStore ?? new JsonSettingsStore();
        ChangelogViewModel? changelog = _changelogSource is null ? null : new ChangelogViewModel(_changelogSource, store);
        BugReportViewModel? bugReport = _bugReportComposer is null ? null : new BugReportViewModel(_bugReportComposer);
        IMfaApi? mfaApi = MfaApiFactory?.Invoke();
        CloudResyncService? resync = CloudResyncFactory?.Invoke();
        TeamInviteContext? team = TeamInviteFactory?.Invoke();
        return new SettingsViewModel(store, _updateService, changelog, bugReport, mfaApi, resync, team);
    }

    public string AppVersionText =>
        $"RemoteOps Desktop {typeof(WorkspaceViewModel).Assembly.GetName().Version?.ToString(3) ?? "?"}";

    // A verificação de atualização vive agora no UpdateNotificationViewModel, que além de checar
    // mantém o estado do indicador da barra de status e o carimbo da última checagem boa. Manter aqui
    // um segundo caminho de checagem (o antigo CheckForUpdatesQuietAsync) só criaria duas fontes de
    // verdade divergindo com o tempo — a aplicação, essa sim, continua sendo daqui.

    /// <summary>Baixa e aplica (Velopack reinicia o app em sucesso); false em falha.</summary>
    public async Task<bool> TryApplyUpdateAsync(UpdateCheckResult update)
    {
        if (_updateService is null)
        {
            return false;
        }

        try
        {
            await _updateService.ApplyUpdateAsync(update);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
