using System.IO;
using System.IO.Pipelines;
using RemoteOps.Terminal.Telnet;

namespace RemoteOps.UnitTests.Terminal.Fakes;

internal sealed class FakeTelnetConnectionFactory : ITelnetConnectionFactory
{
    public List<FakeTelnetConnection> Created { get; } = [];

    public ITelnetConnection Create(string host, int port)
    {
        var conn = new FakeTelnetConnection();
        Created.Add(conn);
        return conn;
    }
}

internal sealed class FakeTelnetConnection : ITelnetConnection
{
    private readonly FakeBidirectionalStream _stream = new();

    public bool ConnectCalled { get; private set; }
    public bool Disposed { get; private set; }
    public bool IsConnected => ConnectCalled && !Disposed;

    public Task ConnectAsync(CancellationToken ct)
    {
        ConnectCalled = true;
        return Task.CompletedTask;
    }

    public Stream RawStream => ConnectCalled
        ? _stream
        : throw new InvalidOperationException("Não conectado.");

    /// <summary>Stream para o teste injetar dados que o provider vai ler (server → client).</summary>
    public Stream InjectStream => _stream.InjectStream;

    /// <summary>Dados escritos pelo provider de volta ao servidor (client → server).</summary>
    public IReadOnlyList<byte[]> WrittenToServer => _stream.WrittenData;

    public async ValueTask DisposeAsync()
    {
        Disposed = true;
        await _stream.DisposeAsync();
    }
}

/// <summary>
/// Stream bidirecional para testes:
/// - Leitura: dados injetados via <see cref="InjectStream"/> (pipe reader).
/// - Escrita: capturada em <see cref="WrittenData"/> (não envia nada de verdade).
/// </summary>
internal sealed class FakeBidirectionalStream : Stream
{
    private readonly Pipe _readPipe = new();

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Test injects server-to-client bytes here.</summary>
    public Stream InjectStream => _readPipe.Writer.AsStream();

    /// <summary>Bytes written by the provider back to the server.</summary>
    public List<byte[]> WrittenData { get; } = [];

    // ── Read (server → client, from pipe) ────────────────────────────────────

    public override int Read(byte[] buffer, int offset, int count)
        => _readPipe.Reader.AsStream().Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => _readPipe.Reader.AsStream().ReadAsync(buffer, offset, count, ct);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => _readPipe.Reader.AsStream().ReadAsync(buffer, ct);

    // ── Write (client → server, captured) ────────────────────────────────────

    public override void Write(byte[] buffer, int offset, int count)
        => WrittenData.Add(buffer.Skip(offset).Take(count).ToArray());

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct = default)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        WrittenData.Add(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _readPipe.Reader.Complete();
        base.Dispose(disposing);
    }
}
