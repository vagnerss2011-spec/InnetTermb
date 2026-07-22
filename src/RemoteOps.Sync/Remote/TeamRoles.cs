namespace RemoteOps.Sync.Remote;

/// <summary>
/// Os papéis do time como o CLIENTE precisa deles: um id que o servidor reconhece + um rótulo em
/// pt-BR + uma frase do que a pessoa vai poder fazer.
///
/// <para><b>Por que existe uma lista aqui</b> se o servidor já tem a dele (<c>Rbac/Roles.cs</c>): o
/// Desktop não referencia o assembly da nuvem, e até o estágio 1d o papel do convite era um
/// <c>TextBox</c> livre — "Tecnico" (sem acento, com maiúscula errada, com espaço no fim) virava um
/// 400 do servidor DEPOIS de o operador já ter ditado o código por telefone. Uma lista fechada não
/// deixa esse erro nascer.</para>
///
/// <para><b>A duplicação é protegida por teste</b> (<c>TeamRolesTests</c>): cada id daqui tem de ser
/// conhecido pelo <c>Roles.IsKnown</c> do servidor. Sem essa amarra, renomear um papel lá deixaria a
/// lista daqui oferecendo um papel que ninguém aceita — e a falha só apareceria na frente do
/// operador.</para>
/// </summary>
public static class TeamRoles
{
    /// <summary>Administra o time inteiro: convida, remove e mexe em tudo.</summary>
    public const string Owner = "Owner";

    /// <summary>Quase tudo do dono, menos ser o último responsável pelo time.</summary>
    public const string Admin = "Admin";

    /// <summary>Cadastra clientes/equipamentos e convida gente; não expulsa ninguém.</summary>
    public const string Manager = "Manager";

    /// <summary>Usa as senhas para conectar; não cadastra nem edita.</summary>
    public const string Operator = "Operator";

    /// <summary>Como o técnico, com WinBox/API do MikroTik liberados.</summary>
    public const string MikroTikOperator = "MikroTikOperator";

    /// <summary>Só leitura + trilha de auditoria.</summary>
    public const string Auditor = "Auditor";

    /// <summary>Só enxerga a lista; não conecta e não edita.</summary>
    public const string ReadOnly = "ReadOnly";

    /// <summary>
    /// O papel sugerido a quem é convidado. Técnico é o caso comum num ISP: conecta nos
    /// equipamentos do cliente sem poder reorganizar o cadastro do time inteiro.
    /// </summary>
    public const string Default = Operator;

    /// <summary>
    /// Um papel oferecível na tela de convite. Não é a lista COMPLETA do servidor de propósito: os
    /// papéis de NDesk e de release não têm sentido num time de campo, e uma caixa de seleção com
    /// dez opções obscuras faz o operador escolher a primeira.
    /// </summary>
    public sealed record Option(string Id, string Label, string Description);

    /// <summary>Opções na ordem em que a tela as mostra — da mais poderosa para a mais restrita.</summary>
    public static IReadOnlyList<Option> Options { get; } =
    [
        new(Owner, "Dono", "Administra o time: convida, remove pessoas e mexe em tudo."),
        new(Admin, "Administrador", "Convida e remove pessoas, cadastra e edita tudo."),
        new(Manager, "Gerente", "Cadastra e edita clientes e equipamentos; convida, mas não remove."),
        new(Operator, "Técnico", "Conecta nos equipamentos usando as senhas do time; não edita o cadastro."),
        new(MikroTikOperator, "Técnico MikroTik", "Como o técnico, com WinBox e API do MikroTik liberados."),
        new(Auditor, "Auditor", "Só leitura, com acesso à trilha de auditoria."),
        new(ReadOnly, "Somente leitura", "Enxerga a lista de equipamentos e nada mais."),
    ];

    /// <summary>
    /// Rótulo em pt-BR do papel, ou o <b>id cru</b> quando ele não está na lista. Devolver o id é
    /// deliberado: um papel novo criado no servidor apareceria como "Auditor2" na tela — feio, mas
    /// verdadeiro. Devolver vazio (ou "Desconhecido") esconderia do operador que aquela pessoa TEM
    /// um papel, e um campo em branco na coluna de papel é indistinguível de bug de binding.
    /// </summary>
    public static string Label(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            // Membership sem papel não deveria existir; se existir, a tela diz isso em vez de
            // desenhar uma célula vazia que ninguém sabe interpretar.
            return "(sem papel)";
        }

        foreach (Option option in Options)
        {
            if (string.Equals(option.Id, role, StringComparison.Ordinal))
            {
                return option.Label;
            }
        }

        return role;
    }
}
