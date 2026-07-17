using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>Passos da tela de 2FA.</summary>
public enum MfaSetupState
{
    /// <summary>Ponto de partida: ativar (enroll) ou desativar.</summary>
    Intro,

    /// <summary>Pós-enroll: mostra o segredo/QR e pede o código pra confirmar.</summary>
    ShowSecret,

    /// <summary>Concluído (ativado ou desativado): mostra o resultado e fecha.</summary>
    Done,
}

/// <summary>
/// UI de ativar/desativar 2FA (spec Fase 3, item 6). Fala só com o <see cref="IMfaApi"/> (autenticado):
/// enroll → mostra o segredo Base32 + otpauth URI (pra QR ou digitação) → confirm com um código.
/// Também desativa (exige código). Não conhece cripto — o TOTP é gerado/validado no servidor.
///
/// <para><b>Fronteira E2EE:</b> ativar 2FA NÃO mexe no cofre. É proteção do LOGIN; a chave do cofre
/// continua vindo da senha. A tela deixa isso explícito pro operador não confundir com recuperação.</para>
/// </summary>
public sealed class MfaEnrollmentViewModel : BaseViewModel
{
    private readonly IMfaApi _api;
    private readonly Action<string> _copyToClipboard;

    private MfaSetupState _state = MfaSetupState.Intro;
    private string _secretBase32 = string.Empty;
    private string _otpauthUri = string.Empty;
    private string _confirmCode = string.Empty;
    private string _disableCode = string.Empty;
    private string _errorMessage = string.Empty;
    private string _statusMessage = string.Empty;
    private string _doneMessage = string.Empty;
    private bool _isBusy;

    public MfaEnrollmentViewModel(IMfaApi api, Action<string>? copyToClipboard = null)
    {
        _api = api;
        _copyToClipboard = copyToClipboard ?? (text => System.Windows.Clipboard.SetText(text));
        BeginEnrollCommand = new RelayCommand(() => _ = BeginEnrollAsync(), () => IsIdle && IsIntro);
        ConfirmCommand = new RelayCommand(() => _ = ConfirmAsync(), () => IsIdle && IsShowSecret);
        DisableCommand = new RelayCommand(() => _ = DisableAsync(), () => IsIdle && IsIntro);
        CopySecretCommand = new RelayCommand(() => Copy(SecretBase32));
        CopyUriCommand = new RelayCommand(() => Copy(OtpauthUri));
        CloseCommand = new RelayCommand(() => Completed?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>A tela terminou (concluiu ou o operador fechou) — a janela pode fechar.</summary>
    public event EventHandler? Completed;

    public MfaSetupState State
    {
        get => _state;
        private set
        {
            Set(ref _state, value);
            RaisePropertyChanged(nameof(IsIntro));
            RaisePropertyChanged(nameof(IsShowSecret));
            RaisePropertyChanged(nameof(IsDone));
            RaiseCommandStates();
        }
    }

    public bool IsIntro => State == MfaSetupState.Intro;
    public bool IsShowSecret => State == MfaSetupState.ShowSecret;
    public bool IsDone => State == MfaSetupState.Done;

    /// <summary>Segredo em Base32, agrupado em blocos de 4 pra digitar sem errar.</summary>
    public string SecretBase32 { get => _secretBase32; private set => Set(ref _secretBase32, value); }

    /// <summary>otpauth:// URI — a fonte do QR (ou pra colar no app).</summary>
    public string OtpauthUri { get => _otpauthUri; private set => Set(ref _otpauthUri, value); }

    public string ConfirmCode { get => _confirmCode; set => Set(ref _confirmCode, value); }
    public string DisableCode { get => _disableCode; set => Set(ref _disableCode, value); }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set { Set(ref _errorMessage, value); RaisePropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string StatusMessage
    {
        get => _statusMessage;
        private set { Set(ref _statusMessage, value); RaisePropertyChanged(nameof(HasStatus)); }
    }

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public string DoneMessage { get => _doneMessage; private set => Set(ref _doneMessage, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set { Set(ref _isBusy, value); RaisePropertyChanged(nameof(IsIdle)); RaiseCommandStates(); }
    }

    public bool IsIdle => !IsBusy;

    public RelayCommand BeginEnrollCommand { get; }
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand DisableCommand { get; }
    public RelayCommand CopySecretCommand { get; }
    public RelayCommand CopyUriCommand { get; }
    public RelayCommand CloseCommand { get; }

    /// <summary>Enroll: pede o segredo ao servidor e passa pra tela de confirmação. NÃO ativa ainda.</summary>
    public async Task BeginEnrollAsync()
    {
        if (IsBusy || !IsIntro)
        {
            return;
        }

        await RunAsync(async () =>
        {
            MfaEnrollResponse resp = await _api.EnrollAsync();
            SecretBase32 = GroupInFours(resp.SecretBase32);
            OtpauthUri = resp.OtpauthUri;
            ConfirmCode = string.Empty;
            State = MfaSetupState.ShowSecret;
            StatusMessage = "Escaneie o QR (ou digite o segredo) no seu app autenticador e informe o código gerado.";
        });
    }

    /// <summary>Confirma o código e ATIVA o 2FA.</summary>
    public async Task ConfirmAsync()
    {
        if (IsBusy || !IsShowSecret)
        {
            return;
        }

        string code = ConfirmCode.Trim();
        if (!IsSixDigits(code))
        {
            ErrorMessage = "Informe o código de 6 dígitos do seu aplicativo autenticador.";
            return;
        }

        await RunAsync(async () =>
        {
            await _api.ConfirmAsync(new MfaConfirmRequest(code));
            DoneMessage = "Verificação em duas etapas ativada. A partir do próximo login, o app pedirá o código.";
            State = MfaSetupState.Done;
        });
    }

    /// <summary>Desativa o 2FA (exige um código válido).</summary>
    public async Task DisableAsync()
    {
        if (IsBusy || !IsIntro)
        {
            return;
        }

        string code = DisableCode.Trim();
        if (!IsSixDigits(code))
        {
            ErrorMessage = "Para desativar, informe um código de 6 dígitos do seu aplicativo autenticador.";
            return;
        }

        await RunAsync(async () =>
        {
            await _api.DisableAsync(new MfaDisableRequest(code));
            DoneMessage = "Verificação em duas etapas desativada.";
            State = MfaSetupState.Done;
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ErrorMessage = DescribeError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Copy(string text)
    {
        try
        {
            _copyToClipboard(text);
            StatusMessage = "Copiado para a área de transferência.";
        }
        catch (Exception)
        {
            StatusMessage = "Não foi possível copiar. Selecione e copie manualmente.";
        }
    }

    private void RaiseCommandStates()
    {
        BeginEnrollCommand.RaiseCanExecuteChanged();
        ConfirmCommand.RaiseCanExecuteChanged();
        DisableCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Agrupa em blocos de 4 (ex.: <c>GEZD GNBV …</c>) pra reduzir erro de digitação.</summary>
    private static string GroupInFours(string secret)
    {
        var sb = new StringBuilder(secret.Length + secret.Length / 4);
        for (int i = 0; i < secret.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                sb.Append(' ');
            }

            sb.Append(secret[i]);
        }

        return sb.ToString();
    }

    private static bool IsSixDigits(string value)
    {
        if (value.Length != 6)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        CloudSyncException { StatusCode: HttpStatusCode.Conflict }
            => "A verificação em duas etapas já está ativa nesta conta.",

        CloudSyncException { StatusCode: HttpStatusCode.BadRequest }
            => "Código inválido. Confira o aplicativo autenticador e tente de novo.",

        CloudSyncException { StatusCode: HttpStatusCode.Unauthorized }
            => "Sua sessão expirou. Feche esta janela, entre de novo e tente outra vez.",

        CloudSyncException { StatusCode: HttpStatusCode.TooManyRequests }
            => "Muitas tentativas seguidas. Aguarde um minuto e tente de novo.",

        CloudSyncException { StatusCode: >= HttpStatusCode.InternalServerError }
            => "O servidor está fora do ar. Tente de novo em alguns minutos.",

        HttpRequestException
            => "Sem conexão com o servidor. Verifique sua rede e o endereço do RemoteOps Cloud.",

        TaskCanceledException or TimeoutException
            => "O servidor demorou demais para responder. Tente de novo.",

        _ => "Não foi possível concluir a operação. Tente de novo.",
    };
}
