using RemoteOps.Contracts.Models;

namespace RemoteOps.Contracts;

/// <summary>
/// Implemented by SSH and Telnet adapters. The session manager calls these methods
/// and routes data to/from the WebView2 bridge.
/// </summary>
public interface IRemoteSessionProvider : IAsyncDisposable
{
    RemoteProtocol Protocol { get; }

    /// <summary>
    /// Opens the remote connection. Must not return until the session is fully connected
    /// or throws on failure. Writes received bytes to <paramref name="output"/>.
    /// </summary>
    Task ConnectAsync(
        SessionRequest request,
        PlaintextCredential credential,
        IAsyncEnumerable<byte[]> input,
        Func<byte[], Task> output,
        CancellationToken ct);

    /// <summary>Sends a resize event to the remote PTY.</summary>
    Task ResizeAsync(int cols, int rows, CancellationToken ct = default);

    /// <summary>
    /// Raised when the provider needs the user to accept/reject a host key.
    /// The callback receives host key info and must return the user's verdict.
    /// </summary>
    Func<HostKeyInfo, Task<HostKeyVerdict>>? HostKeyConfirmation { get; set; }
}
