using System;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>
/// Uma pessoa na lista da tela de Equipe. Só formatação: quem fala com o servidor é a
/// <see cref="TeamViewModel"/>.
/// </summary>
public sealed class TeamMemberViewModel
{
    public TeamMemberViewModel(TeamMemberDto member)
    {
        ArgumentNullException.ThrowIfNull(member);
        Member = member;
    }

    public TeamMemberDto Member { get; }

    public string UserId => Member.UserId;

    /// <summary>
    /// Nome, ou o e-mail quando a conta não tem nome cadastrado. Nunca vazio: uma linha em branco
    /// na lista é indistinguível de binding quebrado, e o operador não teria como saber quem é.
    /// </summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Member.DisplayName) ? Member.Email : Member.DisplayName.Trim();

    public string Email => Member.Email;

    /// <summary>Papel em pt-BR (id cru se o servidor mandar um papel que o cliente não conhece).</summary>
    public string RoleLabel => TeamRoles.Label(Member.Role);

    /// <summary>
    /// Esta pessoa ainda não tem a chave do time. Precisa aparecer ESCRITO na linha: sem isso, o
    /// sintoma chega como "a senha não abre" no meio de um atendimento, e ninguém liga uma coisa à
    /// outra.
    /// </summary>
    public bool HasKeyWarning => !Member.HasWk;

    public string KeyWarning =>
        HasKeyWarning
            ? "Ainda sem a chave do time — enxerga a lista, mas não abre as senhas."
            : string.Empty;
}

/// <summary>
/// A tela de Equipe (Fatia 1e): quem está no time, convidar e remover.
///
/// <para><b>As duas regras que mandam aqui:</b></para>
/// <list type="number">
///   <item><b>A remoção diz a verdade inteira, na tela.</b> Cortar a membership corta o acesso
///   FUTURO; o que a pessoa já baixou continua na máquina dela e nenhum servidor desfaz isso. A
///   resposta completa é operacional — trocar as senhas nos equipamentos. Prometer menos do que a
///   criptografia entrega seria enganar o operador num assunto de segurança, e esconder isso num
///   tooltip é a mesma coisa que não dizer.</item>
///   <item><b>Nenhum caminho de erro some.</b> Lista que não carrega, remoção que falha, permissão
///   negada: tudo vira texto visível. Em especial, falha de listagem NUNCA vira lista vazia — "o
///   time não tem ninguém" é a mentira mais cara que esta tela pode contar.</item>
/// </list>
/// </summary>
public sealed class TeamViewModel : BaseViewModel
{
    /// <summary>
    /// O que o operador tem de ler ANTES de confirmar a remoção. Constante porque é afirmada por
    /// teste de render: um refactor que a apague (ou a mova para um tooltip) fica vermelho.
    /// </summary>
    public const string RemovalTruth =
        "Isso corta o acesso daqui pra frente. Não apaga o que a pessoa já viu — as senhas que ela "
        + "conhecia devem ser trocadas nos equipamentos.";

    private readonly ITeamApi _api;
    private readonly string _workspaceId;
    private readonly SessionVaultKind _sessionKind;

    private bool _isLoading;
    private bool _loaded;
    private string _loadError = string.Empty;
    private TeamMemberViewModel? _removalTarget;
    private string _actionError = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isBusy;

    /// <param name="sessionKind">
    /// Que cofre esta sessão abriu. <b>Sem default</b>: quem monta a tela tem de DIZER, porque é
    /// disso que depende oferecer ou não o convite — e um default herdado em silêncio é exatamente
    /// como o botão passou a apontar para o cofre pessoal.
    /// </param>
    public TeamViewModel(
        ITeamApi api,
        string workspaceId,
        SessionVaultKind sessionKind,
        VaultBadgeViewModel? vault = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        _api = api;
        _workspaceId = workspaceId;
        _sessionKind = sessionKind;
        Vault = vault ?? new VaultBadgeViewModel();

        ReloadCommand = new RelayCommand(() => _ = LoadAsync(), () => !_isLoading);
        InviteCommand = new RelayCommand(
            () => InviteRequested?.Invoke(this, EventArgs.Empty), () => CanInvite);
        RemoveCommand = new RelayCommand(BeginRemoval, target => target is TeamMemberViewModel);
        ConfirmRemoveCommand = new RelayCommand(() => _ = ConfirmRemoveAsync(), () => !_isBusy);
        CancelRemoveCommand = new RelayCommand(CancelRemoval);
    }

    /// <summary>Pedido de abrir a janela de convite (quem a abre é o code-behind).</summary>
    public event EventHandler? InviteRequested;

    public ObservableCollection<TeamMemberViewModel> Members { get; } = [];

    /// <summary>Em qual cofre o operador está — repetido aqui porque é onde ele pensa nisso.</summary>
    public VaultBadgeViewModel Vault { get; }

    /// <summary>
    /// Convidar faz sentido nesta sessão? Só num cofre de TIME. Numa sessão pessoal o "time" seria o
    /// acervo particular do operador, e oferecer o botão como se ele fosse funcionar é a mentira que
    /// esta tela existe justamente para não contar.
    /// </summary>
    public bool CanInvite => _sessionKind is
        SessionVaultKind.Team or
        SessionVaultKind.TeamWithoutKey;

    /// <summary>Aparece no LUGAR do botão de convite quando a sessão é a do cofre pessoal.</summary>
    public string InviteBlockedNotice =>
        CanInvite
            ? string.Empty
            : "Esta sessão abriu o seu COFRE PESSOAL, não um time. Convidar alguém daqui daria a "
              + "essa pessoa acesso a todos os seus clientes e equipamentos — por isso o convite "
              + "não é oferecido. Crie um time em Configurações → Conta → Equipe e escolha-o ao "
              + "abrir o RemoteOps.";

    public bool HasInviteBlockedNotice => !string.IsNullOrEmpty(InviteBlockedNotice);

    public string Title => "Equipe";

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            Set(ref _isLoading, value);
            RaiseListState();
            ReloadCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasLoadError => !string.IsNullOrEmpty(_loadError);

    public string LoadError
    {
        get => _loadError;
        private set { Set(ref _loadError, value); RaiseListState(); }
    }

    /// <summary>
    /// A lista só é DESENHADA quando carregou de verdade e veio alguém. Enquanto carrega e depois de
    /// falhar ela some — é o que impede a lista vazia de contar história.
    /// </summary>
    public bool ShowMembers => _loaded && !HasLoadError && Members.Count > 0;

    /// <summary>Carregou sem erro e não veio ninguém. Ver <see cref="EmptyMessage"/>.</summary>
    public bool IsEmpty => _loaded && !HasLoadError && Members.Count == 0;

    /// <summary>
    /// Time sem NENHUM membro é impossível — quem pergunta já é membro, senão levaria 403. Se
    /// acontecer, a tela diz que é anormal em vez de desenhar um vazio tranquilo que o operador
    /// interpretaria como "perdi o time".
    /// </summary>
    public string EmptyMessage =>
        "O servidor respondeu sem nenhum membro — nem a sua conta. Isso não deveria acontecer: "
        + "tente atualizar e, se continuar assim, reporte pela aba Problemas nas Configurações.";

    /// <summary>Quem está prestes a ser removido (<c>null</c> = a confirmação não está na tela).</summary>
    public TeamMemberViewModel? RemovalTarget
    {
        get => _removalTarget;
        private set
        {
            Set(ref _removalTarget, value);
            RaisePropertyChanged(nameof(IsRemovalConfirmVisible));
            RaisePropertyChanged(nameof(RemovalQuestion));
        }
    }

    public bool IsRemovalConfirmVisible => _removalTarget is not null;

    /// <summary>A pergunta com NOME e e-mail: remover a pessoa errada é irreversível para o acesso dela.</summary>
    public string RemovalQuestion =>
        _removalTarget is null
            ? string.Empty
            : $"Remover {_removalTarget.DisplayName} ({_removalTarget.Email}) do time?";

    /// <summary>A verdade sobre o que a remoção faz e o que ela NÃO faz.</summary>
    public string RemovalWarning => RemovalTruth;

    public bool HasActionError => !string.IsNullOrEmpty(_actionError);

    public string ActionError
    {
        get => _actionError;
        private set { Set(ref _actionError, value); RaisePropertyChanged(nameof(HasActionError)); }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_statusMessage);

    public string StatusMessage
    {
        get => _statusMessage;
        private set { Set(ref _statusMessage, value); RaisePropertyChanged(nameof(HasStatus)); }
    }

    /// <summary>Uma remoção está em curso (bloqueia o segundo clique).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            Set(ref _isBusy, value);
            RaisePropertyChanged(nameof(IsIdle));
            ConfirmRemoveCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Negação de <see cref="IsBusy"/> pro XAML (IsEnabled) — evita um converter só pra isso.</summary>
    public bool IsIdle => !_isBusy;

    public RelayCommand ReloadCommand { get; }

    public RelayCommand InviteCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand ConfirmRemoveCommand { get; }

    public RelayCommand CancelRemoveCommand { get; }

    /// <summary>
    /// Lê a lista de membros. Não relança: a tela de Equipe não pode morrer por causa de um servidor
    /// fora do ar — a falha vira texto, e o botão "Atualizar" continua ali.
    /// </summary>
    public async Task LoadAsync()
    {
        if (_isLoading)
        {
            return;
        }

        IsLoading = true;
        LoadError = string.Empty;
        try
        {
            TeamMembersResponse response = await _api.GetMembersAsync(_workspaceId);

            Members.Clear();
            foreach (TeamMemberDto member in response.Members)
            {
                Members.Add(new TeamMemberViewModel(member));
            }

            _loaded = true;
        }
        catch (Exception ex)
        {
            // A lista fica VAZIA e o _loaded fica false: é a combinação que faz ShowMembers e
            // IsEmpty serem os dois falsos, e só a caixa de erro aparecer. Limpar a lista sem
            // marcar o erro produziria exatamente a lista vazia mentirosa.
            Members.Clear();
            _loaded = false;
            LoadError = DescribeLoad(ex);
        }
        finally
        {
            IsLoading = false;
            RaiseListState();
        }
    }

    /// <summary>
    /// Executa a remoção confirmada. Cada um dos três desfechos do servidor vira uma frase própria:
    /// "removi", "essa pessoa não é membro" e "é o último dono" pedem ações DIFERENTES do operador.
    /// </summary>
    public async Task ConfirmRemoveAsync()
    {
        if (_removalTarget is not { } target || _isBusy)
        {
            return;
        }

        IsBusy = true;
        ActionError = string.Empty;
        StatusMessage = string.Empty;
        try
        {
            TeamMemberRemoval outcome = await _api.RemoveMemberAsync(_workspaceId, target.UserId);
            switch (outcome)
            {
                case TeamMemberRemoval.Removed:
                    RemovalTarget = null;
                    Members.Remove(target);
                    RaiseListState();

                    // O recado do sucesso REPETE a parte operacional. É o último momento em que o
                    // operador ainda está pensando no assunto e pode agir sobre os equipamentos.
                    StatusMessage =
                        $"{target.DisplayName} não tem mais acesso ao time. Lembre-se: as senhas que "
                        + "essa pessoa já conhecia continuam valendo nos equipamentos — troque-as.";
                    break;

                case TeamMemberRemoval.NotAMember:
                    // Alguém removeu antes (outro administrador, outra janela). Dizer "removido"
                    // seria inventar um ato que este app não praticou; a lista está velha e é ela
                    // que precisa ser relida.
                    RemovalTarget = null;
                    ActionError =
                        "Essa pessoa já não era membro do time — outra pessoa deve tê-la removido. "
                        + "A lista foi atualizada.";
                    await LoadAsync();
                    break;

                default:
                    ActionError =
                        "Esse é o último dono do time. Promova outra pessoa a dono antes de remover — "
                        + "sem dono, ninguém mais consegue convidar, remover ou administrar o time.";
                    break;
            }
        }
        catch (Exception ex)
        {
            // A pessoa CONTINUA na lista: só some quando o servidor confirma. Sumir aqui faria o
            // operador acreditar que cortou um acesso que segue de pé.
            ActionError = DescribeRemoval(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BeginRemoval(object? parameter)
    {
        if (parameter is not TeamMemberViewModel member)
        {
            return;
        }

        // Abre a confirmação; NUNCA remove direto. Um clique acidental na lista não pode cortar o
        // acesso de um colega no meio de um plantão.
        ActionError = string.Empty;
        StatusMessage = string.Empty;
        RemovalTarget = member;
    }

    private void CancelRemoval()
    {
        RemovalTarget = null;
        ActionError = string.Empty;
    }

    private void RaiseListState()
    {
        RaisePropertyChanged(nameof(ShowMembers));
        RaisePropertyChanged(nameof(IsEmpty));
        RaisePropertyChanged(nameof(HasLoadError));
    }

    /// <summary>Falha ao listar → recado acionável em pt-BR (nunca "erro desconhecido").</summary>
    private static string DescribeLoad(Exception ex) => ex switch
    {
        CloudSyncException { StatusCode: HttpStatusCode.Forbidden }
            => "Sua conta não tem permissão para ver os membros deste time. Peça a quem administra "
                + "o time para liberar o seu acesso.",

        CloudSyncException { StatusCode: HttpStatusCode.Unauthorized }
            => VaultSwitchText.SessionExpired,

        CloudSyncException { StatusCode: HttpStatusCode.NotFound }
            => "Este time não existe mais no servidor.",

        CloudSyncException { StatusCode: >= HttpStatusCode.InternalServerError }
            => "O servidor está fora do ar. Tente atualizar em alguns minutos.",

        CloudSyncException { StatusCode: { } status }
            => $"O servidor recusou a consulta (HTTP {(int)status}). Se persistir, confira o "
                + "endereço do RemoteOps Cloud nas configurações.",

        HttpRequestException
            => "Sem conexão com o servidor — não deu para carregar quem está no time. Verifique sua "
                + "rede e clique em Atualizar.",

        TaskCanceledException or TimeoutException
            => "O servidor demorou demais para responder. Clique em Atualizar para tentar de novo.",

        _ => "Não foi possível carregar a lista de membros. Clique em Atualizar para tentar de novo.",
    };

    /// <summary>Falha ao remover → recado acionável. O caso do 403 tem saída própria.</summary>
    private static string DescribeRemoval(Exception ex) => ex switch
    {
        CloudSyncException { StatusCode: HttpStatusCode.Forbidden }
            => "Sua conta não tem permissão para remover pessoas deste time. Peça a quem administra "
                + "o time (dono ou administrador).",

        CloudSyncException { StatusCode: HttpStatusCode.Unauthorized }
            => VaultSwitchText.SessionExpired,

        CloudSyncException { StatusCode: >= HttpStatusCode.InternalServerError }
            => "O servidor está fora do ar. A pessoa CONTINUA com acesso — tente de novo em alguns "
                + "minutos.",

        CloudSyncException { StatusCode: { } status }
            => $"O servidor recusou a remoção (HTTP {(int)status}). A pessoa continua com acesso.",

        HttpRequestException
            => "Sem conexão com o servidor. A pessoa CONTINUA com acesso ao time — tente de novo "
                + "quando a rede voltar.",

        TaskCanceledException or TimeoutException
            => "O servidor demorou demais para responder. A pessoa continua com acesso; tente de novo.",

        _ => "Não foi possível remover essa pessoa agora. Ela continua com acesso ao time.",
    };
}
