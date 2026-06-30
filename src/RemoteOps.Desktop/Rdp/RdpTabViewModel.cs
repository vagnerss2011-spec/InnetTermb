using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Rdp;

namespace RemoteOps.Desktop.Rdp;

/// <summary>
/// ViewModel de uma aba RDP. Espelha o ciclo de vida de TerminalTabViewModel
/// (CAS idle→connecting→connected, CloseAsync), mas a "conexão" real (MSTSCAX
/// Connect()) é disparada pela View — esta classe só prepara a config e expõe
/// a senha sob demanda, sem nunca retê-la em campo.
/// </summary>
public sealed class RdpTabViewModel : SessionTabViewModel
{
    private readonly IRdpSessionProvider _provider;
    private readonly IRdpCredentialResolver _credentialResolver;
    private readonly SessionRequest _baseRequest;

    private SessionHandle? _handle;
    // 0 = idle, 1 = connecting/prepared, 2 = connected (ActiveX OnConnected)
    private int _connectionState;

    private readonly object _prepareLock = new();
    private Task<RdpConnectionConfig>? _prepareTask;

    public RdpTabViewModel(
        string id,
        string title,
        string protocol,
        IRdpSessionProvider provider,
        IRdpCredentialResolver credentialResolver,
        SessionRequest baseRequest)
        : base(id, title, protocol)
    {
        _provider = provider;
        _credentialResolver = credentialResolver;
        _baseRequest = baseRequest;
    }

    public bool IsConnected => Volatile.Read(ref _connectionState) == 2;

    public RdpConnectionConfig? ConnectionConfig { get; private set; }

    /// <summary>Disparado quando o ActiveX reporta desconexão/erro. View repassa o motivo.</summary>
    public event Action<string>? ConnectFailed;

    /// <summary>
    /// Resolve endpoint/usuário e audita início. Chamado pela View ao carregar o
    /// WindowsFormsHost, ANTES de aplicar Server/UserName no controle MSTSCAX.
    /// </summary>
    public Task<RdpConnectionConfig> PrepareAsync(CancellationToken ct = default)
    {
        lock (_prepareLock)
        {
            // Uma tentativa anterior que já falhou/foi cancelada não deve ser
            // reaproveitada: descarta o cache para permitir nova tentativa.
            // (Não é seguro limpar o cache de dentro de PrepareCoreAsync: se
            // o provider falhar de forma síncrona, o catch rodaria antes da
            // atribuição abaixo terminar e o null seria sobrescrito pelo
            // próprio Task com falha.)
            if (_prepareTask is { IsFaulted: true } or { IsCanceled: true })
                _prepareTask = null;

            _prepareTask ??= PrepareCoreAsync(ct);
            return _prepareTask;
        }
    }

    private async Task<RdpConnectionConfig> PrepareCoreAsync(CancellationToken ct)
    {
        Interlocked.CompareExchange(ref _connectionState, 1, 0);
        try
        {
            _handle = await _provider.OpenAsync(_baseRequest, ct);
            ConnectionConfig = _provider.GetConnectionConfig(_baseRequest.SessionId);
            return ConnectionConfig;
        }
        catch
        {
            Interlocked.Exchange(ref _connectionState, 0);
            throw;
        }
    }

    /// <summary>
    /// Resolve a senha do vault sob demanda. Chame imediatamente antes de
    /// AdvancedSettings.ClearTextPassword e descarte a string assim que usada
    /// (mitigação ADR-009 §FIX-3 — a senha nunca fica em campo deste ViewModel).
    /// </summary>
    public Task<string?> ResolvePasswordAsync(CancellationToken ct = default) =>
        _credentialResolver.ResolvePasswordAsync(_baseRequest.CredentialRefId, ct);

    /// <summary>Chamado pela View quando o ActiveX dispara OnConnected/OnLoginComplete.</summary>
    public void MarkConnected() => Interlocked.Exchange(ref _connectionState, 2);

    /// <summary>Chamado pela View quando o ActiveX dispara OnDisconnected ou erro.</summary>
    public void MarkDisconnected(string? reason)
    {
        Interlocked.Exchange(ref _connectionState, 0);
        // Invalida o cache de preparo: uma reconexão subsequente (PrepareAsync)
        // deve reabrir a sessão de verdade, não reaproveitar a config antiga.
        lock (_prepareLock)
            _prepareTask = null;
        if (reason != null) ConnectFailed?.Invoke(reason);
    }

    /// <summary>Encerra a sessão. Chamado ao fechar a aba.</summary>
    public async Task CloseAsync()
    {
        if (_handle == null) return;

        await _provider.CloseAsync(_handle, CancellationToken.None);
        _handle = null;
        Interlocked.Exchange(ref _connectionState, 0);
        // Idem MarkDisconnected: um PrepareAsync futuro (reabrir a aba) precisa
        // de uma nova chamada a OpenAsync, não da Task em cache da sessão fechada.
        lock (_prepareLock)
            _prepareTask = null;
    }
}
