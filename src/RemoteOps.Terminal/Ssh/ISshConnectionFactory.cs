namespace RemoteOps.Terminal.Ssh;

/// <summary>Opções de conexão SSH: senha OU chave privada, mais o perfil de algoritmos.</summary>
internal sealed record SshConnectionOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Username { get; init; }

    /// <summary>Senha materializada de VaultSecret.RevealString() (auth por senha). Ver ADR-009 §FIX-3.</summary>
    public string? Password { get; init; }

    /// <summary>Bytes UTF-8 da chave privada PEM/OpenSSH (auth por chave). Zerar após uso.</summary>
    public byte[]? PrivateKeyUtf8 { get; init; }

    /// <summary>Passphrase da chave (quando houver).</summary>
    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>Perfil de segurança SSH ("auto" | "strict"); null = auto.</summary>
    public string? AlgorithmProfile { get; init; }
}

/// <summary>
/// Fábrica de conexões SSH. Internal para permitir substituição em testes
/// sem expor detalhes de SSH.NET na API pública do módulo.
/// </summary>
internal interface ISshConnectionFactory
{
    ISshConnection Create(SshConnectionOptions options);
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
