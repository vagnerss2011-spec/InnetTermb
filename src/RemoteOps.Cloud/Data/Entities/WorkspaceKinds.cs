namespace RemoteOps.Cloud.Data.Entities;

/// <summary>
/// Como o workspace NASCEU. É a marca que permite ao servidor recusar sozinho as operações de time
/// (convite, publicação da chave do time) num cofre PESSOAL — sem depender de o cliente pedir a
/// coisa certa.
///
/// <para><b>Por que o servidor precisa disso:</b> o <c>/sync</c> é escopado por workspace +
/// membership. Um convite emitido contra o workspace pessoal do operador entrega o acervo INTEIRO
/// dele (nomes de cliente, endereços, grupos, fabricantes) no computador do convidado — vazamento de
/// cadastro, sobre o qual nenhum indicador de cofre fala. Consertar só a interface deixaria a
/// proteção do lado que qualquer cliente adulterado ignora.</para>
/// </summary>
public static class WorkspaceKinds
{
    /// <summary>
    /// Cofre pessoal: o workspace que nasce junto com a conta (<c>/auth/register</c>). É o
    /// <b>default do banco</b>, e a escolha não é neutra — ver <see cref="IsTeam"/>.
    /// </summary>
    public const string Personal = "personal";

    /// <summary>Time: workspace criado por <c>POST /workspaces</c>, que nasce VAZIO e compartilhado.</summary>
    public const string Team = "team";

    /// <summary>
    /// <b>Lista de PERMISSÃO, não de negação:</b> só o valor exatamente <see cref="Team"/> é time.
    /// Nulo, vazio, um valor futuro que este binário não conhece ou lixo qualquer caem todos no lado
    /// PESSOAL — que é o lado que recusa compartilhar.
    ///
    /// <para>Escrito ao contrário (<c>kind != Personal → é time</c>), uma linha antiga com a coluna
    /// em branco, ou um valor novo introduzido por uma versão futura do servidor, passaria a
    /// autorizar convite no cofre pessoal do operador. Erro de classificação para "time" custa os
    /// ~700 clientes; para "pessoal", custa uma recusa explicada na tela.</para>
    /// </summary>
    public static bool IsTeam(string? kind) => string.Equals(kind, Team, StringComparison.Ordinal);
}
