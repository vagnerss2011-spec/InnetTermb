namespace RemoteOps.Security.Vault;

/// <summary>Pedido de armazenamento de um novo segredo no cofre.</summary>
public sealed record VaultStoreRequest
{
    public required string WorkspaceId { get; init; }

    public required string CredentialId { get; init; }

    /// <summary>password | privateKey | secret | ...</summary>
    public string Type { get; init; } = "secret";

    /// <summary>Usuário que origina a ação (para auditoria).</summary>
    public required string ActorUserId { get; init; }

    public string? DeviceId { get; init; }
}

/// <summary>Contexto de acesso para operações de leitura/rotação/revogação.</summary>
public sealed record VaultAccessContext
{
    public required string ActorUserId { get; init; }

    public string? DeviceId { get; init; }
}

/// <summary>
/// Ações de credencial registradas em auditoria. Alinhadas a
/// <c>docs/18-modelo-permissoes-rbac.md</c>.
/// </summary>
public static class VaultAction
{
    public const string CredentialCreate = "credential.create";

    public const string CredentialUse = "credential.use";

    public const string CredentialRotate = "credential.rotate";

    public const string CredentialRevoke = "credential.revoke";
}
