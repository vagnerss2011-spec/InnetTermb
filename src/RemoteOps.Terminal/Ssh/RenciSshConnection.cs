using System.IO;
using Renci.SshNet;

namespace RemoteOps.Terminal.Ssh;

/// <summary>
/// Implementação real de ISshConnection/ISshShell usando Renci.SshNet (SSH.NET 2024.x).
/// </summary>
internal sealed class RenciSshConnectionFactory : ISshConnectionFactory
{
    public ISshConnection Create(SshConnectionOptions options)
    {
        AuthenticationMethod authMethod;
        if (options.PrivateKeyUtf8 is { } keyBytes)
        {
            try
            {
                using var ms = new MemoryStream(keyBytes);
                var keyFile = string.IsNullOrEmpty(options.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(ms)
                    : new PrivateKeyFile(ms, options.PrivateKeyPassphrase);
                authMethod = new PrivateKeyAuthenticationMethod(options.Username, keyFile);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Chave privada inválida ou passphrase incorreta.", ex);
            }
        }
        else
        {
            authMethod = new PasswordAuthenticationMethod(options.Username, options.Password ?? string.Empty);
        }

        var info = new ConnectionInfo(options.Host, options.Port, options.Username, authMethod)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        SshAlgorithmPolicy.Apply(info, options.AlgorithmProfile);

        // Keepalive: envia keep-alive a cada 30 s para manter sessões ociosas vivas.
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
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

    // Limitação conhecida: SSH.NET 2024.2.0 não expõe window-change/resize público
    // no ShellStream. O resize do PTY remoto fica como TODO (enviar window-change via
    // canal quando a API permitir). No-op para não quebrar o fluxo de UI. Ver ADR-009.
    public void Resize(uint cols, uint rows)
    {
        // no-op intencional (ver comentário acima).
    }

    public void Dispose() => _shell.Dispose();
}
