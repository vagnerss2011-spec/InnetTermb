using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Security.Vault;

namespace RemoteOps.Terminal.Ssh;

/// <summary>
/// Adaptador SSH para ITerminalSessionProvider. Consolida SSH em RemoteOps.Terminal
/// (Opção A do layout — ver ADR-009 §Decisão de layout).
/// </summary>
public sealed class SshSessionProvider : ITerminalSessionProvider
{
    private readonly IEndpointResolver _endpointResolver;
    private readonly ICredentialRefResolver _credentialRefResolver;
    private readonly IVault _vault;
    private readonly ITerminalSecurityContext _securityContext;
    private readonly IHostKeyConfirmation _hostKeyConfirmation;
    private readonly ITerminalAuditSink _auditSink;
    private readonly ISshConnectionFactory _factory;
    private readonly HostKeyStore _hostKeyStore;
    private readonly ConcurrentDictionary<string, SshSessionState> _sessions = new();

    public string Protocol => RemoteProtocol.Ssh;

    public SshSessionProvider(
        IEndpointResolver endpointResolver,
        ICredentialRefResolver credentialRefResolver,
        IVault vault,
        ITerminalSecurityContext securityContext,
        IHostKeyConfirmation hostKeyConfirmation,
        ITerminalAuditSink auditSink)
        : this(endpointResolver, credentialRefResolver, vault, securityContext,
               hostKeyConfirmation, auditSink, factory: null)
    {
    }

    // Construtor de injeção da fábrica (test seam). Internal para não expor
    // ISshConnectionFactory na API pública; visível aos testes via InternalsVisibleTo.
    internal SshSessionProvider(
        IEndpointResolver endpointResolver,
        ICredentialRefResolver credentialRefResolver,
        IVault vault,
        ITerminalSecurityContext securityContext,
        IHostKeyConfirmation hostKeyConfirmation,
        ITerminalAuditSink auditSink,
        ISshConnectionFactory? factory,
        HostKeyStore? hostKeyStore = null)
    {
        _endpointResolver = endpointResolver;
        _credentialRefResolver = credentialRefResolver;
        _vault = vault;
        _securityContext = securityContext;
        _hostKeyConfirmation = hostKeyConfirmation;
        _auditSink = auditSink;
        _factory = factory ?? new RenciSshConnectionFactory();
        _hostKeyStore = hostKeyStore ?? new HostKeyStore();
    }

    public async Task<SessionHandle> OpenAsync(SessionRequest request, CancellationToken ct)
    {
        var endpoint = await _endpointResolver.ResolveAsync(request.EndpointId, ct);
        var credRef = await _credentialRefResolver.ResolveAsync(request.CredentialRefId, ct);

        string host = ResolveHost(endpoint, request.PreferIpv6);
        int port = endpoint.Port > 0 ? endpoint.Port : 22;
        string username = credRef.Metadata?.Username
            ?? throw new InvalidOperationException(
                $"CredentialRef '{request.CredentialRefId}' não tem username em Metadata.");
        string envelopeId = credRef.SecretEnvelopeId
            ?? throw new InvalidOperationException(
                $"CredentialRef '{request.CredentialRefId}' não tem SecretEnvelopeId.");

        int cols = request.Terminal?.Cols ?? 80;
        int rows = request.Terminal?.Rows ?? 24;
        string termType = "xterm-256color";
        string? algorithmProfile = endpoint.Profile?.SshAlgorithmProfile;

        var vaultCtx = new VaultAccessContext
        {
            ActorUserId = _securityContext.ActorUserId,
            DeviceId = _securityContext.DeviceId,
        };

        // Dispatch por tipo: credencial de chave usa PrivateKeyAuthenticationMethod (a chave
        // NUNCA vai como senha ao servidor); senão, senha como antes. VaultSecret zera o buffer
        // no using; a chave em byte[] é zerada após o connect. Ver ADR-009 §FIX-3.
        SshConnectionOptions options;
        if (credRef.Type == CredentialTypes.PrivateKey)
        {
            using var keySecret = await _vault.RetrieveAsync(envelopeId, vaultCtx, ct);
            byte[] keyBytes = keySecret.RevealUtf8().ToArray();
            string? passphrase = null;
            if (credRef.Metadata?.PassphraseEnvelopeId is { } ppId)
            {
                using var ppSecret = await _vault.RetrieveAsync(ppId, vaultCtx, ct);
                passphrase = ppSecret.RevealString();
            }
            options = new SshConnectionOptions
            {
                Host = host,
                Port = port,
                Username = username,
                PrivateKeyUtf8 = keyBytes,
                PrivateKeyPassphrase = passphrase,
                AlgorithmProfile = algorithmProfile,
            };
        }
        else
        {
            using var secret = await _vault.RetrieveAsync(envelopeId, vaultCtx, ct);
            options = new SshConnectionOptions
            {
                Host = host,
                Port = port,
                Username = username,
                Password = secret.RevealString(),
                AlgorithmProfile = algorithmProfile,
            };
        }

        var (connection, shell, channel, readerCts) =
            await ConnectWithTofuAsync(options, cols, rows, termType, request.SessionId, ct);

        // Zera a cópia gerenciada da chave privada assim que a conexão foi estabelecida.
        if (options.PrivateKeyUtf8 is { } usedKey)
        {
            Array.Clear(usedKey);
        }

        var state = new SshSessionState(connection, shell, channel, readerCts);
        _sessions[request.SessionId] = state;

        await _auditSink.EmitAsync(new TerminalAuditEvent
        {
            Action = TerminalActions.SessionOpened,
            SessionId = request.SessionId,
            Host = host,
            Protocol = RemoteProtocol.Ssh,
            UserId = _securityContext.ActorUserId,
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct);

        return new SessionHandle
        {
            SessionId = request.SessionId,
            Protocol = RemoteProtocol.Ssh,
            EndpointId = request.EndpointId,
            OpenedAt = DateTimeOffset.UtcNow,
            IsOpen = true,
        };
    }

    private async Task<(ISshConnection, ISshShell, Channel<ReadOnlyMemory<byte>>, CancellationTokenSource)>
        ConnectWithTofuAsync(
            SshConnectionOptions options,
            int cols, int rows, string termType, string sessionId, CancellationToken ct)
    {
        string host = options.Host;
        HostKeyRejectionReason? rejectionReason = null;
        string? capturedFp = null;

        ISshConnection? connection = null;
        try
        {
            connection = _factory.Create(options);

            // FIX 1: HostKeyValidator é callback síncrono — sem async/await/GetResult aqui.
            // Capturamos fingerprint e motivo; o fluxo assíncrono ocorre FORA do callback.
            connection.HostKeyValidator = fp =>
            {
                capturedFp = fp;
                if (_hostKeyStore.IsKnown(host, fp)) return true;
                rejectionReason = _hostKeyStore.HasAnyKey(host)
                    ? HostKeyRejectionReason.KeyChanged
                    : HostKeyRejectionReason.UnknownKey;
                return false;
            };

            Exception? firstConnectEx = null;
            try
            {
                await Task.Run(() => connection.Connect(), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                firstConnectEx = ex;
            }

            if (firstConnectEx != null)
            {
                if (!rejectionReason.HasValue || capturedFp is null)
                {
                    // Falha de rede/protocolo/auth não relacionada a host key — mensagem pt-BR
                    // acionável (senha errada, timeout, negociação) em vez de exceção crua.
                    throw new InvalidOperationException(
                        SshConnectionError.Describe(firstConnectEx, options.Host, options.Port), firstConnectEx);
                }

                // Host key não confiada — tratamento assíncrono fora do callback (FIX 1).
                bool isChanged = rejectionReason == HostKeyRejectionReason.KeyChanged;

                if (isChanged)
                {
                    // FIX 5: auditar substituição de host key ANTES de perguntar ao usuário.
                    await _auditSink.EmitAsync(new TerminalAuditEvent
                    {
                        Action = TerminalActions.HostKeyChanged,
                        SessionId = sessionId,
                        Host = host,
                        Protocol = RemoteProtocol.Ssh,
                        Fingerprint = capturedFp,
                        UserId = _securityContext.ActorUserId,
                        OccurredAt = DateTimeOffset.UtcNow,
                    }, ct);
                }

                bool trusted = await _hostKeyConfirmation.ConfirmAsync(host, capturedFp!, isChanged, ct);

                connection.Dispose();
                connection = null;

                if (!trusted)
                {
                    await _auditSink.EmitAsync(new TerminalAuditEvent
                    {
                        Action = TerminalActions.HostKeyRejected,
                        SessionId = sessionId,
                        Host = host,
                        Protocol = RemoteProtocol.Ssh,
                        Fingerprint = capturedFp,
                        UserId = _securityContext.ActorUserId,
                        OccurredAt = DateTimeOffset.UtcNow,
                    }, ct);
                    throw new InvalidOperationException(
                        $"Host key para '{host}' rejeitada pelo usuário. Conexão abortada.");
                }

                _hostKeyStore.Trust(host, capturedFp!);

                await _auditSink.EmitAsync(new TerminalAuditEvent
                {
                    Action = TerminalActions.HostKeyAccepted,
                    SessionId = sessionId,
                    Host = host,
                    Protocol = RemoteProtocol.Ssh,
                    Fingerprint = capturedFp,
                    UserId = _securityContext.ActorUserId,
                    OccurredAt = DateTimeOffset.UtcNow,
                }, ct);

                // Reconecta com key agora confiada no store. Aqui é onde a AUTENTICAÇÃO
                // acontece (host novo) — erro de senha vira mensagem pt-BR acionável.
                connection = _factory.Create(options);
                connection.HostKeyValidator = fp => _hostKeyStore.IsKnown(host, fp);
                try
                {
                    await Task.Run(() => connection.Connect(), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    throw new InvalidOperationException(
                        SshConnectionError.Describe(ex, options.Host, options.Port), ex);
                }
            }

            // connection é não-nulo aqui: ou primeira conexão OK, ou TOFU reassigned.
            var shell = connection!.OpenShell(termType, cols, rows);

            var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = false,
            });

            var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = PumpShellOutputAsync(shell.DataStream, channel.Writer, readerCts.Token);

            return (connection!, shell, channel, readerCts);
            // Transferência de ownership para o caller; catch NÃO deve dispor.
        }
        catch
        {
            connection?.Dispose();
            throw;
        }
    }

    private static async Task PumpShellOutputAsync(
        Stream dataStream, ChannelWriter<ReadOnlyMemory<byte>> writer, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await dataStream.ReadAsync(buffer, ct);
                if (read == 0) break;
                await writer.WriteAsync(buffer[..read].ToArray(), ct);
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
            Protocol = RemoteProtocol.Ssh,
            UserId = _securityContext.ActorUserId,
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    public Task WriteAsync(SessionHandle handle, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(handle.SessionId, out var state))
            throw new InvalidOperationException($"Sessão '{handle.SessionId}' não encontrada.");

        // Enfileira (síncrono e ordenado) e retorna na hora. A thread de UI chama isto em sequência,
        // então os bytes entram na fila na MESMA ordem em que foram digitados; uma tarefa dedicada em
        // SshSessionState drena a fila e escreve na ShellStream de forma síncrona e serializada.
        //
        // Por que NÃO usar WriteAsync/FlushAsync direto na ShellStream: no SSH.NET 2024.2.0 a
        // ShellStream não sobrescreve os métodos async, e o Stream base serializa ReadAsync/WriteAsync
        // no MESMO semáforo (_asyncActiveSemaphore, 1 permit). Como o pump de leitura fica
        // permanentemente parado em ReadAsync segurando esse semáforo (só retorna quando o equipamento
        // manda bytes), um WriteAsync jamais o adquiria — travava pra sempre, sem completar nem lançar.
        // Era o motivo REAL de "digitar não fazia nada". O caminho síncrono (Write/Flush) tem locks
        // internos próprios, separados de leitura, e não disputa o semáforo async do pump. Ver
        // SshSessionState.DrainWritesAsync.
        state.EnqueueWrite(data.ToArray());
        return Task.CompletedTask;
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

    public Task ResizeAsync(SessionHandle handle, int cols, int rows, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(handle.SessionId, out var state))
            throw new InvalidOperationException($"Sessão '{handle.SessionId}' não encontrada.");

        state.Shell.Resize((uint)cols, (uint)rows);
        return Task.CompletedTask;
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

internal sealed class SshSessionState : IAsyncDisposable
{
    private readonly ISshConnection _connection;
    private readonly CancellationTokenSource _readerCts;
    private readonly Channel<byte[]> _writeChannel;
    private readonly Task _writerTask;

    public ISshShell Shell { get; }
    public Channel<ReadOnlyMemory<byte>> OutputChannel { get; }

    public SshSessionState(
        ISshConnection connection,
        ISshShell shell,
        Channel<ReadOnlyMemory<byte>> outputChannel,
        CancellationTokenSource readerCts)
    {
        _connection = connection;
        Shell = shell;
        OutputChannel = outputChannel;
        _readerCts = readerCts;

        // Fila de escrita ORDENADA (um único leitor). Garante FIFO: os bytes vão pra ShellStream na
        // mesma ordem em que a UI os enfileirou, sem corrida entre teclas. A escrita real é síncrona
        // (Write/Flush) — ver comentário em SshSessionProvider.WriteAsync sobre o deadlock do async.
        _writeChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _writerTask = Task.Run(() => DrainWritesAsync(_readerCts.Token));
    }

    /// <summary>Enfileira bytes pra envio ao equipamento. Síncrono e ordenado; não bloqueia a UI.</summary>
    public void EnqueueWrite(byte[] bytes) => _writeChannel.Writer.TryWrite(bytes);

    // Drena a fila numa ÚNICA tarefa: escreve de forma serializada e síncrona na ShellStream.
    // Síncrono de propósito — o WriteAsync do Stream base disputa o mesmo semáforo do ReadAsync do
    // pump e trava (ver SshSessionProvider.WriteAsync). O Write/Flush síncrono usa locks próprios.
    private async Task DrainWritesAsync(CancellationToken ct)
    {
        try
        {
            await foreach (byte[] bytes in _writeChannel.Reader.ReadAllAsync(ct))
            {
                Stream stream = Shell.DataStream;
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* sessão caindo/stream fechado: escrita restante é descartada */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _readerCts.CancelAsync();
        _writeChannel.Writer.TryComplete();
        try { await _writerTask; } catch { /* já cancelado/encerrado */ }
        _readerCts.Dispose();
        Shell.Dispose();
        _connection.Dispose();
    }
}
