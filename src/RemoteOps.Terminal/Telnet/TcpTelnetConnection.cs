using System.Net.Sockets;

namespace RemoteOps.Terminal.Telnet;

internal sealed class TcpTelnetConnectionFactory : ITelnetConnectionFactory
{
    public ITelnetConnection Create(string host, int port) => new TcpTelnetConnection(host, port);
}

internal sealed class TcpTelnetConnection : ITelnetConnection
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public TcpTelnetConnection(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public bool IsConnected => _tcp?.Connected ?? false;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_host, _port, ct);
        _stream = _tcp.GetStream();
    }

    public Stream RawStream =>
        _stream ?? throw new InvalidOperationException("Não conectado. Chame ConnectAsync primeiro.");

    public async ValueTask DisposeAsync()
    {
        if (_stream != null) await _stream.DisposeAsync();
        _tcp?.Dispose();
    }
}
