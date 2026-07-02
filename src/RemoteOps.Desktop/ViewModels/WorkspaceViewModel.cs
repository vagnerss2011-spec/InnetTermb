using System.Threading.Tasks;
using RemoteOps.Desktop.Changelog;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Reporting;
using RemoteOps.Desktop.Update;

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
        return new SettingsViewModel(store, _updateService, changelog, bugReport);
    }

    public string AppVersionText =>
        $"RemoteOps Desktop {typeof(WorkspaceViewModel).Assembly.GetName().Version?.ToString(3) ?? "?"}";
}
