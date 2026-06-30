using System.Text.Json;

using Microsoft.AspNetCore.SignalR.Client;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Implementação de <see cref="ISyncHintChannel"/> sobre <c>Microsoft.AspNetCore.SignalR.Client</c>
/// (ADR-010/ADR-013). Conecta ao hub <c>/hubs/sync</c> com o JWT via <c>access_token</c> na query
/// (WebSocket não envia header Authorization), chama <c>JoinWorkspace</c> e levanta
/// <see cref="WorkspaceChanged"/> ao receber <c>workspace.changed</c>. TLS validado; nunca loga token.
/// </summary>
public sealed class SignalRSyncHintChannel : ISyncHintChannel
{
    private readonly HubConnection _connection;

    public SignalRSyncHintChannel(Uri hubUrl, Func<Task<string?>> accessTokenProvider)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options => options.AccessTokenProvider = accessTokenProvider)
            .WithAutomaticReconnect()
            .Build();

        _connection.On<JsonElement>("workspace.changed", async payload =>
        {
            WorkspaceChangedHint? hint = SyncHintParser.Parse(payload);
            Func<WorkspaceChangedHint, Task>? handler = WorkspaceChanged;
            if (hint is not null && handler is not null)
            {
                await handler(hint);
            }
        });
    }

    public event Func<WorkspaceChangedHint, Task>? WorkspaceChanged;

    public async Task ConnectAsync(string workspaceId, CancellationToken ct = default)
    {
        await _connection.StartAsync(ct);
        await _connection.InvokeAsync("JoinWorkspace", workspaceId, ct);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
