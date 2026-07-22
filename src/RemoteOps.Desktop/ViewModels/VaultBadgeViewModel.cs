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
    /// O workspace ativo é de TIME — <b>mas o cofre que o app abre continua sendo o PESSOAL</b>
    /// (Fatia 1; o cofre compartilhado é a Fatia 2). É o estado que precisa gritar.
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
    /// </summary>
    public const string TeamVaultNotActiveWarning =
        "Você está trabalhando no cofre PESSOAL. O cofre compartilhado deste time ainda não abre "
        + "nesta versão: os equipamentos que você cadastrar aqui chegam aos colegas, mas as SENHAS "
        + "não abrem do outro lado. Até o cofre compartilhado ser liberado, use o time só para "
        + "convidar pessoas e aceitar convites.";

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
        VaultScope.TeamPending => "Cofre pessoal — o do time ainda não abre aqui",
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
        VaultScope.TeamPending => TeamVaultNotActiveWarning,
        _ =>
            "Não foi possível confirmar com o servidor se este workspace é de time. O cofre aberto "
            + "aqui é o PESSOAL — isso não muda. Reconecte para o RemoteOps checar de novo.",
    };

    /// <summary>Acende o alerta visual. Só no estado que precisa saltar aos olhos.</summary>
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
    /// Descobre o estado perguntando "este workspace é de time?".
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

        try
        {
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
