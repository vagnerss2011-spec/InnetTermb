using RemoteOps.Contracts.NDesk;

namespace RemoteOps.NDesk.Viewer;

// TODO: Implementar na frente feature/ndesk-viewer.
// Viewer do operador integrado ao Desktop; consome stream do Relay/WebRTC.
// Ver docs/09 e docs/22.
public interface INDeskViewerSession
{
    string SessionId { get; }

    NDeskPermissionGrant Grant { get; }

    Task ConnectAsync(string relayUri, CancellationToken ct = default);

    Task DisconnectAsync(CancellationToken ct = default);
}
