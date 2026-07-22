using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>Os três momentos da mesma janela: fundar o time, convidar e entrar.</summary>
public enum TeamInviteMode
{
    /// <summary>Dono/gerente gerando um convite.</summary>
    Generate,

    /// <summary>Convidado informando identificador + código.</summary>
    Accept,

    /// <summary>
    /// Fundar um time NOVO e vazio. Acrescentado no fim de propósito (formato novo entra
    /// ADICIONANDO): os valores existentes não mudam de número, então nada que já esteja gravado ou
    /// comparado por aí muda de significado.
    /// </summary>
    CreateTeam,
}

/// <summary>
/// Janela de convite do time (Fatia 1). Não conhece cripto nem HTTP — fala com o
/// <see cref="TeamInviteService"/> e traduz falha em recado acionável em pt-BR.
///
/// <para><b>O texto desta tela é parte da segurança, não decoração.</b> O convite tem DUAS metades
/// que precisam viajar por canais diferentes: o link/identificador vai por e-mail, o CÓDIGO vai por
/// WhatsApp, telefone ou pessoalmente. Se o operador colar o código no mesmo e-mail, qualquer caixa
/// de entrada invadida vira acesso ao cofre do time — por isso o aviso é frase inteira, em destaque,
/// e não uma nota de rodapé.</para>
/// </summary>
public sealed class TeamInviteViewModel : BaseViewModel
{
    /// <summary>
    /// Aviso que NÃO pode sumir da tela do dono. Constante (e não texto solto no XAML) porque é
    /// afirmado por teste de render: um refactor que o apague tem de ficar vermelho.
    /// </summary>
    public const string OutOfBandWarning =
        "O CÓDIGO NÃO VAI NO E-MAIL — mande por outro canal (WhatsApp, telefone ou pessoalmente). "
        + "O e-mail leva só o link do convite. São duas metades de propósito: quem tiver só o e-mail "
        + "não abre o cofre do time.";

    /// <summary>
    /// O que o operador precisa ter LIDO antes de clicar em "criar o time". Constante, e afirmada
    /// por teste de render, pelo mesmo motivo do aviso acima.
    ///
    /// <para><b>Por que este aviso existe:</b> quem tem ~700 clientes cadastrados espera vê-los do
    /// outro lado. O time é um workspace NOVO — nasce sem nada —, e uma expectativa frustrada aqui
    /// não vira só decepção: vira o operador procurando os equipamentos, achando que a sincronização
    /// quebrou. Dizer isso antes custa uma frase; dizer depois custa um chamado.</para>
    /// </summary>
    public const string EmptyTeamWarning =
        "O time nasce VAZIO. Os seus clientes e equipamentos continuam onde estão, no seu cofre "
        + "pessoal — eles NÃO vão junto, e ninguém do time passa a enxergá-los. Depois de criar, "
        + "feche e abra o RemoteOps e escolha o time ao entrar: o que você cadastrar lá dentro é o "
        + "que o time enxerga.";

    private readonly TeamContext _team;
    private readonly Action<string> _copyToClipboard;

    private TeamInviteMode _mode;
    private string _email = string.Empty;
    private string _teamName = string.Empty;

    // Era "Technician" — um papel que NÃO EXISTE no RBAC do servidor (lá é "Operator"). O convite
    // saía com 400 e o operador não tinha como adivinhar o que digitar. Ver TeamRoles.
    private string _role = TeamRoles.Default;
    private string _inviteId = string.Empty;
    private string _code = string.Empty;
    private string _generatedCode = string.Empty;
    private string _errorMessage = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isBusy;

    /// <param name="team">
    /// O contexto da sessão inteiro, e <b>não</b> um id de workspace solto. É aqui que o vazamento
    /// morava: a janela recebia o workspace ATIVO — o cofre pessoal do operador — e convidava para
    /// ele. Recebendo o contexto, o workspace do convite sai de
    /// <see cref="TeamContext.TeamWorkspaceId"/>, que é <c>null</c> quando não existe time nenhum.
    /// </param>
    public TeamInviteViewModel(
        TeamContext team,
        TeamInviteMode mode = TeamInviteMode.Generate,
        Action<string>? copyToClipboard = null)
    {
        ArgumentNullException.ThrowIfNull(team);

        _team = team;
        _mode = mode;
        _copyToClipboard = copyToClipboard ?? (text => System.Windows.Clipboard.SetText(text));
        CopyCodeCommand = new RelayCommand(CopyCode, () => HasGeneratedCode);
    }

    /// <summary>Convite aceito — a janela fecha e quem abriu decide o que fazer com o cofre novo.</summary>
    public event EventHandler<AcceptedTeamInvite>? Accepted;

    /// <summary>
    /// Time criado. Quem abriu a janela usa isto para reavaliar o indicador de cofre: a chave do
    /// time acabou de nascer neste computador, e é o único instante em que a resposta muda sem o app
    /// reiniciar.
    /// </summary>
    public event EventHandler<CreatedTeam>? TeamCreated;

    public TeamInviteMode Mode
    {
        get => _mode;
        private set
        {
            Set(ref _mode, value);
            RaisePropertyChanged(nameof(IsGenerateMode));
            RaisePropertyChanged(nameof(IsAcceptMode));
            RaisePropertyChanged(nameof(IsCreateTeamMode));
            RaisePropertyChanged(nameof(Title));
            RaisePropertyChanged(nameof(WindowTitle));
        }
    }

    public bool IsGenerateMode => Mode == TeamInviteMode.Generate;

    public bool IsAcceptMode => Mode == TeamInviteMode.Accept;

    /// <summary>Fundar um time novo e vazio.</summary>
    public bool IsCreateTeamMode => Mode == TeamInviteMode.CreateTeam;

    public string Title => Mode switch
    {
        TeamInviteMode.Generate => "Convidar alguém para o time",
        TeamInviteMode.CreateTeam => "Criar um time",
        _ => "Entrar num time",
    };

    /// <summary>Título da JANELA. O do topo da tela é o <see cref="Title"/>; este é o do Alt+Tab.</summary>
    public string WindowTitle => $"RemoteOps — {Title}";

    /// <summary>O aviso fora-de-banda, para o XAML bindar (ver <see cref="OutOfBandWarning"/>).</summary>
    public string Warning => OutOfBandWarning;

    /// <summary>O aviso do time vazio, para o XAML bindar (ver <see cref="EmptyTeamWarning"/>).</summary>
    public string TeamCreationWarning => EmptyTeamWarning;

    /// <summary>Nome do time a criar. É só rótulo — não entra em cripto nem em id.</summary>
    public string TeamName { get => _teamName; set => Set(ref _teamName, value); }

    public string Email { get => _email; set => Set(ref _email, value); }

    /// <summary>Papel RBAC do convidado. O backend recusa papel mais poderoso que o de quem convida.</summary>
    public string Role
    {
        get => _role;
        set { Set(ref _role, value); RaisePropertyChanged(nameof(RoleDescription)); }
    }

    /// <summary>
    /// Os papéis oferecidos. Lista fechada de propósito: com campo livre, "Tecnico" (sem acento) ou
    /// "operator" (minúsculo) viravam um 400 do servidor DEPOIS de o dono já ter ditado o código por
    /// telefone — e o convite tinha de ser refeito do zero, com código novo.
    /// </summary>
    public IReadOnlyList<TeamRoles.Option> RoleOptions => TeamRoles.Options;

    /// <summary>
    /// O que o papel escolhido permite, em uma frase. O id do RBAC ("MikroTikOperator") não diz nada
    /// a quem está na rua, e escolher errado aqui só aparece semanas depois como "não consigo
    /// cadastrar o cliente".
    /// </summary>
    public string RoleDescription
    {
        get
        {
            foreach (TeamRoles.Option option in TeamRoles.Options)
            {
                if (string.Equals(option.Id, _role, StringComparison.Ordinal))
                {
                    return option.Description;
                }
            }

            return string.Empty;
        }
    }

    public string InviteId { get => _inviteId; set => Set(ref _inviteId, value); }

    /// <summary>Código digitado pelo convidado (o que chegou pelo outro canal).</summary>
    public string Code { get => _code; set => Set(ref _code, value); }

    /// <summary>
    /// O código recém-sorteado, para o dono ditar. É string porque a tela o exibe e o copia — como a
    /// chave de recuperação, ele vive um diálogo e some com o GC. O servidor nunca o teve.
    /// </summary>
    public string GeneratedCode
    {
        get => _generatedCode;
        private set
        {
            Set(ref _generatedCode, value);
            RaisePropertyChanged(nameof(HasGeneratedCode));
            CopyCodeCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasGeneratedCode => !string.IsNullOrEmpty(GeneratedCode);

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

    public RelayCommand CopyCodeCommand { get; }

    /// <summary>
    /// Funda um time NOVO e vazio. Vale em qualquer sessão — inclusive na do cofre pessoal, que é de
    /// onde todo operador parte: criar não compartilha nada, nasce um workspace próprio.
    ///
    /// <para>Não relança: a janela não pode morrer por causa de um servidor fora do ar. A falha vira
    /// texto acionável — e o recado de sucesso repete que o time está VAZIO, porque quem tem
    /// centenas de equipamentos cadastrados vai procurá-los do outro lado.</para>
    /// </summary>
    public async Task CreateTeamAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(TeamName))
        {
            ErrorMessage = "Dê um nome ao time (ex.: \"Equipe de campo\"). É só um rótulo — dá para "
                + "reconhecê-lo na hora de escolher o cofre ao abrir o RemoteOps.";
            return;
        }

        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        IsBusy = true;
        try
        {
            CreatedTeam time = await _team.Service.CreateTeamAsync(TeamName.Trim());

            // O recado REPETE o aviso do topo, agora no passado. É o último instante em que o
            // operador ainda está pensando no assunto — e é aqui que ele decide se vai procurar os
            // ~700 equipamentos do outro lado ou não.
            StatusMessage =
                $"Time \"{time.Name}\" criado — e ele está VAZIO. Os seus clientes e equipamentos "
                + "continuam no seu cofre pessoal, intactos e só seus. Para cadastrar coisas no time "
                + "e convidar gente, feche e abra o RemoteOps e escolha este time ao entrar.";

            TeamCreated?.Invoke(this, time);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Gera o convite e mostra o código ao dono. O código NÃO vai para o servidor.</summary>
    public async Task GenerateAsync()
    {
        if (IsBusy)
        {
            return;
        }

        // ⚠️ O WORKSPACE DO CONVITE É O DO TIME, e ele NÃO EXISTE numa sessão de cofre pessoal.
        // Era exatamente aqui que o vazamento morava: a janela usava o workspace ATIVO, que para o
        // operador é o cofre com todos os clientes dele. Com `TeamWorkspaceId` sendo `null` fora de
        // uma sessão de time, esquecer a checagem vira erro na hora — não convite para o acervo
        // pessoal. O serviço recusa de novo logo adiante (fail-closed em duas camadas); esta recusa
        // aqui existe para não gastar nem o sorteio do código num pedido que já não pode sair.
        if (_team.TeamWorkspaceId is not { } teamWorkspaceId)
        {
            ErrorMessage = TeamInviteService.PersonalSessionRefusal;
            return;
        }

        if (string.IsNullOrWhiteSpace(Email) || !Email.Contains('@', StringComparison.Ordinal))
        {
            ErrorMessage = "Informe o e-mail de quem você quer convidar.";
            return;
        }

        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        IsBusy = true;
        try
        {
            GeneratedTeamInvite invite = await _team.Service.CreateInviteAsync(
                teamWorkspaceId, Email.Trim(), Role.Trim());

            InviteId = invite.InviteId;
            GeneratedCode = invite.Code;

            // O e-mail é CONVENIÊNCIA, não o convite. Se ele não saiu, o dono precisa SABER — senão
            // fica esperando uma mensagem que nunca vai chegar (falha silenciosa clássica).
            StatusMessage = invite.EmailDelivered
                ? $"Convite criado e enviado para {invite.Email}. Expira em "
                  + $"{invite.ExpiresAt.ToLocalTime():dd/MM/yyyy HH:mm}. Agora passe o código abaixo "
                  + "por outro canal."
                : $"Convite criado, mas o e-mail para {invite.Email} NÃO saiu. Passe o "
                  + $"identificador ({invite.InviteId}) e o código abaixo direto para a pessoa. "
                  + $"Expira em {invite.ExpiresAt.ToLocalTime():dd/MM/yyyy HH:mm}.";
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Aceita o convite: identificador + código. A chave do time entra no cofre local aqui.</summary>
    public async Task AcceptAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(InviteId))
        {
            ErrorMessage = "Informe o identificador do convite (ele está no e-mail que você recebeu).";
            return;
        }

        if (string.IsNullOrWhiteSpace(Code))
        {
            ErrorMessage = "Informe o código que você recebeu por WhatsApp, telefone ou pessoalmente.";
            return;
        }

        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        IsBusy = true;
        try
        {
            AcceptedTeamInvite accepted = await _team.Service.AcceptInviteAsync(InviteId.Trim(), Code);

            StatusMessage = accepted.SessionRefreshRequired
                ? $"Você entrou no time \"{accepted.WorkspaceName}\" como {accepted.Role}. "
                  + "Feche e abra o RemoteOps para o acesso valer — a sessão atual ainda é a antiga."
                : $"Você entrou no time \"{accepted.WorkspaceName}\" como {accepted.Role}.";

            Accepted?.Invoke(this, accepted);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CopyCode()
    {
        try
        {
            _copyToClipboard(GeneratedCode);
            StatusMessage = "Código copiado. Cole numa conversa de WhatsApp — nunca no e-mail do convite.";
        }
        catch (Exception)
        {
            // A área de transferência pode estar travada por outro processo (COM). Não é motivo pra
            // derrubar a tela: o código está visível e pode ser ditado.
            StatusMessage = "Não foi possível copiar. Dite o código para a pessoa.";
        }
    }

    /// <summary>
    /// Falha → recado acionável em pt-BR. O <see cref="TeamInviteException"/> já vem pronto do
    /// serviço (ele é quem sabe distinguir código torto de convite vencido); o resto é rede/servidor.
    /// </summary>
    private static string Describe(Exception ex) => ex switch
    {
        TeamInviteException invite => invite.Message,

        // 422 é a recusa do SERVIDOR para operação de time num cofre pessoal (motivo
        // `workspace.personal`). Sem este caso ela cairia no genérico "o servidor recusou a operação
        // (HTTP 422)" — um número que não diz ao operador nem o que aconteceu nem o que fazer. Este
        // caminho só é alcançável com a guarda local contornada (cliente antigo, chave de time
        // plantada no cofre pessoal por um build anterior), e é justamente aí que a frase importa.
        CloudSyncException { StatusCode: HttpStatusCode.UnprocessableEntity }
            => TeamInviteService.PersonalSessionRefusal,

        CloudSyncException { StatusCode: HttpStatusCode.Forbidden }
            => "Sua conta não tem permissão para convidar neste time. Peça a quem administra o time.",

        CloudSyncException { StatusCode: HttpStatusCode.Conflict }
            => "Essa pessoa já é membro deste time.",

        CloudSyncException { StatusCode: HttpStatusCode.Unauthorized }
            => "Sua sessão expirou. Feche e abra o RemoteOps para entrar de novo.",

        CloudSyncException { StatusCode: >= HttpStatusCode.InternalServerError }
            => "O servidor está fora do ar. Tente de novo em alguns minutos.",

        CloudSyncException { StatusCode: { } status }
            => $"O servidor recusou a operação (HTTP {(int)status}). "
                + "Se persistir, confira o endereço do RemoteOps Cloud nas configurações.",

        HttpRequestException
            => "Sem conexão com o servidor. Verifique sua rede e o endereço do RemoteOps Cloud.",

        TaskCanceledException or TimeoutException
            => "O servidor demorou demais para responder. Tente de novo.",

        _ => "Não foi possível concluir a operação. Tente de novo.",
    };
}
