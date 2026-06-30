using RemoteOps.Contracts.Sessions;

namespace RemoteOps.Terminal;

// TODO: Implementar na frente feature/terminal-ssh-telnet.
// Adaptadores SSH (Renci.SshNet ou similar) e Telnet.
public interface ITerminalSessionProvider : IRemoteSessionProvider
{
    Task WriteAsync(SessionHandle handle, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(SessionHandle handle, CancellationToken ct = default);

    Task ResizeAsync(SessionHandle handle, int cols, int rows, CancellationToken ct = default);
}
