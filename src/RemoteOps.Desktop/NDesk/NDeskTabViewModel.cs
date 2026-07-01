using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.NDesk;

/// <summary>
/// Aba NDesk: compõe o painel do operador e o painel mock do lado atendido, ambos
/// observando a mesma sessão viva via <see cref="INDeskBrokerClient.IncomingSessionRequested"/>.
/// Pinada — não é uma sessão por host, é um painel de ferramenta sempre disponível
/// enquanto a flag ndesk.enabled estiver ligada.
/// </summary>
public sealed class NDeskTabViewModel : SessionTabViewModel
{
    public NDeskTabViewModel(INDeskBrokerClient broker, string workspaceId = "ws-local")
        : base(id: "ndesk", title: "NDesk", protocol: "ndesk")
    {
        Operator = new NDeskOperatorViewModel(broker, workspaceId);
        Assisted = new NDeskAssistedViewModel(broker);
        IsPinned = true;
    }

    public NDeskOperatorViewModel Operator { get; }

    public NDeskAssistedViewModel Assisted { get; }
}
