using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Web.WebView2.Core;
using RemoteOps.Contracts;
using RemoteOps.Contracts.Models;

namespace RemoteOps.Terminal.Core;

/// <summary>
/// Represents one active terminal session bound to a WebView2 instance.
/// Manages the Channel-based input queue and serializes bridge messages.
/// </summary>
public sealed class TerminalSession : IAsyncDisposable
{
    private readonly IRemoteSessionProvider _provider;
    private readonly Channel<byte[]> _inputChannel;
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource<HostKeyVerdict>? _hostKeyTcs;
    private Task? _sessionTask;

    public string SessionId { get; }
    public CoreWebView2? WebView { get; private set; }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TerminalSession(string sessionId, IRemoteSessionProvider provider)
    {
        SessionId = sessionId;
        _provider = provider;
        _inputChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

        _provider.HostKeyConfirmation = HandleHostKeyAsync;
    }

    public void AttachWebView(CoreWebView2 webView)
    {
        WebView = webView;
        webView.WebMessageReceived += OnWebMessageReceived;
    }

    public void Start(SessionRequest request, PlaintextCredential credential)
    {
        _sessionTask = RunAsync(request, credential);
    }

    public void SendInput(byte[] data)
    {
        _inputChannel.Writer.TryWrite(data);
    }

    public async Task ResizeAsync(int cols, int rows)
    {
        await _provider.ResizeAsync(cols, rows, _cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _inputChannel.Writer.TryComplete();
        if (WebView is not null) WebView.WebMessageReceived -= OnWebMessageReceived;
        if (_sessionTask is not null) await _sessionTask.ConfigureAwait(false);
        await _provider.DisposeAsync();
        _cts.Dispose();
    }

    // -------------------------------------------------------------------------

    private async Task RunAsync(SessionRequest request, PlaintextCredential credential)
    {
        try
        {
            await _provider.ConnectAsync(
                request,
                credential,
                ReadInput(),
                SendOutput,
                _cts.Token);

            PostMessage(new DisconnectedMessage("Sessão encerrada normalmente."));
        }
        catch (OperationCanceledException)
        {
            PostMessage(new DisconnectedMessage("Sessão encerrada pelo usuário."));
        }
        catch (Exception ex)
        {
            PostMessage(new ErrorMessage(ex.Message));
        }
        finally
        {
            credential.Dispose();
        }
    }

    private async IAsyncEnumerable<byte[]> ReadInput(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in _inputChannel.Reader.ReadAllAsync(_cts.Token))
            yield return chunk;
    }

    private Task SendOutput(byte[] data)
    {
        PostMessage(new DataMessage(Convert.ToBase64String(data)));
        return Task.CompletedTask;
    }

    private async Task<HostKeyVerdict> HandleHostKeyAsync(HostKeyInfo info)
    {
        _hostKeyTcs = new TaskCompletionSource<HostKeyVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);

        PostMessage(new HostKeyPromptMsg(
            Host: info.Host,
            Fingerprint: info.FingerprintSha256,
            KeyType: info.KeyType,
            IsKnown: info.IsKnown,
            HasChanged: info.HasChanged));

        return await _hostKeyTcs.Task;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        BridgeMessage? msg;
        try { msg = JsonSerializer.Deserialize<BridgeMessage>(e.WebMessageAsJson, _jsonOpts); }
        catch { return; }

        switch (msg)
        {
            case InputMessage { Payload: var b64 }:
                SendInput(Convert.FromBase64String(b64));
                break;

            case ResizeMessage { Cols: var cols, Rows: var rows }:
                _ = ResizeAsync(cols, rows);
                break;

            case HostKeyAcceptMsg:
                _hostKeyTcs?.TrySetResult(HostKeyVerdict.Accepted);
                break;

            case HostKeyRejectMsg:
                _hostKeyTcs?.TrySetResult(HostKeyVerdict.RejectedByUser);
                break;
        }
    }

    private void PostMessage(BridgeMessage msg)
    {
        if (WebView is null) return;
        var json = JsonSerializer.Serialize(msg, msg.GetType(), _jsonOpts);
        WebView.PostWebMessageAsJson(json);
    }
}
