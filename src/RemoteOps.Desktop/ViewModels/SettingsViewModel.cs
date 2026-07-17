using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Update;
using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.ViewModels;

public sealed class SettingsViewModel : BaseViewModel
{
    private readonly ISettingsStore _store;
    private readonly IUpdateService? _updateService;
    private readonly IMfaApi? _mfaApi;
    private AppSettings _settings;
    private bool _rdpEnabled;
    private bool _ndeskEnabled;
    private string? _winBoxExePath;
    private string? _winBoxSha256;
    private string _updateStatus = string.Empty;
    private UpdateCheckResult? _lastCheck;

    public SettingsViewModel(
        ISettingsStore store,
        IUpdateService? updateService = null,
        ChangelogViewModel? changelog = null,
        BugReportViewModel? bugReport = null,
        IMfaApi? mfaApi = null)
    {
        _store = store;
        _updateService = updateService;
        _mfaApi = mfaApi;
        Changelog = changelog;
        BugReport = bugReport;
        _settings = store.Load();
        _rdpEnabled = _settings.Flags.TryGetValue(FeatureFlagNames.RdpEnabled, out bool rdp) && rdp;
        _ndeskEnabled = _settings.Flags.TryGetValue(FeatureFlagNames.NdeskEnabled, out bool nd) && nd;
        _winBoxExePath = _settings.WinBoxExePath;
        _winBoxSha256 = _settings.WinBoxSha256;

        SaveCommand = new RelayCommand(Save);
        CheckForUpdatesCommand = new RelayCommand(
            () => _ = CheckForUpdatesAsync(),
            () => _updateService != null);
        ApplyUpdateCommand = new RelayCommand(
            () => _ = ApplyUpdateAsync(),
            () => _updateService != null && UpdateAvailable);
        // Só habilita "verificação em duas etapas" quando há conta na nuvem ativa (IMfaApi injetado).
        // Sem conta (modo local puro), o botão fica oculto/desabilitado.
        ManageMfaCommand = new RelayCommand(
            () => MfaSetupRequested?.Invoke(this, EventArgs.Empty),
            () => _mfaApi != null);
    }

    /// <summary>True após um check que encontrou versão nova (habilita "Baixar e instalar").</summary>
    public bool UpdateAvailable => _lastCheck?.UpdateAvailable == true;

    public bool RdpEnabled { get => _rdpEnabled; set => Set(ref _rdpEnabled, value); }
    public bool NdeskEnabled { get => _ndeskEnabled; set => Set(ref _ndeskEnabled, value); }

    /// <summary>Caminho do executável do WinBox escolhido pela GUI (Ferramentas externas).</summary>
    public string? WinBoxExePath { get => _winBoxExePath; set => Set(ref _winBoxExePath, value); }

    /// <summary>SHA-256 fixado do WinBox (validado no launch; fail-closed se divergir).</summary>
    public string? WinBoxSha256 { get => _winBoxSha256; set => Set(ref _winBoxSha256, value); }

    /// <summary>True quando há um WinBox configurado (habilita "Re-fixar hash").</summary>
    public bool HasWinBox => !string.IsNullOrWhiteSpace(_winBoxExePath);

    /// <summary>Aba "Novidades" (pode ser null em testes que não injetam os filhos).</summary>
    public ChangelogViewModel? Changelog { get; }

    /// <summary>Aba "Reportar problema" (pode ser null em testes que não injetam os filhos).</summary>
    public BugReportViewModel? BugReport { get; }

    public string ThemeName => "Slate Signal (escuro)";
    public string VersionText =>
        $"Versão {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?"}";
    public bool CanCheckUpdates => _updateService != null;

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => Set(ref _updateStatus, value);
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand CheckForUpdatesCommand { get; }
    public RelayCommand ApplyUpdateCommand { get; }
    public RelayCommand ManageMfaCommand { get; }

    /// <summary>True quando há conta na nuvem ativa: habilita a seção de verificação em duas etapas.</summary>
    public bool CanManageMfa => _mfaApi != null;

    /// <summary>Cliente autenticado de 2FA — a janela de setup o usa pra montar seu VM. Null sem conta.</summary>
    public IMfaApi? MfaApi => _mfaApi;

    /// <summary>Pedido pra abrir a janela de 2FA (o code-behind das Configurações a abre).</summary>
    public event EventHandler? MfaSetupRequested;

    /// <summary>Disparado após persistir; a janela fecha e avisa "requer reinício" se necessário.</summary>
    public event EventHandler? Saved;

    /// <summary>Fixa o WinBox escolhido (caminho + hash calculado). A UI zera nada — dados não sensíveis.</summary>
    public void SetWinBox(string path, string sha256)
    {
        WinBoxExePath = path;
        WinBoxSha256 = sha256;
        RaisePropertyChanged(nameof(HasWinBox));
    }

    private void Save()
    {
        var flags = new Dictionary<string, bool>(_settings.Flags, StringComparer.OrdinalIgnoreCase)
        {
            [FeatureFlagNames.RdpEnabled] = RdpEnabled,
            [FeatureFlagNames.NdeskEnabled] = NdeskEnabled,
        };
        _settings = _settings with
        {
            Flags = flags,
            WinBoxExePath = WinBoxExePath,
            WinBoxSha256 = WinBoxSha256,
        };
        _store.Save(_settings);
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Verifica o feed e habilita "Baixar e instalar" quando há versão nova.</summary>
    public async Task CheckForUpdatesAsync()
    {
        if (_updateService is null)
        {
            return;
        }

        UpdateStatus = "Verificando…";
        try
        {
            UpdateCheckResult result = await _updateService.CheckForUpdatesAsync();
            _lastCheck = result;
            UpdateStatus = result.UpdateAvailable
                ? $"Atualização disponível: {result.AvailableVersion}. Clique em \"Baixar e instalar\"."
                : "Você está na versão mais recente.";
        }
        catch (Exception)
        {
            _lastCheck = null;
            UpdateStatus = "Não foi possível verificar atualizações agora.";
        }

        RaisePropertyChanged(nameof(UpdateAvailable));
        ApplyUpdateCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Baixa e aplica a atualização verificada. Em sucesso o Velopack REINICIA o app
    /// sozinho — este método só "retorna" em falha. Antes desta mudança não existia
    /// nenhum caminho na GUI que chamasse ApplyUpdateAsync (o operador via
    /// "atualização disponível" e tinha que baixar o Setup.exe na mão).
    /// </summary>
    public async Task ApplyUpdateAsync()
    {
        if (_updateService is null || _lastCheck is not { UpdateAvailable: true })
        {
            return;
        }

        UpdateStatus = "Baixando atualização… o RemoteOps reinicia sozinho ao concluir.";
        try
        {
            await _updateService.ApplyUpdateAsync(_lastCheck);
        }
        catch (Exception)
        {
            UpdateStatus = "Não foi possível baixar/aplicar a atualização agora. Verifique a conexão e tente novamente.";
        }
    }
}
