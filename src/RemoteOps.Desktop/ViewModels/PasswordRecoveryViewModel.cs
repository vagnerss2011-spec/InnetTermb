using System;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>Os dois passos da recuperação de senha.</summary>
public enum RecoveryStep
{
    /// <summary>Passo 1: informa o e-mail para receber o código.</summary>
    RequestEmail,

    /// <summary>Passo 2: informa o código do e-mail + a chave de recuperação + a nova senha.</summary>
    EnterCode,
}

/// <summary>
/// "Esqueci a senha" (spec Fase 4). Recuperação de DOIS fatores por design do E2EE: o código do
/// e-mail restaura o ACESSO e a chave de recuperação reabre o COFRE. A UI deixa isso explícito — não
/// finge uma recuperação que o E2EE proíbe (sem a chave de recuperação, os dados cifrados não voltam).
///
/// A nova senha NUNCA é propriedade deste VM: chega como <c>char[]</c> em <see cref="SubmitResetAsync"/>
/// (lido do PasswordBox pelo code-behind) e é zerada antes de o método retornar — mesmo padrão do
/// <see cref="AccountViewModel"/>.
/// </summary>
public sealed class PasswordRecoveryViewModel : BaseViewModel
{
    /// <summary>A nova senha é a única chave do cofre — mesmo mínimo do registro (o servidor não redefine).</summary>
    private const int MinPasswordLength = 8;

    private readonly IAccountAuthenticator _authenticator;

    private RecoveryStep _step = RecoveryStep.RequestEmail;
    private string _email = string.Empty;
    private string _token = string.Empty;
    private string _recoveryKey = string.Empty;
    private string _errorMessage = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isBusy;

    public PasswordRecoveryViewModel(IAccountAuthenticator authenticator)
    {
        _authenticator = authenticator;
        BackToLoginCommand = new RelayCommand(() => BackToLogin?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Reset concluído: a UI volta ao login com a mensagem de sucesso.</summary>
    public event EventHandler? ResetCompleted;

    /// <summary>Operador desistiu / quer voltar ao login.</summary>
    public event EventHandler? BackToLogin;

    public RecoveryStep Step
    {
        get => _step;
        private set
        {
            Set(ref _step, value);
            RaisePropertyChanged(nameof(IsRequestStep));
            RaisePropertyChanged(nameof(IsCodeStep));
        }
    }

    public bool IsRequestStep => Step == RecoveryStep.RequestEmail;
    public bool IsCodeStep => Step == RecoveryStep.EnterCode;

    public string Email { get => _email; set => Set(ref _email, value); }

    /// <summary>Código de recuperação recebido por e-mail (uso único, expira em 30 min).</summary>
    public string Token { get => _token; set => Set(ref _token, value); }

    /// <summary>Chave de recuperação da conta (a que o RemoteOps mostrou uma vez, ao criar a conta).</summary>
    public string RecoveryKey { get => _recoveryKey; set => Set(ref _recoveryKey, value); }

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

    public bool IsIdle => !IsBusy;

    public RelayCommand BackToLoginCommand { get; }

    /// <summary>Passo 1: pede o código de recuperação por e-mail. Mensagem NEUTRA (anti-enumeração).</summary>
    public async Task RequestResetAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Email) || !MailAddress.TryCreate(NormalizedEmail, out _))
        {
            ErrorMessage = "Informe um e-mail válido.";
            return;
        }

        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            await _authenticator.RequestPasswordResetAsync(NormalizedEmail);

            // MESMA mensagem exista ou não a conta: a UI não pode ser o oráculo que confirma o e-mail.
            StatusMessage = "Se houver uma conta com esse e-mail, enviamos um código de recuperação. "
                + "Confira sua caixa de entrada e informe o código abaixo, junto com a sua chave de recuperação.";
            Step = RecoveryStep.EnterCode;
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

    /// <summary>
    /// Passo 2: código + chave de recuperação + nova senha. <paramref name="newPassword"/> e
    /// <paramref name="confirmPassword"/> são ZERADOS antes de retornar — em qualquer caminho.
    /// </summary>
    public async Task SubmitResetAsync(char[] newPassword, char[]? confirmPassword)
    {
        try
        {
            if (IsBusy)
            {
                return;
            }

            string? problem = ValidateReset(newPassword, confirmPassword);
            if (problem is not null)
            {
                ErrorMessage = problem;
                return;
            }

            ErrorMessage = string.Empty;
            IsBusy = true;
            try
            {
                await _authenticator.ResetPasswordWithRecoveryKeyAsync(
                    Token.Trim(), RecoveryKey.Trim(), newPassword);

                StatusMessage = string.Empty;
                ResetCompleted?.Invoke(this, EventArgs.Empty);
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
            Array.Clear(newPassword);
            if (confirmPassword is not null)
            {
                Array.Clear(confirmPassword);
            }
        }
    }

    private string NormalizedEmail => Email.Trim().ToLowerInvariant();

    private string? ValidateReset(char[] newPassword, char[]? confirmPassword)
    {
        if (string.IsNullOrWhiteSpace(Token))
        {
            return "Informe o código de recuperação que você recebeu por e-mail.";
        }

        if (string.IsNullOrWhiteSpace(RecoveryKey))
        {
            return "Informe sua chave de recuperação — a que o RemoteOps mostrou ao criar a conta. "
                + "Sem ela, por segurança (E2EE), os dados cifrados não podem ser recuperados.";
        }

        if (newPassword.Length < MinPasswordLength)
        {
            return $"A nova senha deve ter pelo menos {MinPasswordLength} caracteres.";
        }

        if (confirmPassword is null || !newPassword.AsSpan().SequenceEqual(confirmPassword))
        {
            return "As senhas não conferem. Digite a mesma senha nos dois campos.";
        }

        return null;
    }

    /// <summary>Falha → recado acionável em pt-BR. Nunca vaza detalhe técnico do servidor.</summary>
    private static string DescribeError(Exception ex) => ex switch
    {
        // A AMK não abriu com a chave de recuperação informada: na prática, chave errada.
        CryptographicException
            => "Chave de recuperação inválida. Confira a chave (com os hífens) e tente de novo.",

        // 400 = token inválido/expirado/já usado.
        CloudSyncException { StatusCode: HttpStatusCode.BadRequest }
            => "Código de recuperação inválido ou expirado. Peça um novo código e tente de novo.",

        CloudSyncException { StatusCode: HttpStatusCode.TooManyRequests }
            => "Muitas tentativas seguidas. Aguarde um minuto e tente de novo.",

        CloudSyncException { StatusCode: >= HttpStatusCode.InternalServerError }
            => "O servidor está fora do ar. Tente de novo em alguns minutos.",

        CloudSyncException { StatusCode: { } status }
            => $"O servidor recusou a operação (HTTP {(int)status}). "
                + "Se persistir, confira o endereço do RemoteOps Cloud nas configurações.",

        CloudSyncException
            => "O servidor respondeu num formato que este app não reconhece. Atualize o RemoteOps.",

        HttpRequestException
            => "Sem conexão com o servidor. Verifique sua rede e o endereço do RemoteOps Cloud.",

        TaskCanceledException or TimeoutException
            => "O servidor demorou demais para responder. Tente de novo.",

        _ => "Não foi possível concluir a operação. Tente de novo.",
    };
}
