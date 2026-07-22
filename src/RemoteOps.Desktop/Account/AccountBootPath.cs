namespace RemoteOps.Desktop.Account;

/// <summary>
/// Como esta execução entrou na conta.
/// </summary>
/// <param name="Activation">A conta ativada (cofre pronto).</param>
/// <param name="WorkspaceCount">
/// Quantos cofres a conta enxerga — <c>null</c> quando o boot veio do cache, porque ali <b>não há
/// lista</b>: o cache guarda um workspace só, e afirmar "1" seria inventar um número que ninguém
/// mediu. Quem lê isto é a regra 5 do <c>SessionVaultScopeResolver</c>, e lá "não perguntei" é o
/// valor mais fraco de propósito.
/// </param>
internal sealed record AccountBootEntry(AccountActivation Activation, int? WorkspaceCount);

/// <summary>
/// ⚠️ <b>Quem decide se o boot ENTRA DIRETO ou PERGUNTA.</b> É a fiação que faltava um teste — e é
/// nela que o bloqueante do H2 morava.
///
/// <para><b>A regra, em uma frase:</b> com <c>account.amk</c> em disco o app entra no cofre cacheado
/// <b>sem rede e sem perguntar</b>; sem cache, ele chama o login — e é dentro do login que o
/// <c>IWorkspaceChooser</c> aparece. As duas metades importam: perguntar a senha todo dia seria
/// atrito puro, e não perguntar NUNCA foi o beco sem saída (quatro telas mandando "feche e abra e
/// escolha o time", e o app entrando direto no cofre pessoal em todas as vezes).</para>
///
/// <para><b>Por que este tipo existe, em vez de o <c>App.xaml.cs</c> decidir inline:</b> a suíte
/// inteira injeta o chooser ou monta o resolvedor direto, então "com cache ⇒ sem chooser" nunca foi
/// exercitado ponta a ponta. Mesma disciplina do <c>TeamKeyBootRepair</c>: a decisão saiu do
/// code-behind para um tipo que um teste consegue rodar.</para>
/// </summary>
internal sealed class AccountBootPath
{
    private readonly AccountSyncCoordinator _coordinator;
    private readonly Func<CancellationToken, Task<AccountSession?>> _login;

    /// <param name="login">
    /// O fluxo de login inteiro (janela de conta → autenticador → chooser). É um delegate porque em
    /// produção ele abre uma janela modal do WPF; o que este tipo garante é <b>quando</b> ele é
    /// chamado, nunca o que ele desenha.
    /// </param>
    internal AccountBootPath(
        AccountSyncCoordinator coordinator, Func<CancellationToken, Task<AccountSession?>> login)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(login);

        _coordinator = coordinator;
        _login = login;
    }

    /// <summary>
    /// Ativa a conta. <c>null</c> = o operador desistiu do login (fechou a janela) — o App cai no
    /// modo local, exatamente como antes.
    /// </summary>
    internal async Task<AccountBootEntry?> EnterAsync(CancellationToken ct = default)
    {
        // 1) Relaunch com o cache da AMK (spec §4.3): abre SEM pedir senha e sem tocar a rede — e,
        //    portanto, sem a tela de escolha. É o boot diário de quem já usa o app, e transformá-lo
        //    numa tela a mais seria atrito puro. A contrapartida é que a escolha só reaparece quando
        //    o operador SAI da conta, que é o que o botão "Trocar de cofre…" faz.
        if (await _coordinator.TryActivateFromCacheAsync(ct).ConfigureAwait(false) is { } cached)
        {
            return new AccountBootEntry(cached, WorkspaceCount: null);
        }

        // 2) Sem cache: pede login. É DENTRO dele que o IWorkspaceChooser aparece.
        AccountSession? session = await _login(ct).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        int workspaces = session.Workspaces.Count;

        // A contagem é lida ANTES da ativação porque ela CONSOME a sessão (zera a AMK).
        AccountActivation activation =
            await _coordinator.ActivateFromLoginAsync(session, ct).ConfigureAwait(false);

        return new AccountBootEntry(activation, workspaces);
    }
}
