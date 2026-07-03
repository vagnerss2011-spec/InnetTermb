namespace RemoteOps.Contracts.Assets;

/// <summary>Valores canônicos de <see cref="CredentialRef.Type"/>. O Type entra no AAD do
/// AES-GCM do vault — divergência de grafia entre camadas quebra o decrypt.</summary>
public static class CredentialTypes
{
    public const string Password = "password"; // pragma: allowlist secret (discriminador de tipo, não uma senha)
    public const string PrivateKey = "privateKey";
    public const string PrivateKeyPassphrase = "privateKeyPassphrase";
}
