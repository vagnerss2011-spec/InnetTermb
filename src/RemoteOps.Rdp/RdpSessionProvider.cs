using System.Collections.Concurrent;
using RemoteOps.Contracts.Sessions;

namespace RemoteOps.Rdp;

/// <summary>
/// Trabalho não-visual da sessão RDP: resolve endpoint/usuário, monta a config,
/// audita início/fim, devolve SessionHandle. A conexão visual (Connect do MSTSCAX)
/// é disparada pela View — este provider NUNCA toca o vault (ver IRdpCredentialResolver).
/// </summary>
public sealed class RdpSessionProvider : IRdpSessionProvider
{
    private readonly IRdpEndpointResolver _endpointResolver;
    private readonly IRdpCredentialRefResolver _credentialRefResolver;
    private readonly IRdpSecurityContext _securityContext;
    private readonly IRdpAuditSink _auditSink;
    private readonly ConcurrentDictionary<string, SessionHandle> _sessions = new();
    private readonly ConcurrentDictionary<string, RdpConnectionConfig> _configs = new();

    public string Protocol => RemoteProtocol.Rdp;

    public RdpSessionProvider(
        IRdpEndpointResolver endpointResolver,
        IRdpCredentialRefResolver credentialRefResolver,
        IRdpSecurityContext securityContext,
        IRdpAuditSink auditSink)
    {
        _endpointResolver = endpointResolver;
        _credentialRefResolver = credentialRefResolver;
        _securityContext = securityContext;
        _auditSink = auditSink;
    }

    public async Task<SessionHandle> OpenAsync(SessionRequest request, CancellationToken ct)
    {
        var endpoint = await _endpointResolver.ResolveAsync(request.EndpointId, ct);
        var credRef = await _credentialRefResolver.ResolveAsync(request.CredentialRefId, ct);
        string username = credRef.Metadata?.Username
            ?? throw new InvalidOperationException(
                $"CredentialRef '{request.CredentialRefId}' não tem username em Metadata.");

        var config = RdpConnectionConfigBuilder.Build(endpoint, username, request.PreferIpv6);
        _configs[request.SessionId] = config;

        var handle = new SessionHandle
        {
            SessionId = request.SessionId,
            Protocol = RemoteProtocol.Rdp,
            EndpointId = request.EndpointId,
            OpenedAt = DateTimeOffset.UtcNow,
            IsOpen = true,
        };
        _sessions[request.SessionId] = handle;

        await _auditSink.EmitAsync(new RdpAuditEvent
        {
            Action = RdpActions.SessionOpened,
            SessionId = request.SessionId,
            Host = config.Host,
            UserId = _securityContext.ActorUserId,
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct);

        return handle;
    }

    public RdpConnectionConfig GetConnectionConfig(string sessionId) =>
        _configs.TryGetValue(sessionId, out var config)
            ? config
            : throw new InvalidOperationException($"Sessão RDP '{sessionId}' não encontrada ou ainda não aberta.");

    public async Task CloseAsync(SessionHandle handle, CancellationToken ct)
    {
        if (!_sessions.TryRemove(handle.SessionId, out _)) return;
        _configs.TryGetValue(handle.SessionId, out var config);
        _configs.TryRemove(handle.SessionId, out _);
        handle.IsOpen = false;

        await _auditSink.EmitAsync(new RdpAuditEvent
        {
            Action = RdpActions.SessionClosed,
            SessionId = handle.SessionId,
            Host = config?.Host ?? handle.EndpointId,
            UserId = _securityContext.ActorUserId,
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct);
    }
}
