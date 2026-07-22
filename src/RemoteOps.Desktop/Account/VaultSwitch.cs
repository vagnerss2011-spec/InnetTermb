using Microsoft.Data.Sqlite;

using RemoteOps.Sync;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// O que ainda não subiu do cofre que está aberto AGORA.
/// </summary>
/// <param name="Pending">Edições medidas depois do cursor do outbox deste banco.</param>
/// <param name="CheckFailed">
/// Não deu para olhar a fila. Campo PRÓPRIO, e não zero: "não consegui conferir" e "não há nada"
/// pedem frases diferentes na hora de decidir se troca de cofre. Colapsar os dois é como um erro
/// vira estado vazio — o defeito estrutural desta base.
/// </param>
public sealed record VaultSwitchBacklog(int Pending, bool CheckFailed)
{
    /// <summary>Fila medida e vazia. Diferente de "não verificada", de propósito.</summary>
    public static VaultSwitchBacklog Empty { get; } = new(0, CheckFailed: false);

    /// <summary>Há o que dizer ao operador antes de ele trocar de cofre?</summary>
    public bool HasSomethingToSay => Pending > 0 || CheckFailed;
}

/// <summary>
/// ⚠️ <b>O caminho de produção até a tela de escolha do cofre.</b>
///
/// <para><b>O bloqueante que este tipo fecha:</b> o chooser (<c>DialogWorkspaceChooser</c>) só é
/// construído dentro do login, e o login só acontece quando <c>TryActivateFromCacheAsync</c> devolve
/// <c>null</c>. Com <c>account.amk</c> em disco — o caso de TODO operador que já usa o app —, o boot
/// entra direto no cofre cacheado, sem rede e sem perguntar nada. <c>LogoutAsync</c> (o único código
/// que apaga esse cache) não tinha <b>nenhum chamador de produção</b>: quatro telas mandavam "feche e
/// abra o RemoteOps e escolha o time ao entrar", o operador fechava, abria, e caía no mesmo cofre
/// pessoal. Sem erro, sem log, sem saída.</para>
///
/// <para><b>Por que é interface:</b> a tela de Configurações precisa poder ser exercitada sem conta,
/// sem rede e sem SQLCipher — e a decisão que ela toma (perguntar antes, e o que dizer) é justamente
/// a parte que não pode regredir em silêncio.</para>
/// </summary>
public interface IVaultSwitch
{
    /// <summary>
    /// Quanto ficaria para trás se o operador trocasse de cofre AGORA. Medido no banco desta sessão,
    /// nunca presumido: o aviso genérico permanente é o aviso que ninguém lê.
    /// </summary>
    Task<VaultSwitchBacklog> ReadBacklogAsync(CancellationToken ct = default);

    /// <summary>
    /// Drena o que der e sai da conta, para que o próximo boot pergunte de novo (login → escolha do
    /// cofre). Quem reinicia o processo é a janela — ViewModel não toca em processo.
    /// </summary>
    Task SignOutAsync(CancellationToken ct = default);
}

/// <summary>
/// ⚠️ <b>A ÚNICA fonte do texto "como trocar de cofre".</b>
///
/// <para>Quatro telas diferentes mandavam <i>"feche e abra o RemoteOps e escolha o time ao entrar"</i>
/// (<c>SettingsWindow</c>, <c>SettingsViewModel</c> e dois pontos do <c>TeamInviteViewModel</c>). A
/// frase era falsa — fechar e abrir caía no mesmo cofre — e eram quatro cópias, ou seja, quatro bugs
/// esperando divergir no primeiro ajuste. Uma constante só: consertar a instrução em um lugar
/// conserta as quatro telas, e um teste consegue afirmar que nenhuma delas voltou a mentir.</para>
/// </summary>
public static class VaultSwitchText
{
    /// <summary>
    /// O texto do botão. Constante porque ele aparece DENTRO da instrução abaixo: se o rótulo mudar
    /// e a frase não, o operador procura na tela um botão que não existe mais com aquele nome.
    /// </summary>
    public const string ButtonLabel = "Trocar de cofre…";

    /// <summary>
    /// O que fazer, em uma frase que cabe no meio de outra (começa em minúscula de propósito — os
    /// quatro usos a emendam depois de uma vírgula).
    ///
    /// <para>Ela diz que o RemoteOps <b>reinicia e pede a senha</b> porque é isso que acontece: sair
    /// da conta apaga o cache da AMK, e sem ele o próximo boot volta a pedir login. Omitir esse
    /// detalhe faria o operador clicar achando que é instantâneo e depois desconfiar do app.</para>
    /// </summary>
    public const string HowToSwitch =
        "clique em \"" + ButtonLabel + "\", em Configurações → Conta: o RemoteOps envia o que estiver "
        + "na fila, reinicia, pede a sua senha e aí você escolhe em qual cofre entrar.";

    /// <summary>
    /// 401 do servidor. A frase antiga — <i>"Feche e abra o RemoteOps para entrar de novo"</i>,
    /// repetida em três telas — era a MESMA mentira por outro caminho: com o cache da AMK em disco,
    /// reabrir reusa os tokens guardados no cofre e não entra de novo em lugar nenhum. Quem realmente
    /// renova a sessão é sair da conta.
    /// </summary>
    public const string SessionExpired = "Sua sessão expirou. Para entrar de novo, " + HowToSwitch;
}

/// <summary>
/// A implementação de produção: mede a fila no banco DESTA sessão, drena o que der e sai da conta.
///
/// <para><b>A ordem drenar → sair não é estética.</b> <c>LogoutAsync</c> limpa o token store, e é
/// esse token que autentica o push. Sair primeiro deixaria a fila parada com um erro de autenticação
/// que ninguém veria — trocar de cofre viraria exatamente a perda de trabalho que este fluxo existe
/// para evitar.</para>
///
/// <para><b>Nada é apagado.</b> O outbox é durável e append-only: o que não subir agora continua no
/// banco deste cofre e sobe quando o RemoteOps for aberto nele de novo. É isso que a tela promete, e
/// é isso que o código faz.</para>
/// </summary>
internal sealed class VaultSwitch : IVaultSwitch
{
    private readonly AccountSyncCoordinator _coordinator;
    private readonly WorkspaceContext _workspace;
    private readonly Func<Task>? _drain;

    /// <param name="drain">
    /// Drenagem best-effort do outbox desta sessão (o mesmo flush do fechamento, com teto próprio).
    /// <c>null</c> quando não há sessão de sync — offline-first: sem servidor não há o que drenar, e
    /// isso não impede o operador de trocar de cofre.
    /// </param>
    internal VaultSwitch(
        AccountSyncCoordinator coordinator, WorkspaceContext workspace, Func<Task>? drain = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(workspace);

        _coordinator = coordinator;
        _workspace = workspace;
        _drain = drain;
    }

    public async Task<VaultSwitchBacklog> ReadBacklogAsync(CancellationToken ct = default)
    {
        try
        {
            using SqliteConnection conn = await _workspace.OpenConnectionAsync(ct).ConfigureAwait(false);
            return new VaultSwitchBacklog(
                await OutboxBacklog.CountPendingAsync(conn, ct).ConfigureAwait(false),
                CheckFailed: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sem detalhe da exceção (ADR-013) e SEM inventar zero: "não consegui olhar" vira campo
            // próprio, e a tela diz isso com todas as letras. Afirmar "nada pendente" sobre um banco
            // que ninguém leu é o erro virando estado vazio, no exato instante em que o operador
            // decide se troca de cofre.
            return new VaultSwitchBacklog(0, CheckFailed: true);
        }
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        // 1) Drenar PRIMEIRO, com a sessão ainda autenticada. Ver o comentário da classe: o passo 2
        //    apaga o token store, e sem token o push não sai.
        if (_drain is { } drain)
        {
            try
            {
                await drain().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // fechamento do app: não é falha de rede, é o operador desistindo.
            }
            catch (Exception)
            {
                // Rede fora no meio do drenar é ROTINA de campo (ADR-002), não erro: o outbox é
                // durável e append-only, então o que ficou sobe quando o RemoteOps for aberto neste
                // cofre de novo. Travar a troca aqui prenderia o operador no cofre errado por causa
                // de um servidor que ele não controla — e a tela já disse quantos itens estão na fila
                // ANTES de ele confirmar, que é onde a decisão dele cabe.
            }
        }

        // 2) Sair da conta. É isto que apaga `account.amk` — e é a ausência desse arquivo que faz o
        //    próximo boot pedir login e, com 2+ cofres, oferecer a escolha.
        await _coordinator.LogoutAsync(ct).ConfigureAwait(false);
    }
}
