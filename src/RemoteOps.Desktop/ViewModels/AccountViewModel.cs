using System;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>Os três estados da janela de conta.</summary>
public enum AccountMode
{
    Login,
    Register,

    /// <summary>Pós-registro: exibe a chave de recuperação UMA vez. Não dá pra voltar.</summary>
    RecoveryKey,
}

/// <summary>
/// Janela "Entrar / Criar conta" (spec §8). Não conhece cripto nem HTTP — fala com o
/// <see cref="IAccountAuthenticator"/> e traduz falha em mensagem acionável em pt-BR.
///
/// A senha NUNCA é propriedade deste VM: ela chega como <c>char[]</c> num parâmetro de
/// <see cref="SubmitAsync"/> (lido do PasswordBox pelo code-behind) e é zerada antes de o método
/// retornar. Bindar senha como string deixaria cópias imutáveis vivas até o GC — ver
/// CredentialDialog/HostEditorDialog, mesmo padrão.
/// </summary>
public sealed class AccountViewModel : BaseViewModel
{
    /// <summary>
    /// Mínimo só no REGISTRO: é a única senha que protege um cofre E2EE (não há reset pelo
    /// servidor — ele não tem a chave). No login não se valida tamanho: a conta pode ser antiga e
    /// quem decide se a senha está certa é o AuthHash, não a UI.
    /// </summary>
    private const int MinPasswordLength = 8;

    private readonly IAccountAuthenticator _authenticator;
    private readonly Action<string> _copyToClipboard;

    private AccountMode _mode = AccountMode.Login;
    private string _email = string.Empty;
    private string _workspaceName = string.Empty;
    private string _errorMessage = string.Empty;
    private string _statusMessage = string.Empty;
    private string _recoveryKey = string.Empty;
    private string _totpCode = string.Empty;
    private bool _isBusy;
    private bool _isMfaChallenge;
    private bool _recoveryAcknowledged;
    private AccountSession? _session;

    public AccountViewModel(IAccountAuthenticator authenticator, Action<string>? copyToClipboard = null)
    {
        _authenticator = authenticator;
        _copyToClipboard = copyToClipboard ?? (text => System.Windows.Clipboard.SetText(text));
        SwitchToRegisterCommand = new RelayCommand(() => SwitchMode(AccountMode.Register));
        SwitchToLoginCommand = new RelayCommand(() => SwitchMode(AccountMode.Login));
        CopyRecoveryCommand = new RelayCommand(CopyRecovery);
        FinishCommand = new RelayCommand(Finish, () => RecoveryAcknowledged);
    }

    /// <summary>Login concluído (ou registro + chave de recuperação confirmada). A janela fecha.</summary>
    public event EventHandler? Authenticated;

    public AccountMode Mode
    {
        get => _mode;
        private set
        {
            Set(ref _mode, value);
            RaisePropertyChanged(nameof(IsLoginMode));
            RaisePropertyChanged(nameof(IsRegisterMode));
            RaisePropertyChanged(nameof(IsRecoveryMode));
            RaisePropertyChanged(nameof(IsFormMode));
            RaisePropertyChanged(nameof(Title));
            RaisePropertyChanged(nameof(SubmitButtonText));
        }
    }

    public bool IsLoginMode => Mode == AccountMode.Login;
    public bool IsRegisterMode => Mode == AccountMode.Register;
    public bool IsRecoveryMode => Mode == AccountMode.RecoveryKey;

    /// <summary>Entrar OU criar conta — os dois compartilham o formulário (e-mail + senha).</summary>
    public bool IsFormMode => Mode != AccountMode.RecoveryKey;

    public string Title => Mode switch
    {
        AccountMode.Register => "Criar conta",
        AccountMode.RecoveryKey => "Guarde sua chave de recuperação",
        _ => "Entrar na sua conta",
    };

    public string SubmitButtonText => IsRegisterMode ? "Criar conta" : "Entrar";

    public string Email { get => _email; set => Set(ref _email, value); }

    public string WorkspaceName { get => _workspaceName; set => Set(ref _workspaceName, value); }

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

    public bool IsBusy
    {
        get => _isBusy;
        private set { Set(ref _isBusy, value); RaisePropertyChanged(nameof(IsIdle)); }
    }

    /// <summary>Negação de <see cref="IsBusy"/> pro XAML (IsEnabled) — evita um converter só pra isso.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>Chave de recuperação recém-criada. Só tem valor no modo <see cref="AccountMode.RecoveryKey"/>.</summary>
    public string RecoveryKey { get => _recoveryKey; private set => Set(ref _recoveryKey, value); }

    /// <summary>
    /// Login exigiu 2FA: o backend respondeu <c>mfa_required</c>. A UI mostra o campo do código e o
    /// próximo submit reenvia a senha (do PasswordBox) + o <see cref="TotpCode"/>. Ortogonal ao
    /// <see cref="Mode"/> — só acontece no login.
    /// </summary>
    public bool IsMfaChallenge
    {
        get => _isMfaChallenge;
        private set => Set(ref _isMfaChallenge, value);
    }

    /// <summary>
    /// Código TOTP de 6 dígitos digitado no desafio de 2FA. É string (não char[]) de propósito: um
    /// código de 30s não é segredo de longa duração como a senha — bindar como texto é aceitável.
    /// </summary>
    public string TotpCode
    {
        get => _totpCode;
        set => Set(ref _totpCode, value);
    }

    /// <summary>"Guardei em local seguro" — obrigatório pra fechar a tela da chave.</summary>
    public bool RecoveryAcknowledged
    {
        get => _recoveryAcknowledged;
        set { Set(ref _recoveryAcknowledged, value); FinishCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>
    /// Sessão autenticada (contém a AMK). Quem consome é o <c>AccountSyncCoordinator</c>, via
    /// <see cref="TakeSession"/>: ele troca a raiz do cofre pra AMK, migra o cofre local, persiste
    /// os tokens no VaultTokenStore, cacheia a AMK sob DPAPI (spec §4.3) e liga o sync.
    /// </summary>
    public AccountSession? Session => _session;

    public RelayCommand SwitchToRegisterCommand { get; }
    public RelayCommand SwitchToLoginCommand { get; }
    public RelayCommand CopyRecoveryCommand { get; }
    public RelayCommand FinishCommand { get; }

    /// <summary>
    /// Entra ou cria a conta. <paramref name="password"/>/<paramref name="confirmPassword"/> são
    /// ZERADOS antes de retornar — em qualquer caminho (sucesso, erro de validação ou falha de rede).
    /// O code-behind lê o PasswordBox e chama aqui; ver <c>AccountWindow.Submit_Click</c>.
    /// </summary>
    public async Task SubmitAsync(char[] password, char[]? confirmPassword)
    {
        try
        {
            if (IsBusy)
            {
                return;
            }

            string? problem = Validate(password, confirmPassword);
            if (problem is not null)
            {
                ErrorMessage = problem;
                return;
            }

            ErrorMessage = string.Empty;
            IsBusy = true;
            try
            {
                // Captura o modo ANTES do await: se algo trocasse o modo durante a chamada de rede,
                // um registro cairia no ramo do login e a chave de recuperação NUNCA seria exibida —
                // o operador ficaria com um cofre sem plano B e sem saber disso.
                bool registering = IsRegisterMode;

                if (registering)
                {
                    _session = await _authenticator.RegisterAsync(NormalizedEmail, password, WorkspaceName.Trim());
                    RaisePropertyChanged(nameof(Session));

                    // Registro NÃO autentica direto: sem a chave de recuperação guardada, o operador
                    // fica com um cofre sem plano B (spec §6 — "exibe a RecoveryKey 1x").
                    RecoveryKey = _session.RecoveryKey ?? string.Empty;
                    SwitchMode(AccountMode.RecoveryKey);
                }
                else
                {
                    // No desafio de 2FA, manda o código; senão, nulo (o backend só exige se tiver 2FA).
                    string? totpCode = IsMfaChallenge ? TotpCode.Trim() : null;
                    _session = await _authenticator.LoginAsync(NormalizedEmail, password, totpCode);
                    RaisePropertyChanged(nameof(Session));

                    IsMfaChallenge = false;
                    Authenticated?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (MfaRequiredException)
            {
                // Senha OK, mas a conta tem 2FA: pede o código e reenvia. Se já estávamos no desafio,
                // foi código errado — a mensagem muda pra dizer isso. NÃO é erro de credencial.
                EnterMfaChallenge(codeWasWrong: IsMfaChallenge);
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
        finally
        {
            // Higiene da senha: some da memória mesmo se a validação nem chegou a chamar o servidor.
            Array.Clear(password);
            if (confirmPassword is not null)
            {
                Array.Clear(confirmPassword);
            }
        }
    }

    /// <summary>Transfere a posse da sessão (a AMK deixa de ser responsabilidade da UI).</summary>
    public AccountSession? TakeSession()
    {
        AccountSession? session = _session;
        _session = null;
        RaisePropertyChanged(nameof(Session));
        return session;
    }

    /// <summary>
    /// Descarta uma sessão que ninguém consumiu (janela fechada no meio do fluxo) zerando a AMK.
    /// Sem isto, fechar a janela da chave de recuperação no X deixaria a raiz do cofre viva na
    /// memória do processo até o GC.
    /// </summary>
    public void ClearSession()
    {
        _session?.ZeroAmk();
        _session = null;
        RaisePropertyChanged(nameof(Session));
    }

    /// <summary>E-mail é a identidade da conta no servidor: normaliza pra registro e login casarem.</summary>
    private string NormalizedEmail => Email.Trim().ToLowerInvariant();

    private string? Validate(char[] password, char[]? confirmPassword)
    {
        if (string.IsNullOrWhiteSpace(Email))
        {
            return "Informe o e-mail da conta.";
        }

        if (!MailAddress.TryCreate(NormalizedEmail, out _))
        {
            return "E-mail inválido. Confira o endereço digitado.";
        }

        if (password.Length == 0)
        {
            return "Informe a senha.";
        }

        // No desafio de 2FA (login), exige o código de 6 dígitos ANTES de bater no servidor.
        if (IsMfaChallenge && !IsSixDigits(TotpCode.Trim()))
        {
            return "Informe o código de 6 dígitos do seu aplicativo autenticador.";
        }

        if (!IsRegisterMode)
        {
            return null;
        }

        if (password.Length < MinPasswordLength)
        {
            return $"A senha deve ter pelo menos {MinPasswordLength} caracteres. "
                + "Ela é a única chave do seu cofre — o servidor não consegue redefini-la.";
        }

        if (confirmPassword is null || !password.AsSpan().SequenceEqual(confirmPassword))
        {
            return "As senhas não conferem. Digite a mesma senha nos dois campos.";
        }

        if (string.IsNullOrWhiteSpace(WorkspaceName))
        {
            return "Informe o nome do workspace (ex.: o nome da sua operação).";
        }

        return null;
    }

    private void SwitchMode(AccountMode mode)
    {
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        // Trocar Entrar/Criar conta abandona qualquer desafio de 2FA pendente.
        IsMfaChallenge = false;
        TotpCode = string.Empty;
        Mode = mode;
    }

    /// <summary>
    /// Entra (ou permanece) no desafio de 2FA. Primeira vez = mensagem informativa; código errado =
    /// mensagem de erro + limpa o campo pra redigitar. NÃO limpa a senha (o PasswordBox a mantém pro
    /// reenvio) nem autentica.
    /// </summary>
    private void EnterMfaChallenge(bool codeWasWrong)
    {
        IsMfaChallenge = true;
        if (codeWasWrong)
        {
            ErrorMessage = "Código inválido. Confira o aplicativo autenticador e tente de novo.";
            StatusMessage = string.Empty;
            TotpCode = string.Empty;
        }
        else
        {
            ErrorMessage = string.Empty;
            StatusMessage = "Esta conta usa verificação em duas etapas. "
                + "Digite o código de 6 dígitos do seu aplicativo autenticador.";
        }
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

    private void CopyRecovery()
    {
        try
        {
            _copyToClipboard(RecoveryKey);
            StatusMessage = "Chave copiada. Cole num gerenciador de senhas ou guarde fora deste PC.";
        }
        catch (Exception)
        {
            // A área de transferência pode estar travada por outro processo (COM). Não é motivo pra
            // derrubar a tela: a chave está na tela e pode ser copiada à mão.
            StatusMessage = "Não foi possível copiar. Anote a chave manualmente.";
        }
    }

    private void Finish()
    {
        if (!RecoveryAcknowledged)
        {
            return;
        }

        Authenticated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Falha → recado acionável em pt-BR (o operador tem que saber O QUE fazer). Nunca vaza detalhe
    /// técnico do servidor: a CloudSyncException já carrega só o status, sem corpo nem header.
    /// </summary>
    private static string DescribeError(Exception ex) => ex switch
    {
        // 404 no /auth/kdf = e-mail não cadastrado. Mesma mensagem do 401 de propósito: a UI não
        // pode ser o oráculo que diz "este e-mail existe" (anti-enumeração, spec §10).
        CloudSyncException
        {
            StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
        } => "E-mail ou senha inválidos. Verifique os dados e tente de novo.",

        // A AMK não abriu com a KEK derivada: na prática, senha errada.
        CryptographicException => "E-mail ou senha inválidos. Verifique os dados e tente de novo.",

        CloudSyncException { StatusCode: HttpStatusCode.Conflict }
            => "Já existe uma conta com esse e-mail. Use “Entrar” para acessá-la.",

        CloudSyncException { StatusCode: HttpStatusCode.TooManyRequests }
            => "Muitas tentativas seguidas. Aguarde um minuto e tente de novo.",

        CloudSyncException { StatusCode: >= HttpStatusCode.InternalServerError }
            => "O servidor está fora do ar. Tente de novo em alguns minutos.",

        CloudSyncException { StatusCode: { } status }
            => $"O servidor recusou a operação (HTTP {(int)status}). "
                + "Se persistir, confira o endereço do RemoteOps Cloud nas configurações.",

        // StatusCode nulo = falha de CONTRATO (campo fora do formato acordado), não de HTTP. O
        // servidor respondeu, só que numa forma que esta versão do app não sabe ler.
        CloudSyncException
            => "O servidor respondeu num formato que este app não reconhece. "
                + "Atualize o RemoteOps; se persistir, avise o suporte.",

        // Sem rede, DNS, TLS, servidor inalcançável.
        HttpRequestException
            => "Sem conexão com o servidor. Verifique sua rede e o endereço do RemoteOps Cloud.",

        TaskCanceledException or TimeoutException
            => "O servidor demorou demais para responder. Tente de novo.",

        _ => "Não foi possível concluir a operação. Tente de novo.",
    };
}
