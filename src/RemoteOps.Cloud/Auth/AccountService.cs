using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;
using RemoteOps.Cloud.Rbac;

namespace RemoteOps.Cloud.Auth;

/// <summary>
/// Ciclo de vida da conta E2EE (spec cloud-sync-e2ee-phase1 §5).
///
/// REGRA DE OURO: o servidor nunca recebe senha, MasterKey, KEK, AMK, WDK ou CEK.
/// Só transita material público (salt/params do Argon2id), a prova de senha
/// (AuthHash, guardada como PBKDF2 dela) e os escrows opacos da AMK.
/// </summary>
public sealed class AccountService(
    AppDbContext db,
    TokenService tokens,
    IConfiguration config,
    ILogger<AccountService> logger)
{
    /// <summary>Params v1 do spec §4.1: 64 MiB, 3 iterações, paralelismo 1, saída 32B.</summary>
    public static readonly Argon2Params DefaultArgon2Params = new(65536, 3, 1, 32);

    private const int SaltLength = 16;
    private const int AuthHashLength = 32;

    // Piso de custo do Argon2id. Os params são escolhidos pelo device (perfil da
    // máquina), mas sem um piso um cliente adulterado poderia registrar com custo
    // ~0 e enfraquecer o escrow da própria conta. 19 MiB = mínimo OWASP p/ Argon2id.
    private const int MinMemoryKib = 19456;
    private const int MinIterations = 2;

    // ── POST /auth/register ───────────────────────────────────────────────────

    /// <summary>Cria Tenant + Workspace + User + Membership + Device. Null = e-mail já em uso.</summary>
    public async Task<RegisterResponse?> RegisterAsync(RegisterRequest req, string ipAddress, CancellationToken ct)
    {
        var email = EmailNormalizer.Normalize(req.Email);
        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
            throw new ArgumentException("E-mail inválido.");
        if (string.IsNullOrWhiteSpace(req.WorkspaceName))
            throw new ArgumentException("workspaceName é obrigatório.");
        if (req.AmkKeyVersion < 1)
            throw new ArgumentException("amkKeyVersion deve ser >= 1.");

        var salt = DecodeExact(req.Argon2Salt, SaltLength, nameof(req.Argon2Salt));
        var authHash = DecodeExact(req.AuthHash, AuthHashLength, nameof(req.AuthHash));
        var wrappedPwd = DecodeNonEmpty(req.WrappedAmkPwd, nameof(req.WrappedAmkPwd));
        var wrappedRec = DecodeNonEmpty(req.WrappedAmkRec, nameof(req.WrappedAmkRec));
        ValidateParams(req.Argon2Params);

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            // Log sem o e-mail em claro; o endpoint devolve 409 genérico.
            logger.LogWarning("Register rejected: email already in use (hash {EmailHash}) from {Ip}",
                HashForLog(email), ipAddress);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Name = req.WorkspaceName,
            Status = "active",
            CreatedAt = now,
        };
        var workspace = new WorkspaceEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = req.WorkspaceName,
            Status = "active",
            CreatedAt = now,
        };
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = email.Split('@')[0],
            Status = "active",
            MfaRequired = false,
            // PasswordHash fica NULO: conta E2EE não tem senha no servidor.
            PasswordHash = null,
            // PBKDF2 do AuthHash — o AuthHash cru nunca toca o disco.
            AuthHashHash = PasswordHasher.Hash(req.AuthHash),
            Argon2Salt = salt,
            Argon2MemoryKib = req.Argon2Params.MemoryKib,
            Argon2Iterations = req.Argon2Params.Iterations,
            Argon2Parallelism = req.Argon2Params.Parallelism,
            Argon2OutputBytes = req.Argon2Params.OutputBytes,
            WrappedAmkPwd = wrappedPwd,
            WrappedAmkRec = wrappedRec,
            AmkKeyVersion = req.AmkKeyVersion,
            CreatedAt = now,
        };
        var membership = new MembershipEntity
        {
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            // Quem cria a conta é dono do próprio workspace.
            Role = Roles.Owner,
        };

        db.Tenants.Add(tenant);
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        db.Memberships.Add(membership);
        await db.SaveChangesAsync(ct);

        var device = await tokens.EnsureDeviceAsync(user.Id, req.DeviceId, req.DeviceName, ct);
        var session = await tokens.IssueSessionAsync(user, device.Id, ct);

        // authHash local só serviu para validar o tamanho; zera para não sobrar cópia na memória.
        CryptographicOperations.ZeroMemory(authHash);

        logger.LogInformation("Register ok: user {UserId} workspace {WorkspaceId} device {DeviceId}",
            user.Id, workspace.Id, device.Id);

        return new RegisterResponse(
            session.AccessToken,
            session.RefreshToken,
            session.ExpiresAt,
            workspace.Id.ToString(),
            session.WrappedAmkPwd,
            session.AmkKeyVersion,
            session.Workspaces);
    }

    // ── GET /auth/kdf ─────────────────────────────────────────────────────────

    /// <summary>
    /// Params públicos de KDF para o device derivar a MasterKey antes do login.
    ///
    /// ANTI-ENUMERAÇÃO: e-mail sem conta E2EE recebe params DETERMINÍSTICOS derivados
    /// do próprio e-mail. A resposta tem o mesmo shape e é indistinguível de uma
    /// conta real — quem tenta enumerar só descobre que o e-mail "tem algum salt",
    /// o que é verdade para qualquer string.
    /// </summary>
    public async Task<KdfResponse> GetKdfAsync(string email, CancellationToken ct)
    {
        var normalized = EmailNormalizer.Normalize(email);

        var found = await db.Users.AsNoTracking()
            .Where(u => u.Email == normalized)
            .Select(u => new
            {
                u.Argon2Salt,
                u.Argon2MemoryKib,
                u.Argon2Iterations,
                u.Argon2Parallelism,
                u.Argon2OutputBytes,
            })
            .FirstOrDefaultAsync(ct);

        // Conta legada (sem Argon2Salt) também cai no decoy: revelar "existe, mas é
        // legada" continua sendo enumeração.
        if (found?.Argon2Salt is not null)
        {
            return new KdfResponse(
                Convert.ToBase64String(found.Argon2Salt),
                new Argon2Params(
                    found.Argon2MemoryKib,
                    found.Argon2Iterations,
                    found.Argon2Parallelism,
                    found.Argon2OutputBytes));
        }

        return DecoyFor(normalized);
    }

    /// <summary>
    /// Salt fake = HMAC-SHA256(segredo do servidor, e-mail normalizado)[..16].
    ///
    /// Por que HMAC e não aleatório: o decoy tem que ser ESTÁVEL entre chamadas.
    /// Um salt aleatório por request denunciaria a conta inexistente em duas
    /// requisições (o salt de uma conta real nunca muda sozinho). E por que com
    /// segredo: sem ele o atacante recomputaria o decoy offline e distinguiria
    /// conta real de fake na hora.
    /// </summary>
    private KdfResponse DecoyFor(string normalizedEmail)
    {
        var mac = HMACSHA256.HashData(DecoyKey(), Encoding.UTF8.GetBytes(normalizedEmail));
        return new KdfResponse(Convert.ToBase64String(mac[..SaltLength]), DefaultArgon2Params);
    }

    private byte[] DecoyKey()
    {
        var configured = config["Auth:KdfDecoyKeyBase64"];
        if (!string.IsNullOrEmpty(configured))
            return Convert.FromBase64String(configured);

        // Fallback: deriva do segredo de assinatura do JWT para não exigir uma env var
        // nova em deploys já existentes. HKDF com info dedicada evita reusar a chave
        // crua de assinatura para outro fim.
        var signing = config["Jwt:SigningKey"]
            ?? throw new InvalidOperationException(
                "Auth:KdfDecoyKeyBase64 ou Jwt:SigningKey precisa estar configurada para o decoy do /auth/kdf.");
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(signing),
            outputLength: 32,
            info: Encoding.UTF8.GetBytes("remoteops:kdf-decoy:v1"));
    }

    // ── POST /auth/password/change ────────────────────────────────────────────

    /// <summary>
    /// Re-embrulha a AMK sob a KEK da senha nova. A AMK NÃO muda: os SecretEnvelope
    /// continuam decifráveis e o escrow de recuperação segue válido (spec §6).
    /// </summary>
    public async Task<bool> ChangePasswordAsync(Guid userId, ChangePasswordRequest req, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.Status == "active", ct);
        if (user?.AuthHashHash is null)
        {
            logger.LogWarning("Password change rejected: user {UserId} not found or not E2EE", userId);
            return false;
        }

        if (!PasswordHasher.Verify(req.OldAuthHash, user.AuthHashHash))
        {
            logger.LogWarning("Password change rejected: old AuthHash mismatch for user {UserId}", userId);
            return false;
        }

        // Valida ANTES de mutar: um payload novo inválido não pode deixar a conta
        // num estado meio-trocado (senha nova gravada, escrow velho) — isso trancaria
        // o cofre para sempre.
        var newSalt = DecodeExact(req.NewArgon2Salt, SaltLength, nameof(req.NewArgon2Salt));
        DecodeExact(req.NewAuthHash, AuthHashLength, nameof(req.NewAuthHash));
        var newWrapped = DecodeNonEmpty(req.NewWrappedAmkPwd, nameof(req.NewWrappedAmkPwd));
        ValidateParams(req.NewArgon2Params);

        user.Argon2Salt = newSalt;
        user.Argon2MemoryKib = req.NewArgon2Params.MemoryKib;
        user.Argon2Iterations = req.NewArgon2Params.Iterations;
        user.Argon2Parallelism = req.NewArgon2Params.Parallelism;
        user.Argon2OutputBytes = req.NewArgon2Params.OutputBytes;
        user.AuthHashHash = PasswordHasher.Hash(req.NewAuthHash);
        user.WrappedAmkPwd = newWrapped;
        // WrappedAmkRec e AmkKeyVersion ficam INTOCADOS de propósito.

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Password changed (AMK rewrapped) for user {UserId}", userId);
        return true;
    }

    // ── Validação ─────────────────────────────────────────────────────────────

    private static void ValidateParams(Argon2Params p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.MemoryKib < MinMemoryKib)
            throw new ArgumentException($"argon2Params.memoryKib abaixo do mínimo ({MinMemoryKib}).");
        if (p.Iterations < MinIterations)
            throw new ArgumentException($"argon2Params.iterations abaixo do mínimo ({MinIterations}).");
        if (p.Parallelism < 1)
            throw new ArgumentException("argon2Params.parallelism deve ser >= 1.");
        if (p.OutputBytes != 32)
            throw new ArgumentException("argon2Params.outputBytes deve ser 32 (a MasterKey é de 32B).");
    }

    private static byte[] DecodeExact(string b64, int expectedLength, string field)
    {
        var bytes = DecodeNonEmpty(b64, field);
        if (bytes.Length != expectedLength)
            throw new ArgumentException($"{field} deve ter {expectedLength} bytes.");
        return bytes;
    }

    private static byte[] DecodeNonEmpty(string b64, string field)
    {
        if (string.IsNullOrWhiteSpace(b64))
            throw new ArgumentException($"{field} é obrigatório.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { throw new ArgumentException($"{field} não é base64 válido."); }
        if (bytes.Length == 0)
            throw new ArgumentException($"{field} não pode ser vazio.");
        return bytes;
    }

    private static string HashForLog(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }
}
