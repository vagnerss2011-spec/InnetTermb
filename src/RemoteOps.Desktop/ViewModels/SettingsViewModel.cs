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
    private readonly CloudResyncService? _resync;
    private AppSettings _settings;
    private bool _rdpEnabled;
    private bool _ndeskEnabled;
    private string? _winBoxExePath;
    private string? _winBoxSha256;
    private bool _cloudSyncEnabled;
    private string _cloudServerUrl = string.Empty;
    private string _cloudConfigError = string.Empty;
    private string _updateStatus = string.Empty;
    private UpdateCheckResult? _lastCheck;

    // ── Reenviar tudo para a nuvem ───────────────────────────────────────────────────────────
    private bool _isResyncConfirmVisible;
    private bool _isResyncing;
    private string _resyncStatus = string.Empty;

    // Geração do reenvio. O IProgress entrega de forma assíncrona (posta no contexto de UI), então
    // um relatório em voo pode chegar DEPOIS do texto final e sobrescrever o resultado com um
    // "Reenviando… 700 de 700" eterno. Comparar a geração descarta os atrasados.
    private int _resyncRun;

    public SettingsViewModel(
        ISettingsStore store,
        IUpdateService? updateService = null,
        ChangelogViewModel? changelog = null,
        BugReportViewModel? bugReport = null,
        IMfaApi? mfaApi = null,
        CloudResyncService? resync = null)
    {
        _store = store;
        _updateService = updateService;
        _mfaApi = mfaApi;
        _resync = resync;
        Changelog = changelog;
        BugReport = bugReport;
        _settings = store.Load();
        _rdpEnabled = _settings.Flags.TryGetValue(FeatureFlagNames.RdpEnabled, out bool rdp) && rdp;
        _ndeskEnabled = _settings.Flags.TryGetValue(FeatureFlagNames.NdeskEnabled, out bool nd) && nd;
        _winBoxExePath = _settings.WinBoxExePath;
        _winBoxSha256 = _settings.WinBoxSha256;
        _cloudSyncEnabled = _settings.CloudSyncEnabled;
        _cloudServerUrl = _settings.CloudServerUrl ?? string.Empty;

        SaveCommand = new RelayCommand(Save);
        SaveAndRestartCommand = new RelayCommand(SaveAndRestart);
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

        // O botão NÃO reenvia: abre a confirmação. Reenviar o acervo inteiro por um clique acidental
        // seria a pior surpresa possível justo pra quem tem centenas de equipamentos.
        ResyncCommand = new RelayCommand(
            () => IsResyncConfirmVisible = true,
            () => CanResync && !_isResyncing);
        ConfirmResyncCommand = new RelayCommand(() => _ = ResyncNowAsync(), () => !_isResyncing);
        CancelResyncCommand = new RelayCommand(() => IsResyncConfirmVisible = false);
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

    /// <summary>Liga a sincronização na nuvem (opt-in). Só passa a valer ao reiniciar o app.</summary>
    public bool CloudSyncEnabled { get => _cloudSyncEnabled; set => Set(ref _cloudSyncEnabled, value); }

    /// <summary>Endereço HTTPS do servidor de sync. Vazio = usa a env var (compat) ou fica sem nuvem.</summary>
    public string CloudServerUrl { get => _cloudServerUrl; set => Set(ref _cloudServerUrl, value); }

    /// <summary>Mensagem de validação do endereço da nuvem (ex.: URL não-HTTPS). Vazia = ok.</summary>
    public string CloudConfigError
    {
        get => _cloudConfigError;
        private set { Set(ref _cloudConfigError, value); RaisePropertyChanged(nameof(HasCloudConfigError)); }
    }

    public bool HasCloudConfigError => !string.IsNullOrEmpty(_cloudConfigError);

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
    public RelayCommand SaveAndRestartCommand { get; }
    public RelayCommand CheckForUpdatesCommand { get; }
    public RelayCommand ApplyUpdateCommand { get; }
    public RelayCommand ManageMfaCommand { get; }

    // ── Reenviar tudo para a nuvem ───────────────────────────────────────────────────────────
    //
    // Existe porque a fila de envio CONGELA o patch no momento da edição: os equipamentos
    // cadastrados numa versão antiga subiram sem o vínculo com a credencial, e o outro PC ficou
    // dizendo "o endpoint não tem credencial" mesmo com o Chaveiro listando os nomes. Reenviar é a
    // única forma de reparar o que já está gravado no servidor. Ver CloudResyncService.

    /// <summary>Abre a confirmação do reenvio (não reenvia nada por si só).</summary>
    public RelayCommand ResyncCommand { get; }

    /// <summary>Confirma e dispara o reenvio.</summary>
    public RelayCommand ConfirmResyncCommand { get; }

    /// <summary>Fecha a confirmação sem reenviar nada.</summary>
    public RelayCommand CancelResyncCommand { get; }

    /// <summary>Há nuvem ativa? Sem ela a seção fica oculta — não há para onde reenviar.</summary>
    public bool CanResync => _resync?.CanResync == true;

    /// <summary>A confirmação ("tem certeza?") está na tela.</summary>
    public bool IsResyncConfirmVisible
    {
        get => _isResyncConfirmVisible;
        private set => Set(ref _isResyncConfirmVisible, value);
    }

    /// <summary>Um reenvio está em curso (mostra progresso e bloqueia um segundo disparo).</summary>
    public bool IsResyncing
    {
        get => _isResyncing;
        private set
        {
            Set(ref _isResyncing, value);
            ResyncCommand.RaiseCanExecuteChanged();
            ConfirmResyncCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Progresso enquanto roda e resultado ao terminar — nunca fica em branco no fim.</summary>
    public string ResyncStatus
    {
        get => _resyncStatus;
        private set { Set(ref _resyncStatus, value); RaisePropertyChanged(nameof(HasResyncStatus)); }
    }

    public bool HasResyncStatus => !string.IsNullOrEmpty(_resyncStatus);

    /// <summary>
    /// Reenvia o acervo local para a nuvem (pull → re-emite tudo → drena). Nada é alterado nem
    /// apagado: o que sobe é o dado que já está neste PC, agora COMPLETO.
    ///
    /// <para>Roda em <see cref="Task.Run(Func{Task})"/> porque o store SQLCipher é síncrono por baixo
    /// dos <c>await</c> — centenas de linhas na UI thread congelariam a janela inteira. O
    /// <see cref="Progress{T}"/> é construído AQUI (na UI thread) de propósito: ele captura o contexto
    /// de sincronização e devolve cada relatório já na thread certa pro binding.</para>
    ///
    /// <para>Não relança: a janela de Configurações não pode morrer por causa de um reenvio. A falha
    /// vira texto na tela — sem detalhe da exceção, que poderia carregar dado do operador (ADR-013).</para>
    /// </summary>
    public async Task ResyncNowAsync()
    {
        if (_resync is not { } resync || !resync.CanResync || _isResyncing)
        {
            return;
        }

        int run = ++_resyncRun;
        IsResyncConfirmVisible = false;
        IsResyncing = true;
        ResyncStatus = "Reenviando o acervo para a nuvem…";

        try
        {
            var progress = new Progress<ResyncProgress>(p =>
            {
                if (_resyncRun == run)
                {
                    ResyncStatus = $"Reenviando… {p.Completed} de {p.Total}.";
                }
            });

            ResyncResult result = await Task.Run(() => resync.ResyncAllAsync(progress));

            // Invalida os relatórios em voo ANTES de escrever o resultado, senão o último "Reenviando…"
            // chega atrasado e apaga a única frase que o operador precisa ler.
            _resyncRun++;
            ResyncStatus = Describe(result);
        }
        catch (Exception)
        {
            _resyncRun++;
            ResyncStatus = "Não foi possível concluir o reenvio agora. Verifique a conexão e tente de novo.";
        }
        finally
        {
            IsResyncing = false;
        }
    }

    private static string Describe(ResyncResult result)
    {
        if (!result.Ran)
        {
            return "Sem conta na nuvem ativa — não há para onde reenviar.";
        }

        // Reenvio parcial é dito com todas as letras: dizer só "concluído" esconderia que alguns
        // equipamentos continuam com o vínculo quebrado no outro PC.
        return result.Failed > 0
            ? $"Reenvio concluído: {result.ReEmitted} itens reenviados, {result.Failed} não puderam ser " +
              "reenviados. Tente de novo; se persistir, reporte pela aba Problemas."
            : $"Reenvio concluído: {result.ReEmitted} itens reenviados. O outro PC recebe em instantes.";
    }

    /// <summary>True quando há conta na nuvem ativa: habilita a seção de verificação em duas etapas.</summary>
    public bool CanManageMfa => _mfaApi != null;

    /// <summary>Cliente autenticado de 2FA — a janela de setup o usa pra montar seu VM. Null sem conta.</summary>
    public IMfaApi? MfaApi => _mfaApi;

    /// <summary>Pedido pra abrir a janela de 2FA (o code-behind das Configurações a abre).</summary>
    public event EventHandler? MfaSetupRequested;

    /// <summary>Disparado após persistir; a janela fecha e avisa "requer reinício" se necessário.</summary>
    public event EventHandler? Saved;

    /// <summary>
    /// Pedido de reiniciar o app pra aplicar a config de nuvem (a conta é ativada no startup). O
    /// code-behind faz o relaunch — VM não toca em processo.
    /// </summary>
    public event EventHandler? RestartRequested;

    /// <summary>Fixa o WinBox escolhido (caminho + hash calculado). A UI zera nada — dados não sensíveis.</summary>
    public void SetWinBox(string path, string sha256)
    {
        WinBoxExePath = path;
        WinBoxSha256 = sha256;
        RaisePropertyChanged(nameof(HasWinBox));
    }

    private void Save()
    {
        Persist(markCloudConfigured: false);
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Grava as settings SEMPRE relendo a base do disco: outro gravador (ex.: MarkAllSeen da aba
    /// Novidades) pode ter mudado o arquivo com a janela aberta; um snapshot cacheado do ctor
    /// reverteria essa gravação (o badge de novidades voltava). <paramref name="markCloudConfigured"/>
    /// só é true no "Aplicar e reiniciar" — é o que faz a GUI vencer a env var da nuvem.
    /// </summary>
    private void Persist(bool markCloudConfigured)
    {
        AppSettings disk = _store.Load();
        var flags = new Dictionary<string, bool>(disk.Flags, StringComparer.OrdinalIgnoreCase)
        {
            [FeatureFlagNames.RdpEnabled] = RdpEnabled,
            [FeatureFlagNames.NdeskEnabled] = NdeskEnabled,
        };
        _settings = disk with
        {
            Flags = flags,
            WinBoxExePath = WinBoxExePath,
            WinBoxSha256 = WinBoxSha256,
            CloudSyncEnabled = CloudSyncEnabled,
            CloudServerUrl = string.IsNullOrWhiteSpace(CloudServerUrl) ? null : CloudServerUrl.Trim(),
            CloudSyncConfigured = markCloudConfigured || disk.CloudSyncConfigured,
        };
        _store.Save(_settings);
    }

    /// <summary>
    /// Salva a config de nuvem e pede o reinício (a conta só é ativada no próximo startup). Valida o
    /// endereço ANTES: nuvem ligada exige HTTPS (fail-closed, ADR-013). Não dispara o Saved (que
    /// fecharia a janela) quando a validação falha — o operador vê o erro e corrige.
    /// </summary>
    private void SaveAndRestart()
    {
        if (CloudSyncEnabled)
        {
            string url = (CloudServerUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(url)
                || !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                || parsed.Scheme != Uri.UriSchemeHttps)
            {
                CloudConfigError = "Informe um endereço HTTPS válido (ex.: https://sync.suaempresa.com).";
                return;
            }
        }

        CloudConfigError = string.Empty;
        Persist(markCloudConfigured: true); // a GUI passa a mandar na nuvem (vence a env var).
        RestartRequested?.Invoke(this, EventArgs.Empty);
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
