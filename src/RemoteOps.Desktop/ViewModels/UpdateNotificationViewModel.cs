using System.Globalization;

using RemoteOps.Desktop.Update;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>
/// Aviso de atualização na barra de status — DISCRETO e persistente, nunca modal.
///
/// <para><b>Por que não é um popup:</b> o RemoteOps é console de operação de rede e fica aberto o dia
/// inteiro. Um diálogo que rouba foco pode pipocar enquanto o operador digita num equipamento em
/// produção: as teclas vão pro diálogo e um <c>Enter</c> destinado ao roteador vira "sim, atualizar
/// agora", reiniciando o app no meio de uma manutenção. Aqui o aviso só ACENDE; quem decide a hora é o
/// operador, clicando.</para>
///
/// <para><b>Por que existe uma verificação periódica:</b> antes, a checagem só rodava no
/// <c>Loaded</c> da janela principal — quem deixa o app aberto por dias nunca era avisado de nada. Ver
/// <c>docs/superpowers/specs/2026-07-20-aviso-atualizacao-nao-intrusivo-design.md</c>.</para>
///
/// <para><b>Thread-affinity:</b> <see cref="CheckAsync"/> deve ser chamado da UI thread (o timer é um
/// <c>DispatcherTimer</c>) — ele toca propriedades ligadas a binding.</para>
/// </summary>
public sealed class UpdateNotificationViewModel : BaseViewModel
{
    private readonly IUpdateService? _updateService;

    private UpdateCheckResult? _pending;
    private DateTimeOffset? _lastSuccessfulCheck;

    public UpdateNotificationViewModel(IUpdateService? updateService)
    {
        _updateService = updateService;
        ApplyCommand = new RelayCommand(_ => RequestApply(), _ => HasUpdate);
    }

    /// <summary>
    /// Pedido de aplicar a atualização. A VM NÃO baixa nem aplica: ela só avisa quem sabe confirmar
    /// com o operador (a janela). Manter o download fora daqui é o que garante que nada é instalado
    /// sem consentimento explícito.
    /// </summary>
    public event EventHandler<UpdateCheckResult>? ApplyRequested;

    public RelayCommand ApplyCommand { get; }

    /// <summary>Há versão nova esperando? É o que controla a visibilidade do indicador.</summary>
    public bool HasUpdate => _pending is { UpdateAvailable: true, AvailableVersion: not null };

    /// <summary>Texto do indicador. Vazio quando não há nada — o bloco fica invisível.</summary>
    public string UpdateText => _pending?.AvailableVersion is { } available
        ? $"Atualização {available} disponível"
        : string.Empty;

    /// <summary>
    /// Vai no ToolTip. Existe para desfazer uma ambiguidade real: sem ele, "nenhum aviso na tela"
    /// significava ao mesmo tempo "você está atualizado" e "a verificação falhou e ninguém te contou".
    /// </summary>
    public string LastCheckText => _lastSuccessfulCheck is { } when
        ? $"Última verificação: {when.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"))}"
        : "Ainda não foi possível verificar atualizações.";

    /// <summary>
    /// Tooltip do aviso: diz o que ACONTECE ao clicar antes de informar quando foi a última checagem.
    /// Sem isso o operador via um destaque na barra sem saber que era clicável nem o que faria.
    /// </summary>
    public string HintText => $"Clique para baixar e instalar. {LastCheckText}";

    /// <summary>
    /// Verifica e atualiza o indicador. NUNCA lança: é chamado pelo timer periódico, e uma exceção
    /// escapando aqui mataria a verificação de vez — o mesmo tipo de falha silenciosa que já derrubou
    /// o laço de sync (ver v1.4.0). Falha de rede preserva o que já se sabia, em vez de "apagar" um
    /// aviso legítimo só porque uma checagem posterior não completou.
    /// </summary>
    public async Task CheckAsync()
    {
        if (_updateService is null)
        {
            return;
        }

        try
        {
            UpdateCheckResult result = await _updateService.CheckForUpdatesAsync();

            _lastSuccessfulCheck = DateTimeOffset.Now;
            _pending = result;

            // Dentro do try DE PROPÓSITO: BaseViewModel e RelayCommand invocam os eventos direto, então
            // um assinante que lance propagaria daqui — e como o tick do timer é async void, a exceção
            // acabaria no handler global, que mostra um MessageBox modal roubando o foco do teclado. Ou
            // seja: o próprio mecanismo do aviso discreto reintroduziria o risco que ele existe pra
            // eliminar. Hoje só o binding do WPF assina (não lança), mas o contrato "nunca lança" tem
            // de valer por construção, não por sorte dos assinantes atuais.
            RaisePropertyChanged(nameof(HasUpdate));
            RaisePropertyChanged(nameof(UpdateText));
            RaisePropertyChanged(nameof(LastCheckText));
            RaisePropertyChanged(nameof(HintText));
            ApplyCommand.RaiseCanExecuteChanged();
        }
        catch (Exception)
        {
            // Falha de rede mantém _pending e o carimbo do último sucesso: uma checagem que não
            // completou NÃO pode apagar um aviso legítimo já detectado.
        }
    }

    private void RequestApply()
    {
        if (_pending is { } check && HasUpdate)
        {
            ApplyRequested?.Invoke(this, check);
        }
    }
}
