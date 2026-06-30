using Renci.SshNet;

namespace RemoteOps.Terminal.Ssh;

/// <summary>
/// Implementação real de ISshConnection/ISshShell usando Renci.SshNet (SSH.NET 2024.x).
/// </summary>
internal sealed class RenciSshConnectionFactory : ISshConnectionFactory
{
    public ISshConnection Create(string host, int port, string username, string password)
    {
        var authMethod = new PasswordAuthenticationMethod(username, password);
        var info = new ConnectionInfo(host, port, username, authMethod)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        // Keepalive: envia keep-alive a cada 30 s para manter sessões ociosas vivas.
        var client = new SshClient(info);
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        return new RenciSshConnection(client);
    }
}

internal sealed class RenciSshConnection : ISshConnection
{
    private readonly SshClient _client;

    public Func<string, bool>? HostKeyValidator { get; set; }

    public RenciSshConnection(SshClient client)
    {
        _client = client;
        _client.HostKeyReceived += OnHostKeyReceived;
    }

    private void OnHostKeyReceived(object? sender, Renci.SshNet.Common.HostKeyEventArgs e)
    {
        if (HostKeyValidator is null)
        {
            e.CanTrust = false;
            return;
        }
        var fp = Convert.ToHexString(e.FingerPrint).ToLowerInvariant();
        // Callback síncrono — FIX 1: sem await/GetAwaiter().GetResult() aqui.
        e.CanTrust = HostKeyValidator(fp);
    }

    public void Connect() => _client.Connect();

    public ISshShell OpenShell(string termType, int cols, int rows)
    {
        var shell = _client.CreateShellStream(termType, (uint)cols, (uint)rows, 800, 600, 4096);
        return new RenciSshShell(shell);
    }

    public void Dispose()
    {
        _client.HostKeyReceived -= OnHostKeyReceived;
        _client.Dispose();
    }
}

internal sealed class RenciSshShell : ISshShell
{
    private readonly ShellStream _shell;

    public RenciSshShell(ShellStream shell) => _shell = shell;

    public Stream DataStream => _shell;

    public void Resize(uint cols, uint rows) =>
        _shell.SendWindowChangeRequest(cols, rows, 0, 0);

    public void Dispose() => _shell.Dispose();
}
