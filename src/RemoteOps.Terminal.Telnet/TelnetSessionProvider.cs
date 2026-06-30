using System.Net.Sockets;
using RemoteOps.Contracts;
using RemoteOps.Contracts.Models;

namespace RemoteOps.Terminal.Telnet;

/// <summary>
/// Minimal Telnet adapter (RFC 854). Telnet is treated as legacy:
/// - Always emits a warning flag via <see cref="TelnetWarning"/> before connecting.
/// - Requires explicit authorization via <see cref="ITelnetPolicy"/>.
/// - Does not encrypt traffic; document this in session audit.
/// </summary>
public sealed class TelnetSessionProvider : IRemoteSessionProvider
{
    private readonly ITelnetPolicy _policy;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _cols = 80;
    private int _rows = 24;

    public RemoteProtocol Protocol => RemoteProtocol.Telnet;
    public Func<HostKeyInfo, Task<HostKeyVerdict>>? HostKeyConfirmation { get; set; }

    /// <summary>
    /// Raised before connection when Telnet is used (legacy/insecure warning).
    /// UI should display a visible warning and await user acknowledgement.
    /// </summary>
    public Func<string, Task>? TelnetWarning { get; set; }

    public TelnetSessionProvider(ITelnetPolicy policy)
    {
        _policy = policy;
    }

    public async Task ConnectAsync(
        SessionRequest request,
        PlaintextCredential credential,
        IAsyncEnumerable<byte[]> input,
        Func<byte[], Task> output,
        CancellationToken ct)
    {
        // TODO: replace "default-group" with actual group resolution from request context.
        // The group ID should come from the endpoint/host record, not be hardcoded.
        // See docs/07-ssh-telnet-mikrotik.md §Telnet and ITelnetPolicy.
        const string groupId = "default-group";

        // ── POLICY GATE ─────────────────────────────────────────────────────
        // Implement this block (5-10 lines):
        // 1. Call _policy.IsTelnetAllowedAsync(groupId, request.Host, ct).
        // 2. If not allowed, throw TelnetNotAllowedException.
        // 3. Raise TelnetWarning with a user-visible message about Telnet being unencrypted.
        // 4. Await TelnetWarning so the UI can show a modal before proceeding.
        // ────────────────────────────────────────────────────────────────────
        await EnforcePolicyAsync(groupId, request.Host, ct);

        _cols = request.Terminal.Cols;
        _rows = request.Terminal.Rows;

        _tcp = new TcpClient();
        await _tcp.ConnectAsync(request.Host, request.Port, ct);
        _stream = _tcp.GetStream();

        // Send initial NAWS so the remote knows our terminal size.
        var nawsCmd = TelnetNegotiator.BuildNawsCommand(_cols, _rows);
        await _stream.WriteAsync(nawsCmd, ct);

        await Task.WhenAll(
            PumpOutputAsync(_stream, output, ct),
            PumpInputAsync(_stream, input, ct));
    }

    public async Task ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        _cols = cols;
        _rows = rows;
        if (_stream is null) return;
        var naws = TelnetNegotiator.BuildNawsCommand(cols, rows);
        await _stream.WriteAsync(naws, ct);
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        return ValueTask.CompletedTask;
    }

    // -------------------------------------------------------------------------

    private async Task EnforcePolicyAsync(string groupId, string host, CancellationToken ct)
    {
        bool allowed = await _policy.IsTelnetAllowedAsync(groupId, host, ct);
        if (!allowed)
            throw new TelnetNotAllowedException(host, groupId);

        if (TelnetWarning is not null)
            await TelnetWarning($"Atenção: Telnet para {host} não é criptografado. " +
                "Todo o tráfego, incluindo credenciais, trafega em texto puro na rede.");
    }

    private static async Task PumpOutputAsync(NetworkStream stream, Func<byte[], Task> output, CancellationToken ct)
    {
        var buf = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            int read = await stream.ReadAsync(buf, ct);
            if (read == 0) break;

            var (data, responses) = TelnetNegotiator.Process(buf.AsSpan(0, read));

            if (responses.Length > 0)
                await stream.WriteAsync(responses, ct);

            if (data.Length > 0)
                await output(data);
        }
    }

    private static async Task PumpInputAsync(NetworkStream stream, IAsyncEnumerable<byte[]> input, CancellationToken ct)
    {
        await foreach (var chunk in input.WithCancellation(ct))
            await stream.WriteAsync(chunk, ct);
    }
}

public sealed class TelnetNotAllowedException(string host, string groupId)
    : Exception($"Telnet para '{host}' não autorizado para o grupo '{groupId}'.")
{
    public string Host { get; } = host;
    public string GroupId { get; } = groupId;
}
