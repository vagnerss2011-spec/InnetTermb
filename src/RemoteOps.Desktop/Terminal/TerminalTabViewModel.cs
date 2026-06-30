using RemoteOps.Contracts.Sessions;
using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Terminal;

/// <summary>
/// ViewModel de uma aba de terminal SSH ou Telnet.
/// Gerencia o ciclo de vida da sessão e o pump de saída independentemente da View.
/// A View pode se re-anexar sem matar a sessão (tolerante a re-criação de DataTemplate).
/// </summary>
public sealed class TerminalTabViewModel : ViewModels.SessionTabViewModel
{
    private readonly ITerminalSessionProvider _provider;
    private readonly SessionRequest _baseRequest;

    private CancellationTokenSource? _cts;
    private SessionHandle? _handle;
    // 0 = idle, 1 = connecting, 2 = connected
    private int _connectionState;

    public TerminalTabViewModel(
        string id,
        string title,
        string protocol,
        ITerminalSessionProvider provider,
        SessionRequest baseRequest)
        : base(id, title, protocol)
    {
        _provider = provider;
        _baseRequest = baseRequest;
    }

    public bool IsConnected => Volatile.Read(ref _connectionState) == 2;

    /// <summary>
    /// Disparado pelo pump de leitura na thread do pump. A View despacha para o Dispatcher.
    /// Nunca contém o conteúdo criptografado do terminal em logs — apenas bytes brutos via evento.
    /// </summary>
    public event Action<ReadOnlyMemory<byte>>? OutputReceived;

    /// <summary>
    /// Abre a sessão e inicia o pump de saída. Chamado pela View após WebView2 estar pronto.
    /// </summary>
    public async Task ConnectAsync(int cols, int rows, CancellationToken ct = default)
    {
        // Prevent double-connect: idle(0) → connecting(1) must be an atomic CAS
        if (Interlocked.CompareExchange(ref _connectionState, 1, 0) != 0) return;

        try
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // SessionRequest é uma classe (não record) — recria copiando os campos
            // e define o TerminalOptions com as dimensões atuais do xterm.
            var request = new SessionRequest
            {
                SessionId = _baseRequest.SessionId,
                Protocol = _baseRequest.Protocol,
                EndpointId = _baseRequest.EndpointId,
                CredentialRefId = _baseRequest.CredentialRefId,
                PreferIpv6 = _baseRequest.PreferIpv6,
                Terminal = new TerminalOptions { Cols = cols, Rows = rows },
            };

            _handle = await _provider.OpenAsync(request, _cts.Token);
            Interlocked.Exchange(ref _connectionState, 2);
            _ = PumpOutputAsync(_cts.Token);
        }
        catch
        {
            Interlocked.Exchange(ref _connectionState, 0);
            throw;
        }
    }

    private async Task PumpOutputAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _provider.ReadAsync(_handle!, ct).ConfigureAwait(false))
            {
                OutputReceived?.Invoke(chunk);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Terminal] Pump error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _connectionState, 0);
        }
    }

    /// <summary>Envia entrada do usuário (do xterm.js) para a sessão remota.</summary>
    public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_handle == null || !IsConnected) return Task.CompletedTask;
        return _provider.WriteAsync(_handle, data, ct);
    }

    /// <summary>Notifica o PTY remoto de redimensionamento (via FitAddon/resize).</summary>
    public Task ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        if (_handle == null || !IsConnected) return Task.CompletedTask;
        return _provider.ResizeAsync(_handle, cols, rows, ct);
    }

    /// <summary>
    /// Encerra a sessão e cancela o pump. Chamado ao fechar a aba.
    /// </summary>
    public async Task CloseAsync()
    {
        if (_handle == null) return;

        if (_cts != null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }

        await _provider.CloseAsync(_handle, CancellationToken.None);
        _handle = null;
        Interlocked.Exchange(ref _connectionState, 0);
    }
}
