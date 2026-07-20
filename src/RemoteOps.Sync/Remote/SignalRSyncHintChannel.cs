using System.Text.Json;

using Microsoft.AspNetCore.SignalR.Client;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Implementação de <see cref="ISyncHintChannel"/> sobre <c>Microsoft.AspNetCore.SignalR.Client</c>
/// (ADR-010/ADR-013). Conecta ao hub <c>/hubs/sync</c> com o JWT via <c>access_token</c> na query
/// (WebSocket não envia header Authorization), chama <c>JoinWorkspace</c> e levanta
/// <see cref="WorkspaceChanged"/> ao receber <c>workspace.changed</c>. TLS validado; nunca loga token
/// nem a URL do hub (o token viaja nela).
/// </summary>
public sealed class SignalRSyncHintChannel : ISyncHintChannel
{
    private readonly HubConnection _connection;
    private string? _workspaceId;
    private volatile bool _isRealTime;

    public SignalRSyncHintChannel(Uri hubUrl, Func<Task<string?>> accessTokenProvider)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options => options.AccessTokenProvider = accessTokenProvider)
            .WithAutomaticReconnect(new InfiniteRetryPolicy(TimeSpan.FromSeconds(30)))
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

        // Toda reconexão traz um ConnectionId NOVO, e o grupo do SignalR é POR ConnectionId: sem
        // re-entrar, o cliente ficava fora do grupo para sempre e o tempo real morria calado. O
        // servidor também faz auto-join no OnConnectedAsync (essa é a defesa principal, porque não
        // depende de o cliente lembrar); isto aqui é a redundância barata que ainda cobre um servidor
        // de versão antiga.
        _connection.Reconnected += async _ =>
        {
            string? workspaceId = _workspaceId;
            if (workspaceId is not null)
            {
                try
                {
                    await _connection.InvokeAsync("JoinWorkspace", workspaceId);
                }
                catch (Exception)
                {
                    // Best-effort: o auto-join do servidor cobre, e o laço por intervalo é a rede de
                    // segurança. Não se loga — a mensagem carregaria a URL do hub, e o JWT vai nela.
                }
            }

            SetRealTime(true);
        };

        // Reconnecting/Closed são os ÚNICOS pontos em que se descobre que o canal caiu: sem eles o
        // IsRealTime ficaria preso em true e a barra de sync afirmaria "tempo real" com o socket morto.
        _connection.Reconnecting += _ =>
        {
            SetRealTime(false);
            return Task.CompletedTask;
        };

        _connection.Closed += _ =>
        {
            SetRealTime(false);
            return Task.CompletedTask;
        };
    }

    public event Func<WorkspaceChangedHint, Task>? WorkspaceChanged;

    public event Action<bool>? RealTimeChanged;

    /// <summary>Fail-closed: só vira <c>true</c> depois de conectar e entrar no grupo de fato.</summary>
    public bool IsRealTime => _isRealTime;

    public async Task ConnectAsync(string workspaceId, CancellationToken ct = default)
    {
        // Guardado ANTES do connect porque é o handler de Reconnected que vai precisar dele, e uma
        // reconexão pode acontecer antes desta chamada retornar.
        _workspaceId = workspaceId;

        await _connection.StartAsync(ct);
        await _connection.InvokeAsync("JoinWorkspace", workspaceId, ct);
        SetRealTime(true);
    }

    private void SetRealTime(bool value)
    {
        // Só notifica na MUDANÇA: o SignalR dispara Reconnecting a cada tentativa, e sem esta guarda a
        // UI receberia uma enxurrada de eventos idênticos enquanto a rede está fora.
        if (_isRealTime == value)
        {
            return;
        }

        _isRealTime = value;
        RealTimeChanged?.Invoke(value);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
