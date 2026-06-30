namespace RemoteOps.Terminal.Telnet;

/// <summary>
/// Fábrica de conexões Telnet. Internal para permitir substituição em testes.
/// </summary>
internal interface ITelnetConnectionFactory
{
    ITelnetConnection Create(string host, int port);
}

internal interface ITelnetConnection : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Stream bruto de bytes (inclui sequências IAC não processadas).</summary>
    Stream RawStream { get; }

    bool IsConnected { get; }
}
