using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Terminal;

namespace RemoteOps.UnitTests.Desktop.Fakes;

/// <summary>
/// Fake ITerminalSessionProvider para testes unitários de TerminalTabViewModel.
/// Controla o canal de saída via EnqueueOutput / CompleteOutput.
/// </summary>
internal sealed class FakeTerminalSessionProvider : ITerminalSessionProvider
{
    private readonly Channel<ReadOnlyMemory<byte>> _output =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    public string Protocol => "ssh";

    public List<SessionRequest> OpenedRequests { get; } = [];
    public List<ReadOnlyMemory<byte>> WrittenData { get; } = [];
    public List<(int Cols, int Rows)> ResizeCalls { get; } = [];
    public int CloseCount { get; private set; }

    public bool ShouldThrowOnOpen { get; set; }

    public Task<SessionHandle> OpenAsync(SessionRequest request, CancellationToken ct)
    {
        if (ShouldThrowOnOpen)
            throw new InvalidOperationException("Fake provider: open failed");

        OpenedRequests.Add(request);
        return Task.FromResult(new SessionHandle
        {
            SessionId = request.SessionId,
            Protocol = request.Protocol,
            EndpointId = request.EndpointId,
            OpenedAt = DateTimeOffset.UtcNow,
            IsOpen = true,
        });
    }

    public Task WriteAsync(SessionHandle handle, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        WrittenData.Add(data);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(
        SessionHandle handle,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in _output.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return chunk;
    }

    public Task ResizeAsync(SessionHandle handle, int cols, int rows, CancellationToken ct = default)
    {
        ResizeCalls.Add((cols, rows));
        return Task.CompletedTask;
    }

    public Task CloseAsync(SessionHandle handle, CancellationToken ct)
    {
        CloseCount++;
        return Task.CompletedTask;
    }

    /// <summary>Empurra um chunk de bytes para o pump de saída.</summary>
    public void EnqueueOutput(byte[] bytes) =>
        _output.Writer.TryWrite(bytes);

    /// <summary>Sinaliza fim de stream — o pump de saída retorna normalmente.</summary>
    public void CompleteOutput() =>
        _output.Writer.TryComplete();
}
