using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using RemoteOps.Desktop.Account;
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
    private readonly TeamContext? _team;
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

    // ── Trocar de cofre / sair da conta ──────────────────────────────────────────────────────
    private readonly IVaultSwitch? _vaultSwitch;
    private bool _isSwitchVaultConfirmVisible;
    private bool _isSwitchingVault;
    private string _switchVaultStatus = string.Empty;

    // null = ainda não foi medido. Diferente de "medido e vazio", que é VaultSwitchBacklog.Empty.
    private VaultSwitchBacklog? _switchVaultBacklog;

    public SettingsViewModel(
        ISettingsStore store,
        IUpdateService? updateService = null,
        ChangelogViewModel? changelog = null,
        BugReportViewModel? bugReport = null,
        IMfaApi? mfaApi = null,
        CloudResyncService? resync = null,
        TeamContext? team = null,
        VaultBadgeViewModel? vaultBadge = null,
        IVaultSwitch? vaultSwitch = null)
    {
        _store = store;
        VaultBadge = vaultBadge ?? new VaultBadgeViewModel();
        _updateService = updateService;
        _mfaApi = mfaApi;
        _resync = resync;
        _team = team;
        _vaultSwitch = vaultSwitch;
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

        // Equipe (Fatia 1). Só com conta na nuvem ativa: sem servidor não há convite nem membro.
        //
        // ⚠️ CONVIDAR exige mais do que conta: exige estar NUMA SESSÃO DE TIME. Até esta entrega o
        // botão convidava para o workspace ATIVO — o cofre pessoal do operador — e o convidado
        // baixava os ~700 clientes dele pelo /sync. Oferecer o botão numa sessão pessoal, mesmo com
        // a guarda do serviço no lugar, ensinaria o operador que "às vezes dá erro": é assim que a
        // recusa vira ruído e para de ser lida.
        InviteToTeamCommand = new RelayCommand(
            () => TeamInviteRequested?.Invoke(this, TeamInviteMode.Generate),
            () => IsTeamSession);

        // ENTRAR num time vale em qualquer sessão: o aceite cria uma membership nova e não toca o
        // cofre que está aberto agora.
        JoinTeamCommand = new RelayCommand(
            () => TeamInviteRequested?.Invoke(this, TeamInviteMode.Accept),
            () => _team != null);

        // A tela cheia da equipe (membros, papéis, remover) — 1e. É a partir daqui que o operador
        // enxerga COM QUEM o cofre é compartilhado. Também só na sessão de time: no cofre pessoal
        // ela listaria a "equipe" de um workspace que só tem o dono, chamando de time o acervo
        // particular dele.
        ManageTeamCommand = new RelayCommand(
            () => TeamManagementRequested?.Invoke(this, EventArgs.Empty),
            () => IsTeamSession);

        // Criar o time. Vale em QUALQUER sessão — inclusive (e principalmente) na do cofre pessoal,
        // que é de onde todo operador parte. Criar não compartilha nada: nasce um workspace NOVO e
        // vazio, e o acervo pessoal não é tocado.
        CreateTeamCommand = new RelayCommand(
            () => TeamInviteRequested?.Invoke(this, TeamInviteMode.CreateTeam),
            () => _team != null);

        // O botão NÃO reenvia: abre a confirmação. Reenviar o acervo inteiro por um clique acidental
        // seria a pior surpresa possível justo pra quem tem centenas de equipamentos.
        ResyncCommand = new RelayCommand(
            () => IsResyncConfirmVisible = true,
            () => CanResync && !_isResyncing);
        ConfirmResyncCommand = new RelayCommand(() => _ = ResyncNowAsync(), () => !_isResyncing);
        CancelResyncCommand = new RelayCommand(() => IsResyncConfirmVisible = false);

        // O botão NÃO troca nada: abre a confirmação e MEDE a fila. Sair da conta por um clique
        // acidental, com trabalho esperando na fila, seria a pior surpresa possível para quem tem
        // centenas de equipamentos — a mesma disciplina do reenvio logo acima.
        SwitchVaultCommand = new RelayCommand(
            () => _ = OpenSwitchVaultAsync(), () => CanSwitchVault && !_isSwitchingVault);
        ConfirmSwitchVaultCommand = new RelayCommand(
            () => _ = SwitchVaultNowAsync(), () => !_isSwitchingVault);
        CancelSwitchVaultCommand = new RelayCommand(() => IsSwitchVaultConfirmVisible = false);
    }

    // ── Trocar de cofre / sair da conta ───────────────────────────────────────────────────────
    //
    // ⚠️ Este é o ÚNICO caminho de produção até a tela de escolha do cofre. O app entra pelo cache
    // da AMK (`account.amk`) sem rede e sem perguntar nada; enquanto esse arquivo existir, "feche e
    // abra o RemoteOps e escolha o time" não faz absolutamente nada. Sair da conta é o que o apaga —
    // e `AccountSyncCoordinator.LogoutAsync`, que já existia e já era testado, não tinha um único
    // chamador de produção.

    /// <summary>Há conta para sair? Sem ela não existe cofre para trocar.</summary>
    public bool CanSwitchVault => _vaultSwitch is not null;

    /// <summary>O rótulo do botão, para o render test comparar com o que está desenhado.</summary>
    public string SwitchVaultButtonText => VaultSwitchText.ButtonLabel;

    /// <summary>Abre a confirmação (e mede a fila). Não troca nada por si só.</summary>
    public RelayCommand SwitchVaultCommand { get; }

    /// <summary>Confirma: drena, sai da conta e pede o reinício.</summary>
    public RelayCommand ConfirmSwitchVaultCommand { get; }

    /// <summary>Fecha a confirmação sem sair da conta.</summary>
    public RelayCommand CancelSwitchVaultCommand { get; }

    /// <summary>
    /// O que a confirmação explica antes do "sim".
    ///
    /// <para><b>A frase da senha não é detalhe.</b> Sair da conta apaga o cache da AMK, e a AMK é a
    /// raiz do cofre: no boot seguinte, quem cancelar o login recebe <i>"é preciso entrar na conta"</i>
    /// e o app ENCERRA (<c>App.xaml.cs</c>). Deixar isso implícito transformaria um clique curioso em
    /// um operador sem app até lembrar a senha — que é o oposto da saída que este botão veio dar.</para>
    /// </summary>
    public string SwitchVaultConfirmDetail =>
        "O RemoteOps fecha e abre sozinho e pede a senha da sua conta — tenha ela à mão, porque sem "
        + "entrar o app não abre. Os seus equipamentos e senhas continuam neste computador, intactos: "
        + "sair da conta não apaga nada.";

    /// <summary>A confirmação ("tem certeza?") está na tela.</summary>
    public bool IsSwitchVaultConfirmVisible
    {
        get => _isSwitchVaultConfirmVisible;
        private set => Set(ref _isSwitchVaultConfirmVisible, value);
    }

    /// <summary>Uma troca está em curso (bloqueia um segundo disparo).</summary>
    public bool IsSwitchingVault
    {
        get => _isSwitchingVault;
        private set
        {
            Set(ref _isSwitchingVault, value);
            SwitchVaultCommand.RaiseCanExecuteChanged();
            ConfirmSwitchVaultCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// ⚠️ <b>O que ficaria para trás, MEDIDO no banco desta sessão.</b> Vazio quando a fila está
    /// vazia — e "vazia" aqui é uma medição, não uma suposição: um aviso genérico permanente é o
    /// aviso que ninguém lê, e um aviso ausente sobre um banco que ninguém conseguiu abrir é erro
    /// virando estado vazio. Por isso "não deu para conferir" tem frase própria.
    ///
    /// <para>Nada é apagado nunca: o outbox é durável e append-only, então o que não subir continua
    /// no banco deste cofre e sobe quando o RemoteOps for aberto nele de novo. O aviso diz isso, e é
    /// a parte que o operador mais precisa ouvir — sem ela, o primeiro reflexo é refazer o cadastro
    /// e criar duplicata.</para>
    /// </summary>
    public string SwitchVaultBacklogText
    {
        get
        {
            if (_switchVaultBacklog is not { HasSomethingToSay: true } backlog)
            {
                return string.Empty;
            }

            string naoConferi =
                "Não foi possível conferir a fila de envio deste cofre agora";

            if (backlog.Pending == 0)
            {
                return naoConferi + ". Pode haver alteração esperando para subir — nada é apagado, e "
                    + "ela sobe quando você abrir este cofre de novo.";
            }

            string quantidade = backlog.Pending.ToString(CultureInfo.GetCultureInfo("pt-BR"));
            string frase = backlog.Pending == 1
                ? "1 alteração ainda não subiu deste cofre. "
                : $"{quantidade} alterações ainda não subiram deste cofre. ";

            frase += "O RemoteOps tenta enviá-las antes de reiniciar; o que não subir continua "
                + "guardado aqui e sobe quando você abrir este cofre de novo. Nada é apagado.";

            return backlog.CheckFailed
                ? frase + " " + naoConferi + " por inteiro, então pode haver mais coisa esperando do "
                    + "que este número mostra."
                : frase;
        }
    }

    public bool HasSwitchVaultBacklog => !string.IsNullOrEmpty(SwitchVaultBacklogText);

    /// <summary>Recado da última tentativa de sair. Nunca fica em branco quando ela falha.</summary>
    public string SwitchVaultStatus
    {
        get => _switchVaultStatus;
        private set { Set(ref _switchVaultStatus, value); RaisePropertyChanged(nameof(HasSwitchVaultStatus)); }
    }

    public bool HasSwitchVaultStatus => !string.IsNullOrEmpty(_switchVaultStatus);

    /// <summary>
    /// Abre a confirmação e mede a fila desta sessão. A medição acontece AQUI (e não no boot) porque
    /// o número precisa valer para o instante da decisão — no boot ele já estaria velho.
    /// </summary>
    public async Task OpenSwitchVaultAsync(CancellationToken ct = default)
    {
        if (_vaultSwitch is not { } vaultSwitch || _isSwitchingVault)
        {
            return;
        }

        SwitchVaultStatus = string.Empty;
        IsSwitchVaultConfirmVisible = true;

        // A sonda já devolve "não verificado" em vez de estourar; este try/catch é a cinta extra, e
        // NÃO é um catch vazio: a falha vira o mesmo aviso visível de "não deu para conferir".
        VaultSwitchBacklog backlog;
        try
        {
            backlog = await vaultSwitch.ReadBacklogAsync(ct);
        }
        catch (Exception)
        {
            backlog = new VaultSwitchBacklog(0, CheckFailed: true);
        }

        _switchVaultBacklog = backlog;
        RaisePropertyChanged(nameof(SwitchVaultBacklogText));
        RaisePropertyChanged(nameof(HasSwitchVaultBacklog));
    }

    /// <summary>
    /// Confirma: drena, sai da conta e pede o reinício.
    ///
    /// <para><b>O reinício não é enfeite.</b> O cofre, o banco, a store e todos os ViewModels são
    /// decididos UMA vez no boot (estágio 1i); sair da conta sem reiniciar deixaria o app rodando
    /// sobre um cofre de que ele já saiu. E <b>falhar ao sair não pode reiniciar assim mesmo</b>:
    /// com o cache da AMK intacto, o boot seguinte devolveria o operador ao MESMO cofre e ele
    /// concluiria que o botão não faz nada — de volta ao beco sem saída que esta entrega fecha.</para>
    /// </summary>
    public async Task SwitchVaultNowAsync(CancellationToken ct = default)
    {
        if (_vaultSwitch is not { } vaultSwitch || _isSwitchingVault)
        {
            return;
        }

        IsSwitchingVault = true;
        SwitchVaultStatus = "Enviando o que está na fila e saindo da conta…";
        try
        {
            await vaultSwitch.SignOutAsync(ct);
        }
        catch (Exception)
        {
            // Sem detalhe da exceção (ADR-013). O recado diz o que NÃO aconteceu, que é o que o
            // operador precisa saber para não ficar esperando uma tela de login que não vem.
            SwitchVaultStatus =
                "Não foi possível sair da conta agora, então o RemoteOps NÃO vai reiniciar. Nada foi "
                + "alterado e a sua fila continua guardada. Tente de novo; se persistir, reporte pela "
                + "aba Problemas.";
            IsSwitchingVault = false;
            return;
        }

        IsSwitchVaultConfirmVisible = false;
        RestartRequested?.Invoke(this, EventArgs.Empty);
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

    // ── Equipe (Fatia 1) ─────────────────────────────────────────────────────────────────────

    /// <summary>True quando há conta na nuvem ativa: habilita a seção de Equipe.</summary>
    public bool CanManageTeam => _team != null;

    /// <summary>
    /// Esta sessão abriu o cofre de um TIME. É o que separa "posso convidar e listar membros" de
    /// "estou no meu cofre pessoal".
    ///
    /// <para><b>Por que a tela precisa disto:</b> até esta entrega o botão "Convidar alguém…"
    /// convidava para o workspace ATIVO. Numa sessão pessoal isso é o cofre com todos os clientes do
    /// operador, e o convidado baixaria o cadastro inteiro. Oferecer o botão como se ele fosse
    /// funcionar é a metade da tela que ainda mentiria mesmo com a guarda do serviço no lugar.</para>
    /// </summary>
    public bool IsTeamSession => _team?.IsTeamSession == true;

    /// <summary>
    /// A frase que explica onde o operador está e o que fazer, quando ele tem conta na nuvem mas a
    /// sessão é a do cofre pessoal. Vazia nas demais situações — aviso permanente é aviso que
    /// ninguém lê.
    /// </summary>
    public string TeamScopeNotice =>
        CanManageTeam && !IsTeamSession ? PersonalSessionNotice : string.Empty;

    /// <summary>
    /// O corpo do aviso, como constante: é uma das quatro frases que mandavam o operador "fechar e
    /// abrir o RemoteOps", e a única forma de um teste garantir que ela não volta a mentir é poder
    /// lê-la sem montar meia sessão de nuvem.
    /// </summary>
    public const string PersonalSessionNotice =
        "Você está no seu cofre pessoal. Convites e lista de membros pertencem a um TIME: "
        + "crie um (ele nasce vazio, os seus equipamentos não vão junto) ou, se você já tem "
        + "um, " + VaultSwitchText.HowToSwitch;

    public bool HasTeamScopeNotice => !string.IsNullOrEmpty(TeamScopeNotice);

    /// <summary>
    /// O indicador de cofre do shell, repassado à tela de Equipe. É a MESMA instância que a barra de
    /// status usa — duas cópias do estado divergiriam, e a tela de Equipe é justamente onde a
    /// divergência seria mais cara de acreditar. Nunca null (sem shell, nasce em "cofre pessoal",
    /// que continua sendo a verdade).
    /// </summary>
    public VaultBadgeViewModel VaultBadge { get; }

    /// <summary>
    /// Reavalia em qual cofre o operador está. Mora aqui (e não no code-behind) para poder ser
    /// exercitado por teste — a janela de falha que ele fecha é sutil demais para ficar sem cobertura.
    ///
    /// <para><b>Por que existe:</b> gerar o PRIMEIRO convite é o ato que faz a chave do time nascer
    /// neste computador, ou seja, é exatamente aí que o workspace ativo passa a ser "de time". Sem
    /// esta reavaliação, o indicador continuaria dizendo "cofre pessoal", sem o aviso, até o próximo
    /// reinício — no pior momento possível: o operador acabou de convidar alguém e vai começar a
    /// cadastrar achando que já está compartilhando.</para>
    ///
    /// <para>Sem conta na nuvem, a sondagem é <c>null</c> e o indicador volta ao estado local, que
    /// continua sendo a verdade.</para>
    /// </summary>
    public Task RefreshVaultScopeAsync(CancellationToken ct = default) =>
        VaultBadge.RefreshAsync(
            _team is { } team
                ? probeCt => team.Service.IsTeamWorkspaceAsync(team.WorkspaceId, probeCt)
                : null,
            ct);

    /// <summary>Serviço + transporte + workspace ativo — as janelas de time os usam pra montar seus
    /// VMs. Null sem conta.</summary>
    public TeamContext? Team => _team;

    /// <summary>Pedido pra abrir a janela de convite, no modo escolhido (o code-behind a abre).</summary>
    public event EventHandler<TeamInviteMode>? TeamInviteRequested;

    /// <summary>Pedido pra abrir a tela de Equipe (membros/remover) — o code-behind a abre.</summary>
    public event EventHandler? TeamManagementRequested;

    public RelayCommand InviteToTeamCommand { get; }

    public RelayCommand JoinTeamCommand { get; }

    public RelayCommand ManageTeamCommand { get; }

    /// <summary>Funda um time novo e vazio (abre a janela no modo de criação).</summary>
    public RelayCommand CreateTeamCommand { get; }

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
