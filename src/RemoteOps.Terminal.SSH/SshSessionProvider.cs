using System.Net;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using RemoteOps.Contracts;
using RemoteOps.Contracts.Models;

namespace RemoteOps.Terminal.SSH;

/// <summary>
/// SSH-2 adapter using SSH.NET (ADR-008).
/// Implements TOFU host-key validation, PTY resize, keepalive, and IPv6 preference.
/// </summary>
public sealed class SshSessionProvider : IRemoteSessionProvider
{
    private readonly IHostKeyStore _hostKeyStore;
    private SshClient? _client;
    private ShellStream? _shellStream;
    private volatile int _cols = 120;
    private volatile int _rows = 32;

    public RemoteProtocol Protocol => RemoteProtocol.Ssh;
    public Func<HostKeyInfo, Task<HostKeyVerdict>>? HostKeyConfirmation { get; set; }

    public SshSessionProvider(IHostKeyStore hostKeyStore)
    {
        _hostKeyStore = hostKeyStore;
    }

    public async Task ConnectAsync(
        SessionRequest request,
        PlaintextCredential credential,
        IAsyncEnumerable<byte[]> input,
        Func<byte[], Task> output,
        CancellationToken ct)
    {
        var host = await ResolveHostAsync(request.Host, request.PreferIpv6, ct);
        _cols = request.Terminal.Cols;
        _rows = request.Terminal.Rows;

        var connectionInfo = BuildConnectionInfo(host, request.Port, credential);
        _client = new SshClient(connectionInfo);
        _client.KeepAliveInterval = TimeSpan.FromSeconds(30);

        // Host key validation runs synchronously inside SSH.NET's connect path.
        // We block on the async user-confirmation via ManualResetEventSlim.
        await AttachHostKeyValidationAsync(_client, request.Host, ct);

        await Task.Run(() => _client.Connect(), ct);

        _shellStream = _client.CreateShellStream(
            terminalName: "xterm-256color",
            columns: (uint)_cols,
            rows: (uint)_rows,
            width: 0,
            height: 0,
            bufferSize: 65536);

        await Task.WhenAll(
            PumpOutputAsync(_shellStream, output, ct),
            PumpInputAsync(_shellStream, input, ct));
    }

    public Task ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        _cols = cols;
        _rows = rows;
        _shellStream?.SendPseudoTerminalSizeChange((uint)cols, (uint)rows);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _shellStream?.Dispose();
        if (_client is not null)
        {
            if (_client.IsConnected) _client.Disconnect();
            _client.Dispose();
        }
        await Task.CompletedTask;
    }

    // -------------------------------------------------------------------------

    private static async Task<string> ResolveHostAsync(string host, bool preferIpv6, CancellationToken ct)
    {
        // If already an IP literal, use as-is.
        if (IPAddress.TryParse(host, out _)) return host;

        var entries = await Dns.GetHostAddressesAsync(host, ct);

        if (preferIpv6)
        {
            var v6 = entries.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
            if (v6 is not null) return v6.ToString();
        }

        var v4 = entries.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        return v4?.ToString() ?? host;
    }

    private static ConnectionInfo BuildConnectionInfo(string host, int port, PlaintextCredential credential)
    {
        AuthenticationMethod[] methods;

        if (credential.PrivateKey is { } pkHandle)
        {
            using var ms = new MemoryStream(pkHandle.Span.ToArray());
            var pkFile = new PrivateKeyFile(ms);
            methods = [new PrivateKeyAuthenticationMethod(credential.Username, pkFile)];
        }
        else if (credential.Password is { } pwdHandle)
        {
            var pwd = pwdHandle.AsString();
            methods = [new PasswordAuthenticationMethod(credential.Username, pwd)];
        }
        else
        {
            throw new InvalidOperationException("Credential has neither password nor private key.");
        }

        return new ConnectionInfo(host, port, credential.Username, methods);
    }

    private async Task AttachHostKeyValidationAsync(SshClient client, string hostname, CancellationToken ct)
    {
        var known = await _hostKeyStore.GetFingerprintAsync(hostname, ct);
        HostKeyVerdict verdict = HostKeyVerdict.Accepted;

        using var gate = new ManualResetEventSlim(false);

        client.HostKeyReceived += (_, args) =>
        {
            var fp = FingerprintHex(args.FingerPrint);
            var hasChanged = known is not null && !string.Equals(known, fp, StringComparison.OrdinalIgnoreCase);
            var info = new HostKeyInfo(
                Host: hostname,
                FingerprintSha256: fp,
                KeyType: args.HostKeyName,
                IsKnown: known is not null,
                HasChanged: hasChanged);

            if (known is not null && !hasChanged)
            {
                // Trusted; allow immediately.
                args.CanTrust = true;
                gate.Set();
                return;
            }

            // Unknown or changed key — must ask the user.
            verdict = HostKeyConfirmation is not null
                ? RunAsync(() => HostKeyConfirmation(info)).GetAwaiter().GetResult()
                : HostKeyVerdict.RejectedByUser;

            args.CanTrust = verdict == HostKeyVerdict.Accepted;

            if (args.CanTrust)
            {
                // Store fingerprint asynchronously; fire-and-forget is safe here
                // because ConnectAsync hasn't returned yet and we're in the event thread.
                _hostKeyStore.SaveFingerprintAsync(hostname, fp, args.HostKeyName, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            gate.Set();
        };

        // gate.Set() is called inside the event during Connect(); no WaitOne needed here.
        // The event is synchronous with respect to SshClient.Connect().

        static Task<T> RunAsync<T>(Func<Task<T>> f) => f();
    }

    private static string FingerprintHex(byte[] raw)
        => Convert.ToHexString(raw).ToLowerInvariant();

    private static async Task PumpOutputAsync(ShellStream shell, Func<byte[], Task> output, CancellationToken ct)
    {
        var buf = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            int read = await shell.ReadAsync(buf, 0, buf.Length, ct);
            if (read == 0) break;
            await output(buf[..read]);
        }
    }

    private static async Task PumpInputAsync(ShellStream shell, IAsyncEnumerable<byte[]> input, CancellationToken ct)
    {
        await foreach (var chunk in input.WithCancellation(ct))
        {
            await shell.WriteAsync(chunk, 0, chunk.Length, ct);
            await shell.FlushAsync(ct);
        }
    }
}
