namespace RemoteOps.NDesk.Relay;

// TODO: Implementar na frente feature/ndesk-relay.
// Relay/TURN/media relay para NAT difícil e conexão lenta.
// Fallback TCP/TLS 443 obrigatório. Ver ADR-005 e docs/09.
public interface INDeskRelay
{
    Task<string> AllocateChannelAsync(string ticketId, CancellationToken ct = default);

    Task ReleaseChannelAsync(string channelId, CancellationToken ct = default);
}
