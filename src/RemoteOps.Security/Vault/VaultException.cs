namespace RemoteOps.Security.Vault;

/// <summary>
/// Erro de domínio do cofre (envelope ausente, revogado, etc.).
/// Mensagens nunca incluem segredo nem material criptográfico.
/// </summary>
public sealed class VaultException : Exception
{
    public VaultException(string message) : base(message)
    {
    }

    public VaultException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
