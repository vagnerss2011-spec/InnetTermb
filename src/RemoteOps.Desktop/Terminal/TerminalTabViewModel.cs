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
    private readonly Func<bool, Task>? _persistBackspace;

    private CancellationTokenSource? _cts;
    private SessionHandle? _handle;
    // 0 = idle, 1 = connecting, 2 = connected
    private int _connectionState;
    // 0 = Backspace envia DEL (0x7F, padrão); 1 = envia BS (0x08, Ctrl+H — p/ OLT Huawei etc.)
    private int _backspaceModeIndex;

    public TerminalTabViewModel(
        string id,
        string title,
        string protocol,
        ITerminalSessionProvider provider,
        SessionRequest baseRequest,
        bool backspaceUsesControlH = false,
        Func<bool, Task>? persistBackspace = null)
        : base(id, title, protocol)
    {
        _provider = provider;
        _baseRequest = baseRequest;
        _backspaceModeIndex = backspaceUsesControlH ? 1 : 0;
        _persistBackspace = persistBackspace;
    }

    /// <summary>
    /// Modo do Backspace, ligado ao seletor da aba: 0 = Padrão (DEL 0x7F), 1 = Ctrl+H (BS 0x08).
    /// Mudar aplica NA HORA (a View lê <see cref="BackspaceUsesControlH"/> na próxima tecla) e
    /// persiste no host via o callback recebido do launcher (fica lembrado para este equipamento).
    /// </summary>
    public int BackspaceModeIndex
    {
        get => _backspaceModeIndex;
        set
        {
            if (_backspaceModeIndex == value)
            {
                return;
            }
            _backspaceModeIndex = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(BackspaceUsesControlH));
            _ = PersistBackspaceSafeAsync(value == 1);
        }
    }

    /// <summary>true = Backspace envia BS (0x08); false = DEL (0x7F). Lido pela View no KeyDown.</summary>
    public bool BackspaceUsesControlH => _backspaceModeIndex == 1;

    private async Task PersistBackspaceSafeAsync(bool usesControlH)
    {
        if (_persistBackspace is null)
        {
            return;
        }
        try
        {
            await _persistBackspace(usesControlH);
        }
        catch
        {
            // Persistir a preferência é best-effort: se falhar, o modo já vale nesta sessão.
        }
    }

    public bool IsConnected => Volatile.Read(ref _connectionState) == 2;

    /// <summary>
    /// Disparado pelo pump de leitura na thread do pump. A View despacha para o Dispatcher.
    /// Nunca contém o conteúdo criptografado do terminal em logs — apenas bytes brutos via evento.
    /// </summary>
    public event Action<ReadOnlyMemory<byte>>? OutputReceived;

    /// <summary>
    /// Disparado quando a sessão TERMINA por conta própria (o servidor fechou / o link caiu),
    /// NÃO em fechamento deliberado da aba. null = fim limpo (ex.: logout/exit); mensagem = erro
    /// (queda de rede). A View escreve "[Sessão encerrada]" / "[Conexão perdida: …]" pra não deixar
    /// o operador achando que a sessão está viva enquanto as teclas somem em silêncio.
    /// </summary>
    public event Action<string?>? SessionEnded;

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
        string? endReason = null;
        bool report = true;
        try
        {
            await foreach (var chunk in _provider.ReadAsync(_handle!, ct).ConfigureAwait(false))
            {
                OutputReceived?.Invoke(chunk);
            }
            // Fim do foreach SEM exceção = servidor fechou o stream (logout/exit) — fim limpo.
        }
        catch (OperationCanceledException)
        {
            report = false; // fechamento DELIBERADO (CloseAsync cancelou) — não avisa o operador
        }
        catch (Exception ex)
        {
            // O provider propaga o erro de rede completando o canal com exceção (TryComplete(ex)),
            // que reaparece aqui. Avisa o operador em vez de fingir um fim limpo.
            endReason = ex.Message;
            System.Diagnostics.Debug.WriteLine($"[Terminal] Pump error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _connectionState, 0);
            if (report)
            {
                SessionEnded?.Invoke(endReason);
            }
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
        // Cancela a conexão em voo MESMO se _handle ainda for null (fechar a aba enquanto conecta):
        // o _cts.Token foi passado ao OpenAsync, então cancelar aborta o connect e o catch de
        // ConnectAsync reseta o estado. Sem isto, fechar durante um connect lento (o CAS já passou
        // pra "connecting" mas _handle só é setado DEPOIS do OpenAsync) caía no early-return antigo
        // e a sessão completava "sem dono" — conexão viva no equipamento + threads vazando, esgotando
        // slots vty em MikroTik/OLT. Idempotente (chamável 2x com segurança).
        if (_cts != null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }

        if (_handle != null)
        {
            await _provider.CloseAsync(_handle, CancellationToken.None);
            _handle = null;
        }

        Interlocked.Exchange(ref _connectionState, 0);
    }
}
