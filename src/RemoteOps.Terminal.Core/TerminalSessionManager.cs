using System.Collections.Concurrent;
using RemoteOps.Contracts;
using RemoteOps.Contracts.Models;

namespace RemoteOps.Terminal.Core;

/// <summary>
/// Manages up to <see cref="MaxSessions"/> concurrent terminal sessions.
/// Sessions are keyed by their ULID session ID.
/// </summary>
public sealed class TerminalSessionManager : IAsyncDisposable
{
    public const int MaxSessions = 10;

    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();
    private readonly ICredentialVault _vault;
    private readonly Func<RemoteProtocol, IRemoteSessionProvider> _providerFactory;

    public TerminalSessionManager(
        ICredentialVault vault,
        Func<RemoteProtocol, IRemoteSessionProvider> providerFactory)
    {
        _vault = vault;
        _providerFactory = providerFactory;
    }

    /// <summary>
    /// Opens a new session tab. Throws <see cref="MaxSessionsReachedException"/>
    /// when the 10-session limit is exceeded.
    /// </summary>
    public async Task<TerminalSession> OpenSessionAsync(SessionRequest request, CancellationToken ct = default)
    {
        if (_sessions.Count >= MaxSessions)
            throw new MaxSessionsReachedException(MaxSessions);

        var credential = await _vault.ResolveAsync(request.CredentialRefId, ct)
            ?? throw new InvalidOperationException($"Credencial '{request.CredentialRefId}' não encontrada ou vault bloqueado.");

        var provider = _providerFactory(request.Protocol);
        var session = new TerminalSession(request.SessionId, provider);
        _sessions[request.SessionId] = session;

        session.Start(request, credential);
        return session;
    }

    public bool TryGetSession(string sessionId, out TerminalSession? session)
        => _sessions.TryGetValue(sessionId, out session);

    public async Task CloseSessionAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
            await session.DisposeAsync();
    }

    public IReadOnlyCollection<string> ActiveSessionIds => [.. _sessions.Keys];

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, session) in _sessions)
            await session.DisposeAsync();
        _sessions.Clear();
    }
}

public sealed class MaxSessionsReachedException(int max)
    : Exception($"Limite de {max} sessões simultâneas atingido.");
