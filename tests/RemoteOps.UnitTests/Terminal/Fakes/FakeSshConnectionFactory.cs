using System.IO;
using System.IO.Pipelines;
using RemoteOps.Terminal.Ssh;

namespace RemoteOps.UnitTests.Terminal.Fakes;

/// <summary>
/// Fábrica de conexões SSH fake para testes sem rede real.
/// Permite configurar fingerprint retornada e se a conexão deve ser aceita ou rejeitada.
/// </summary>
internal sealed class FakeSshConnectionFactory : ISshConnectionFactory
{
    public string SimulatedFingerprint { get; set; } = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899";

    /// <summary>
    /// Controla o que acontece quando HostKeyValidator é chamado.
    /// Se null, usa o resultado do validator. Se não null, retorna esse valor
    /// independente do validator (simula uma segunda conexão já confiada).
    /// </summary>
    public bool? ForceValidatorResult { get; set; }

    /// <summary>Se setada, cada conexão fake lança esta exceção no Connect() (simula auth/timeout reais).</summary>
    public Exception? SimulatedConnectException { get; set; }

    public List<FakeSshConnection> Created { get; } = [];

    public SshConnectionOptions? LastOptions { get; private set; }

    /// <summary>Cópia da chave no momento do Create — o provider zera o buffer original após o connect.</summary>
    public byte[]? LastPrivateKeySnapshot { get; private set; }

    public ISshConnection Create(SshConnectionOptions options)
    {
        LastOptions = options;
        LastPrivateKeySnapshot = options.PrivateKeyUtf8?.ToArray();
        var conn = new FakeSshConnection(SimulatedFingerprint, ForceValidatorResult, SimulatedConnectException);
        Created.Add(conn);
        return conn;
    }
}

internal sealed class FakeSshConnection : ISshConnection
{
    private readonly string _fingerprint;
    private readonly bool? _forceResult;
    private readonly Exception? _simulatedException;
    private FakeSshShell? _shell;

    public Func<string, bool>? HostKeyValidator { get; set; }

    public bool ConnectCalled { get; private set; }
    public bool DisposeCalled { get; private set; }

    public FakeSshConnection(string fingerprint, bool? forceResult = null, Exception? simulatedException = null)
    {
        _fingerprint = fingerprint;
        _forceResult = forceResult;
        _simulatedException = simulatedException;
    }

    public void Connect()
    {
        ConnectCalled = true;
        if (HostKeyValidator is null) throw new InvalidOperationException("HostKeyValidator não configurado.");

        bool trust = _forceResult ?? HostKeyValidator(_fingerprint);
        if (!trust) throw new Exception("Host key não confiada (simulado).");

        if (_simulatedException is not null) throw _simulatedException;
    }

    public ISshShell OpenShell(string termType, int cols, int rows)
    {
        _shell = new FakeSshShell();
        return _shell;
    }

    public FakeSshShell? Shell => _shell;

    public void Dispose() => DisposeCalled = true;
}

internal sealed class FakeSshShell : ISshShell
{
    private readonly Pipe _pipe = new();
    private readonly CaptureDuplexStream _dataStream;

    public FakeSshShell() => _dataStream = new CaptureDuplexStream(_pipe.Reader.AsStream());

    /// <summary>
    /// Stream bidirecional: LEITURA vem do pipe (o teste injeta via <see cref="InjectStream"/>);
    /// ESCRITA é capturada em ordem (o provider escreve aqui). Espelha a ShellStream real, que é
    /// duplex — o provider lê a saída do equipamento e escreve as teclas no MESMO stream.
    /// </summary>
    public Stream DataStream => _dataStream;

    /// <summary>Stream para o teste escrever dados que o provider vai ler.</summary>
    public Stream InjectStream => _pipe.Writer.AsStream();

    /// <summary>Bytes que o provider ESCREVEU, na ordem exata (teclas → equipamento).</summary>
    public byte[] WrittenBytes => _dataStream.WrittenSnapshot();

    public (uint Cols, uint Rows)? LastResize { get; private set; }

    public void Resize(uint cols, uint rows) => LastResize = (cols, rows);

    public void Dispose() => _pipe.Reader.Complete();
}

/// <summary>
/// Stream fake duplex para testes: delega a LEITURA a um stream interno (o pipe de injeção) e
/// CAPTURA toda escrita num buffer, preservando a ordem dos bytes. Serve para verificar que o
/// provider envia as teclas ao equipamento na ordem correta (FIFO), sem corrida entre teclas.
/// </summary>
internal sealed class CaptureDuplexStream(Stream readSide) : Stream
{
    private readonly Stream _readSide = readSide;
    private readonly object _sync = new();
    private readonly List<byte> _written = [];

    public byte[] WrittenSnapshot()
    {
        lock (_sync)
        {
            return [.. _written];
        }
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;

    public override int Read(byte[] buffer, int offset, int count) => _readSide.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _readSide.ReadAsync(buffer, offset, count, ct);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        _readSide.ReadAsync(buffer, ct);

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            for (int i = 0; i < count; i++)
            {
                _written.Add(buffer[offset + i]);
            }
        }
    }

    public override void Flush() { }

    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
