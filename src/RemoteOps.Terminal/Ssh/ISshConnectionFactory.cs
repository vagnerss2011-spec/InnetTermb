namespace RemoteOps.Terminal.Ssh;

/// <summary>
/// Fábrica de conexões SSH. Internal para permitir substituição em testes
/// sem expor detalhes de SSH.NET na API pública do módulo.
/// </summary>
internal interface ISshConnectionFactory
{
    /// <param name="password">
    /// String materializada de VaultSecret.RevealString(). Limitação de Renci.SshNet
    /// (exige string na autenticação por senha). Minimizar escopo; ver ADR-009 §FIX-3.
    /// </param>
    ISshConnection Create(string host, int port, string username, string password);
}

internal interface ISshConnection : IDisposable
{
    /// <summary>
    /// Validador chamado sincronicamente durante <see cref="Connect"/> quando a host key
    /// chega do servidor. Retorne <c>true</c> para confiar, <c>false</c> para rejeitar.
    /// NUNCA execute código assíncrono ou bloqueante aqui (FIX 1).
    /// </summary>
    Func<string, bool>? HostKeyValidator { get; set; }

    void Connect();

    ISshShell OpenShell(string termType, int cols, int rows);
}

internal interface ISshShell : IDisposable
{
    Stream DataStream { get; }
    void Resize(uint cols, uint rows);
}
