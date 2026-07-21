namespace RemoteOps.Desktop.ViewModels;

/// <summary>
/// Pedido de confirmação para excluir um grupo. O <see cref="HostsViewModel"/> dispara o evento com
/// esta carga e o shell (MainWindow) responde marcando <see cref="Confirmed"/>.
///
/// <para><b>Por que evento e não MessageBox no ViewModel:</b> a exclusão de grupo propaga pelo sync
/// para TODOS os dispositivos do operador — é o tipo de ação que precisa de teste automatizado. Um
/// <c>MessageBox</c> dentro do VM tornaria o caminho intestável (modal bloqueando thread de teste) e
/// amarraria o VM ao WPF. Com o evento, o teste responde "sim"/"não" sem UI.</para>
///
/// <para>O padrão é <b>não confirmado</b>: se ninguém assinar o evento (shell que esqueceu de ligar o
/// diálogo), a exclusão simplesmente não acontece — nunca o contrário.</para>
/// </summary>
public sealed class GroupDeletionConfirmation
{
    public GroupDeletionConfirmation(string groupName)
    {
        GroupName = groupName;
    }

    /// <summary>Nome do grupo, para o diálogo NOMEAR o que será excluído.</summary>
    public string GroupName { get; }

    /// <summary>Resposta do operador; o VM só exclui quando o assinante marca <c>true</c>.</summary>
    public bool Confirmed { get; set; }
}
