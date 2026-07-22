using System;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>Em qual cofre o operador está trabalhando AGORA.</summary>
public enum VaultScope
{
    /// <summary>Sem conta na nuvem: cofre pessoal que nunca sai deste computador.</summary>
    LocalOnly,

    /// <summary>Conta na nuvem, workspace pessoal.</summary>
    Personal,

    /// <summary>
    /// O cofre do TIME está ATIVO: as senhas cadastradas aqui são seladas com a chave compartilhada
    /// e abrem para os colegas. Estado normal de quem escolheu um workspace de time ao abrir.
    /// </summary>
    Team,

    /// <summary>
    /// O workspace ativo é de TIME e a chave <b>ainda não chegou neste computador</b>. Os
    /// equipamentos aparecem; nenhuma senha do time abre ou é gravada aqui — o cofre recusa alto
    /// (fail-closed). É o estado que precisa gritar, porque senão vira "o SSH não conecta" no meio
    /// de um atendimento, sem ninguém ligar uma coisa à outra.
    /// </summary>
    TeamPending,

    /// <summary>Não deu para descobrir (servidor fora e nenhuma chave de time neste PC).</summary>
    Unconfirmed,
}

/// <summary>
/// O indicador de <b>em qual cofre estou</b>, que fica visível o tempo todo (barra do shell + título
/// da janela + tela de Equipe).
///
/// <para><b>Por que ele existe:</b> o erro caro desta fatia não é criptográfico — é o operador
/// cadastrar o equipamento de um cliente achando que já está compartilhando com o time, e só
/// descobrir semanas depois, quando o colega diz que não vê nada. Um indicador que só aparece na
/// tela de Equipe chegaria tarde: o cadastro acontece na tela de Hosts.</para>
///
/// <para><b>O que ele NÃO faz: prometer.</b> Na Fatia 1 o cofre que o app abre é sempre o pessoal —
/// a chave do banco SQLCipher é um segredo dele e é por máquina, então trocar a raiz para a WK
/// deixaria o banco local ilegível (ver o "Como ficou" do estágio 1d). Escrever "Cofre do time" na
/// barra porque o workspace escolhido é de time seria exatamente a mentira que este indicador
/// existe para impedir. Ele diz o cofre REAL e, quando o workspace é de time, AVISA que o
/// compartilhado ainda não abre.</para>
/// </summary>
public sealed class VaultBadgeViewModel : BaseViewModel
{
    /// <summary>
    /// O aviso do estado incômodo. Constante (e não texto solto no XAML) porque é afirmado por teste
    /// de render: um refactor que o apague tem de ficar vermelho.
    ///
    /// <para><b>O que este texto dizia antes, e por que era mentira:</b> ele mandava <i>"conecte-se à
    /// internet uma vez para o RemoteOps buscar a chave"</i> — só que o único reparo do boot
    /// (<c>PublishOwnWrappedKeyAsync</c>) apenas <b>PUBLICA</b>, e sem chave local ele sai antes de
    /// tocar a rede. Nada buscava nada: o operador conectava, esperava, e o aviso continuava lá. Um
    /// conselho que não funciona é pior que nenhum, porque troca a ação certa por espera.</para>
    ///
    /// <para>Agora o <c>TeamKeyBootRepair</c> busca de verdade — mas <b>na abertura</b>, porque o
    /// cofre da sessão é decidido uma vez, no boot (1i). Daí as três coisas que o texto precisa
    /// dizer: o app tenta a cada abertura, <b>reabrir</b> é o que dá nova chance, e o desfecho de
    /// cada tentativa fica escrito no painel de Logs.</para>
    /// </summary>
    public const string TeamVaultNotActiveWarning =
        "A chave deste time ainda não chegou neste computador. Os equipamentos aparecem, mas "
        + "nenhuma senha do time abre ou é gravada aqui. A cada abertura o RemoteOps tenta buscar "
        + "essa chave na sua conta: se você estava sem internet, conecte e abra o RemoteOps de novo. "
        + "Se o aviso continuar, é porque a sua conta ainda não tem a chave deste time — aceite o "
        + "convite (identificador + código recebido por outro canal) ou peça um convite novo a quem "
        + "administra o time. O painel de Logs mostra o que aconteceu na última tentativa.";

    /// <summary>
    /// O texto do cofre do time ATIVO. Constante pelo mesmo motivo do de cima: é afirmado por teste
    /// de render, então apagá-lo num refactor tem de ficar vermelho.
    /// </summary>
    public const string TeamVaultActiveDetail =
        "Cofre do TIME. As senhas cadastradas aqui são seladas com a chave compartilhada e abrem "
        + "para todos os membros. Os equipamentos do seu cofre pessoal NÃO estão aqui — eles "
        + "continuam onde sempre estiveram, e ninguém do time os enxerga.";

    private VaultScope _scope = VaultScope.LocalOnly;

    /// <summary>O estado atual. Nasce em <see cref="VaultScope.LocalOnly"/>, que é a verdade antes
    /// de qualquer conta ser ativada — nunca um "carregando" que ninguém substitui depois.</summary>
    public VaultScope Scope => _scope;

    /// <summary>
    /// O rótulo curto, o que cabe na barra do shell. Carrega o alerta no PRÓPRIO texto: quem só
    /// bate o olho na barra não abre tooltip nenhum.
    /// </summary>
    public string Label => _scope switch
    {
        VaultScope.LocalOnly => "Cofre pessoal (só neste PC)",
        VaultScope.Personal => "Cofre pessoal",
        VaultScope.Team => "Cofre do TIME",
        VaultScope.TeamPending => "Cofre do time — a chave ainda não chegou aqui",
        _ => "Cofre pessoal — cofre do time não confirmado",
    };

    /// <summary>
    /// Título da janela principal. É o único lugar que continua visível com uma sessão SSH em tela
    /// cheia, e o que aparece no Alt+Tab e na barra de tarefas.
    /// </summary>
    public string WindowTitle => $"RemoteOps — {Label}";

    /// <summary>A frase inteira: tooltip na barra e texto corrido na tela de Equipe.</summary>
    public string Detail => _scope switch
    {
        VaultScope.LocalOnly =>
            "Sem conta na nuvem. Tudo o que você cadastra fica neste computador e não sincroniza "
            + "com ninguém.",
        VaultScope.Personal =>
            "Conta na nuvem, cofre pessoal. O que você cadastra sincroniza entre os SEUS "
            + "computadores; ninguém mais enxerga.",
        VaultScope.Team => TeamVaultActiveDetail,
        VaultScope.TeamPending => TeamVaultNotActiveWarning,
        _ =>
            "Não foi possível confirmar com o servidor se este workspace é de time. O cofre aberto "
            + "aqui é o PESSOAL — isso não muda. Reconecte para o RemoteOps checar de novo.",
    };

    /// <summary>
    /// Acende o alerta visual. Só no estado que precisa saltar aos olhos: cofre de time SEM a chave.
    /// O cofre do time ATIVO não é alerta — é o estado normal de quem entrou num time, e um aviso
    /// permanente é um aviso que ninguém mais lê.
    /// </summary>
    public bool IsWarning => _scope == VaultScope.TeamPending;

    /// <summary>Estado "não deu para perguntar" — visível, nunca silencioso.</summary>
    public bool IsUnconfirmed => _scope == VaultScope.Unconfirmed;

    /// <summary>Fixa o estado e notifica TUDO o que a tela mostra a partir dele.</summary>
    public void Apply(VaultScope scope)
    {
        if (_scope == scope)
        {
            return;
        }

        _scope = scope;

        // Notificação explícita: todas as propriedades daqui são calculadas em cima de _scope, e o
        // Set(ref …) do BaseViewModel só avisaria sobre o campo. Sem isto o indicador nasce certo e
        // envelhece errado na tela — que é o pior dos dois mundos.
        RaisePropertyChanged(nameof(Scope));
        RaisePropertyChanged(nameof(Label));
        RaisePropertyChanged(nameof(WindowTitle));
        RaisePropertyChanged(nameof(Detail));
        RaisePropertyChanged(nameof(IsWarning));
        RaisePropertyChanged(nameof(IsUnconfirmed));
    }

    /// <summary>
    /// Fixa o estado a partir do ESCOPO já decidido no boot. É o caminho de produção desde a Fatia
    /// 1i: o app não precisa mais perguntar "este workspace é de time?" — ele já resolveu isso para
    /// escolher o banco e o cofre. Derivar o indicador de uma SEGUNDA pergunta permitiria que ele
    /// discordasse do cofre que está realmente aberto, que é a mentira exata que ele existe para
    /// impedir.
    /// </summary>
    internal void ApplyFromSession(Account.SessionVaultKind kind) => Apply(kind switch
    {
        Account.SessionVaultKind.Team => VaultScope.Team,
        Account.SessionVaultKind.TeamWithoutKey => VaultScope.TeamPending,
        _ => VaultScope.Personal,
    });

    /// <summary>
    /// Descobre o estado perguntando "este workspace é de time?". Continua existindo para a tela de
    /// Configurações, que se reavalia depois de gerar o primeiro convite — ali o escopo do boot é
    /// anterior ao ato que fez a WK nascer.
    /// </summary>
    /// <param name="isTeamWorkspace">
    /// A sondagem (na prática, <c>TeamInviteService.IsTeamWorkspaceAsync</c>). <c>null</c> = não há
    /// conta na nuvem, e o estado continua sendo o local — nada muda para quem nunca terá time.
    /// </param>
    public async Task RefreshAsync(
        Func<CancellationToken, Task<bool>>? isTeamWorkspace, CancellationToken ct = default)
    {
        if (isTeamWorkspace is null)
        {
            Apply(VaultScope.LocalOnly);
            return;
        }

        // O cofre do time ATIVO nunca é rebaixado pela sondagem. O boot abriu o cofre COM a chave
        // (a resposta saiu do disco e vale offline); a sondagem não sabe da chave e responderia
        // "é de time" → TeamPending — e a barra gritaria "nenhuma senha é gravada aqui" com o cofre
        // funcionando. Alarme falso ensina o operador a ignorar o único aviso que importa. Esta
        // reavaliação existe para o caminho oposto (sessão PESSOAL cujo workspace virou de time ao
        // gerar o primeiro convite) — para uma sessão de time completa, não há nada a reavaliar.
        if (_scope == VaultScope.Team)
        {
            return;
        }

        try
        {
            // Sem a chave em mãos aqui, o melhor que esta sondagem sabe dizer é "é de time". Quem
            // distingue TIME de TIME-SEM-CHAVE é o escopo do boot (ApplyFromSession), que olhou o
            // disco: por isso este caminho é o de reavaliação, não o de produção.
            Apply(await isTeamWorkspace(ct).ConfigureAwait(false)
                ? VaultScope.TeamPending
                : VaultScope.Personal);
        }
        catch (OperationCanceledException)
        {
            // O app está fechando, não é falha de rede. Carimbar "não confirmado" aqui poluiria a
            // última tela que o operador vê com um alerta que não quer dizer nada.
            throw;
        }
        catch (Exception)
        {
            // Servidor fora, DNS torto, token vencido: o app NÃO SABE. Dizer "cofre pessoal" seria
            // uma afirmação que ele não tem como sustentar, justo no assunto em que errar custa
            // caro. Sem detalhe da exceção no estado (ADR-013) — o texto é para o operador, não
            // para o suporte.
            Apply(VaultScope.Unconfirmed);
        }
    }
}
