using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sessions;

namespace RemoteOps.Terminal.Telnet;

/// <summary>
/// Adaptador Telnet para ITerminalSessionProvider.
/// Telnet é tratado como protocolo legado: desabilitado por padrão, requer consentimento
/// explícito que BLOQUEIA a conexão TCP até ack do usuário (FIX 2 / ADR-009).
/// </summary>
public sealed class TelnetSessionProvider : ITerminalSessionProvider
{
    private readonly IEndpointResolver _endpointResolver;
    private readonly ITelnetConsentProvider _consentProvider;
    private readonly ITerminalAuditSink _auditSink;
    private readonly ITerminalSecurityContext _securityContext;
    private readonly ITelnetConnectionFactory _factory;
    private readonly ConcurrentDictionary<string, TelnetSessionState> _sessions = new();

    public string Protocol => RemoteProtocol.Telnet;

    public TelnetSessionProvider(
        IEndpointResolver endpointResolver,
        ITelnetConsentProvider consentProvider,
        ITerminalAuditSink auditSink,
        ITerminalSecurityContext securityContext)
        : this(endpointResolver, consentProvider, auditSink, securityContext, factory: null)
    {
    }

    // Construtor de injeção da fábrica (test seam). Internal para não expor
    // ITelnetConnectionFactory na API pública; visível aos testes via InternalsVisibleTo.
    internal TelnetSessionProvider(
        IEndpointResolver endpointResolver,
        ITelnetConsentProvider consentProvider,
        ITerminalAuditSink auditSink,
        ITerminalSecurityContext securityContext,
        ITelnetConnectionFactory? factory)
    {
        _endpointResolver = endpointResolver;
        _consentProvider = consentProvider;
        _auditSink = auditSink;
        _securityContext = securityContext;
        _factory = factory ?? new TcpTelnetConnectionFactory();
    }

    public async Task<SessionHandle> OpenAsync(SessionRequest request, CancellationToken ct)
    {
        var endpoint = await _endpointResolver.ResolveAsync(request.EndpointId, ct);
        string host = ResolveHost(endpoint, request.PreferIpv6);
        int port = endpoint.Port > 0 ? endpoint.Port : 23;

        // FIX 2: consentimento bloqueia ANTES de qualquer conexão TCP.
        // Sem ack explícito do usuário, a conexão não é aberta.
        bool consented = await _consentProvider.RequestConsentAsync(host, port, ct);
        if (!consented)
            throw new InvalidOperationException(
                $"Sessão Telnet para '{host}:{port}' abortada: usuário não consentiu.");

        int cols = request.Terminal?.Cols ?? 80;
        int rows = request.Terminal?.Rows ?? 24;

        var connection = _factory.Create(host, port);
        await connection.ConnectAsync(ct);

        var negotiator = new TelnetNegotiator();
        var writeLock = new SemaphoreSlim(1, 1);

        var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false,
        });

        var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = PumpTelnetOutputAsync(connection.RawStream, negotiator, channel.Writer, writeLock, cols, rows, readerCts.Token);

        var state = new TelnetSessionState(connection, channel, negotiator, writeLock, readerCts);
        _sessions[request.SessionId] = state;

        await _auditSink.EmitAsync(new TerminalAuditEvent
        {
            Action = TerminalActions.TelnetConsentGranted,
            SessionId = request.SessionId,
            Host = host,
            Protocol = RemoteProtocol.Telnet,
            UserId = _securityContext.ActorUserId,
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct);

        await _auditSink.EmitAsync(new TerminalAuditEvent
        {
            Action = TerminalActions.SessionOpened,
            SessionId = request.SessionId,
            Host = host,
            Protocol = RemoteProtocol.Telnet,
            UserId = _securityContext.ActorUserId,
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct);

        return new SessionHandle
        {
            SessionId = request.SessionId,
            Protocol = RemoteProtocol.Telnet,
            EndpointId = request.EndpointId,
            OpenedAt = DateTimeOffset.UtcNow,
            IsOpen = true,
        };
    }

    private static async Task PumpTelnetOutputAsync(
        Stream rawStream,
        TelnetNegotiator negotiator,
        ChannelWriter<ReadOnlyMemory<byte>> writer,
        SemaphoreSlim writeLock,
        int cols, int rows,
        CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await rawStream.ReadAsync(buffer, ct);
                if (read == 0) break;

                var (data, response) = negotiator.Process(buffer.AsSpan(0, read));

                // Enviar respostas IAC ao servidor (ex.: WILL NAWS)
                if (response.Length > 0)
                {
                    await writeLock.WaitAsync(ct);
                    try { await rawStream.WriteAsync(response, ct); }
                    finally { writeLock.Release(); }
                }

                // Após negociação NAWS, enviar tamanho inicial da janela (consome o flag).
                if (negotiator.PendingNaws)
                {
                    negotiator.PendingNaws = false;
                    var nawsPacket = TelnetNegotiator.BuildNaws(cols, rows);
                    await writeLock.WaitAsync(ct);
                    try { await rawStream.WriteAsync(nawsPacket, ct); }
                    finally { writeLock.Release(); }
                }

                if (data.Length > 0)
                    await writer.WriteAsync(new ReadOnlyMemory<byte>(data), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            writer.TryComplete();
        }
    }

    public async Task CloseAsync(SessionHandle handle, CancellationToken ct)
    {
        if (!_sessions.TryRemove(handle.SessionId, out var state)) return;

        await state.DisposeAsync();
        handle.IsOpen = false;

        await _auditSink.EmitAsync(new TerminalAuditEvent
        {
            Action = TerminalActions.SessionClosed,
            SessionId = handle.SessionId,
            Host = handle.EndpointId,
            Protocol = RemoteProtocol.Telnet,
            UserId = _securityContext.ActorUserId,
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    public async Task WriteAsync(SessionHandle handle, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(handle.SessionId, out var state))
            throw new InvalidOperationException($"Sessão '{handle.SessionId}' não encontrada.");

        // IAC escape: 0xFF no dado do usuário deve ser enviado como 0xFF 0xFF
        var escaped = EscapeIac(data.Span);
        await state.WriteLock.WaitAsync(ct);
        try { await state.Connection.RawStream.WriteAsync(escaped, ct); }
        finally { state.WriteLock.Release(); }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(
        SessionHandle handle,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(handle.SessionId, out var state))
            throw new InvalidOperationException($"Sessão '{handle.SessionId}' não encontrada.");

        await foreach (var chunk in state.OutputChannel.Reader.ReadAllAsync(ct))
            yield return chunk;
    }

    public async Task ResizeAsync(SessionHandle handle, int cols, int rows, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(handle.SessionId, out var state))
            throw new InvalidOperationException($"Sessão '{handle.SessionId}' não encontrada.");

        var nawsPacket = TelnetNegotiator.BuildNaws(cols, rows);
        await state.WriteLock.WaitAsync(ct);
        try { await state.Connection.RawStream.WriteAsync(nawsPacket, ct); }
        finally { state.WriteLock.Release(); }
    }

    private static ReadOnlyMemory<byte> EscapeIac(ReadOnlySpan<byte> data)
    {
        bool hasIac = data.IndexOf((byte)255) >= 0;
        if (!hasIac) return data.ToArray();

        var result = new List<byte>(data.Length + 4);
        foreach (byte b in data)
        {
            result.Add(b);
            if (b == 255) result.Add(255); // escape IAC
        }
        return result.ToArray();
    }

    private static string ResolveHost(Endpoint endpoint, bool preferIpv6)
    {
        if (preferIpv6 && !string.IsNullOrWhiteSpace(endpoint.Ipv6)) return endpoint.Ipv6;
        if (!string.IsNullOrWhiteSpace(endpoint.Ipv4)) return endpoint.Ipv4;
        if (!string.IsNullOrWhiteSpace(endpoint.Fqdn)) return endpoint.Fqdn;
        if (!string.IsNullOrWhiteSpace(endpoint.Ipv6)) return endpoint.Ipv6;
        throw new InvalidOperationException($"Endpoint '{endpoint.Id}' não tem endereço resolvível.");
    }
}

internal sealed class TelnetSessionState(
    ITelnetConnection connection,
    Channel<ReadOnlyMemory<byte>> outputChannel,
    TelnetNegotiator negotiator,
    SemaphoreSlim writeLock,
    CancellationTokenSource readerCts) : IAsyncDisposable
{
    public ITelnetConnection Connection { get; } = connection;
    public Channel<ReadOnlyMemory<byte>> OutputChannel { get; } = outputChannel;
    public TelnetNegotiator Negotiator { get; } = negotiator;
    public SemaphoreSlim WriteLock { get; } = writeLock;

    public async ValueTask DisposeAsync()
    {
        await readerCts.CancelAsync();
        readerCts.Dispose();
        WriteLock.Dispose();
        await Connection.DisposeAsync();
    }
}
