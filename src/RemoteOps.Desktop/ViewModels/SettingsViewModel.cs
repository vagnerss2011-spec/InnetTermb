using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Update;

namespace RemoteOps.Desktop.ViewModels;

public sealed class SettingsViewModel : BaseViewModel
{
    private readonly ISettingsStore _store;
    private readonly IUpdateService? _updateService;
    private AppSettings _settings;
    private bool _rdpEnabled;
    private bool _ndeskEnabled;
    private string _updateStatus = string.Empty;

    public SettingsViewModel(ISettingsStore store, IUpdateService? updateService = null)
    {
        _store = store;
        _updateService = updateService;
        _settings = store.Load();
        _rdpEnabled = _settings.Flags.TryGetValue(FeatureFlagNames.RdpEnabled, out bool rdp) && rdp;
        _ndeskEnabled = _settings.Flags.TryGetValue(FeatureFlagNames.NdeskEnabled, out bool nd) && nd;

        SaveCommand = new RelayCommand(Save);
        CheckForUpdatesCommand = new RelayCommand(
            () => _ = CheckForUpdatesAsync(),
            () => _updateService != null);
    }

    public bool RdpEnabled { get => _rdpEnabled; set => Set(ref _rdpEnabled, value); }
    public bool NdeskEnabled { get => _ndeskEnabled; set => Set(ref _ndeskEnabled, value); }
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

    /// <summary>Disparado após persistir; a janela fecha e avisa "requer reinício" se necessário.</summary>
    public event EventHandler? Saved;

    private void Save()
    {
        var flags = new Dictionary<string, bool>(_settings.Flags, StringComparer.OrdinalIgnoreCase)
        {
            [FeatureFlagNames.RdpEnabled] = RdpEnabled,
            [FeatureFlagNames.NdeskEnabled] = NdeskEnabled,
        };
        _settings = _settings with { Flags = flags };
        _store.Save(_settings);
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateService is null)
        {
            return;
        }

        UpdateStatus = "Verificando…";
        try
        {
            UpdateCheckResult result = await _updateService.CheckForUpdatesAsync();
            UpdateStatus = result.UpdateAvailable
                ? $"Atualização disponível: {result.AvailableVersion}."
                : "Você está na versão mais recente.";
        }
        catch (Exception)
        {
            UpdateStatus = "Não foi possível verificar atualizações agora.";
        }
    }
}
