using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RemoteOps.Cloud.Configuration;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;

namespace RemoteOps.Cloud.Auth;

public sealed class TokenService(
    AppDbContext db,
    IConfiguration config,
    MfaSecretProtector mfaProtector,
    ILogger<TokenService> logger)
{
    private const int RefreshTokenByteLength = 64;
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    // Seam de teste: relógio para a validação do TOTP. Por padrão é o real; um teste o fixa para
    // gerar um código determinístico e provar a exigência de 2FA no login.
    internal Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    // PBKDF2 de um valor fixo, calculado UMA vez no carregamento do tipo. Serve de alvo
    // "decoy": o login de e-mail inexistente (ou de tipo de prova incompatível) verifica
    // contra ele para gastar o MESMO PBKDF2 e não vazar a existência da conta por timing.
    private static readonly string DummyAuthHashHash = PasswordHasher.Hash("remoteops:login-timing-decoy:v1");

    // Seam de teste: por padrão é o PBKDF2 real. Um teste injeta um contador POR INSTÂNCIA
    // (sem estado global → sem flake com a paralelização do xUnit) para provar que login de
    // e-mail existente e inexistente invocam o hasher o MESMO número de vezes.
    internal Func<string, string?, bool> ProofVerifier { get; init; } = PasswordHasher.Verify;

    public async Task<LoginResult> LoginAsync(LoginRequest req, string ipAddress, CancellationToken ct)
    {
        var email = EmailNormalizer.Normalize(req.Email);
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Email == email && u.Status == "active", ct);

        // Roda SEMPRE (mesmo com user null) para gastar o PBKDF2 e não vazar a existência
        // da conta por timing. Avaliado ANTES do short-circuit de propósito — trocar a
        // ordem reabriria o oráculo de enumeração.
        var proofValid = VerifyProof(req, user);
        if (user is null || !proofValid)
        {
            logger.LogWarning("Login failed for email hash {EmailHash} from {Ip}",
                HashForLog(email), ipAddress);
            return LoginResult.InvalidCredentials;
        }

        // 2FA: SÓ é checado DEPOIS de a senha (AuthHash) validar. Assim o sinal "mfa_required"
        // nunca vira oráculo de enumeração — quem chega aqui já provou a identidade pela senha. E o
        // custo/timing do caminho de falha antes deste ponto segue idêntico ao de sempre (o PBKDF2 já
        // rodou uma vez, com ou sem 2FA na conta).
        if (user.MfaRequired && !VerifyTotp(user, req.TotpCode))
        {
            logger.LogWarning("Login needs valid TOTP for user {UserId} from {Ip}", user.Id, ipAddress);
            return LoginResult.MfaChallenge;
        }

        var device = await EnsureDeviceAsync(user.Id, req.DeviceId, req.DeviceName, ct);
        if (device.Status == "revoked")
        {
            logger.LogWarning("Login blocked: device {DeviceId} revoked for user {UserId}", device.Id, user.Id);
            return LoginResult.InvalidCredentials;
        }

        logger.LogInformation("Login ok for user {UserId} device {DeviceId}", user.Id, device.Id);
        return LoginResult.Success(await IssueSessionAsync(user, device.Id, ct));
    }

    /// <summary>
    /// Valida o TOTP contra o segredo cifrado da conta. Um blob que não desembrulha (chave de deploy
    /// rotacionada, corrupção) NÃO trava o login como sucesso: conta como "código inválido" → a UI
    /// pede o código de novo e o operador percebe (e re-inscreve o 2FA).
    /// </summary>
    private bool VerifyTotp(UserEntity user, string? code)
    {
        if (user.MfaSecret is null)
        {
            return false;
        }

        byte[] secret;
        try
        {
            secret = mfaProtector.Unprotect(user.MfaSecret);
        }
        catch (CryptographicException)
        {
            logger.LogError("TOTP secret failed to unprotect for user {UserId}", user.Id);
            return false;
        }

        try
        {
            return TotpService.Verify(secret, code, UtcNow());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Emite a sessão (tokens + escrow da AMK + workspaces). Compartilhado entre
    /// login e registro para que os dois devolvam exatamente o mesmo formato.
    /// </summary>
    internal async Task<LoginResponse> IssueSessionAsync(UserEntity user, Guid deviceId, CancellationToken ct)
    {
        var tenantId = await ResolveUserTenantAsync(user.Id, ct);
        var (accessToken, expiresAt) = IssueAccessToken(user, tenantId);
        var refreshToken = await IssueRefreshTokenAsync(user.Id, deviceId, ct);
        var workspaces = await LoadWorkspacesAsync(user.Id, ct);

        return new LoginResponse(
            accessToken,
            refreshToken,
            expiresAt,
            // Escrow: cifrado com a KEK, que só existe no device. Devolver isto ao
            // cliente é seguro por construção — sem a senha não abre.
            user.WrappedAmkPwd is not null ? Convert.ToBase64String(user.WrappedAmkPwd) : null,
            user.WrappedAmkPwd is not null ? user.AmkKeyVersion : null,
            workspaces);
    }

    internal async Task<IReadOnlyList<WorkspaceSummary>> LoadWorkspacesAsync(Guid userId, CancellationToken ct)
        => await db.Memberships
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.Workspace.Status == "active")
            .Select(m => new WorkspaceSummary(
                m.WorkspaceId.ToString(), m.Workspace.Name, m.Role))
            .ToListAsync(ct);

    /// <summary>
    /// Tenant do usuário para o claim <c>tenant_id</c> (ativa a guarda cross-tenant do
    /// PermissionEvaluator). Hoje um usuário pertence a exatamente um tenant: o membership só
    /// é criado no /auth/register, junto com o tenant. Por robustez futura, só devolve valor
    /// quando o tenant é ÚNICO — se um dia o usuário pertencer a workspaces de tenants
    /// diferentes, devolve null (claim omitido → guarda inerte, sem NEGAR acesso legítimo) em
    /// vez de cravar um tenant arbitrário. Membership continua protegendo em qualquer caso.
    /// </summary>
    internal async Task<Guid?> ResolveUserTenantAsync(Guid userId, CancellationToken ct)
    {
        var tenantIds = await db.Memberships
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.Workspace.Status == "active")
            .Select(m => m.Workspace.TenantId)
            .Distinct()
            .Take(2)
            .ToListAsync(ct);

        return tenantIds.Count == 1 ? tenantIds[0] : null;
    }

    internal async Task<DeviceEntity> EnsureDeviceAsync(
        Guid userId, string deviceIdStr, string deviceName, CancellationToken ct)
    {
        if (!Guid.TryParse(deviceIdStr, out var deviceId))
            deviceId = Guid.NewGuid();

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId, ct);
        if (device is not null) return device;

        device = new DeviceEntity
        {
            Id = deviceId,
            UserId = userId,
            Name = deviceName,
            Status = "active",
            RegisteredAt = DateTimeOffset.UtcNow,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync(ct);
        return device;
    }

    /// <summary>
    /// Valida a prova de identidade. Conta E2EE só aceita AuthHash; conta legada só
    /// aceita senha. Nunca os dois — senão o caminho legado viraria bypass do E2EE.
    ///
    /// Roda SEMPRE exatamente um PBKDF2 (real ou decoy), inclusive com <paramref name="user"/>
    /// nulo: sem isso, e-mail inexistente respondia em sub-ms e existente em dezenas de ms,
    /// vazando a existência da conta por timing e furando a anti-enumeração do /auth/kdf.
    /// </summary>
    private bool VerifyProof(LoginRequest req, UserEntity? user)
    {
        var hasAuthHash = !string.IsNullOrEmpty(req.AuthHash);
        var hasPassword = !string.IsNullOrEmpty(req.Password);

        // Escolhe a prova enviada e o hash-alvo correspondente. Sem alvo (conta inexistente,
        // tipo de prova incompatível com a conta, ou requisição malformada com ambas/nenhuma
        // prova) o alvo é null e caímos no decoy — nunca em short-circuit.
        string proof;
        string? targetHash;
        if (hasAuthHash == hasPassword)
        {
            // Ambas ou nenhuma prova: o endpoint já barra com 400, mas se chamado direto
            // ainda pagamos o custo para não abrir um oráculo lateral.
            proof = req.AuthHash ?? req.Password ?? string.Empty;
            targetHash = null;
        }
        else if (hasAuthHash)
        {
            proof = req.AuthHash!;
            targetHash = user?.AuthHashHash;
        }
        else
        {
            proof = req.Password!;
            targetHash = user?.PasswordHash;
        }

        if (targetHash is null)
        {
            // Gasta um PBKDF2 no decoy e descarta o resultado (nunca autentica).
            _ = ProofVerifier(proof, DummyAuthHashHash);
            return false;
        }

        return ProofVerifier(proof, targetHash);
    }

    public async Task<RefreshResponse?> RefreshAsync(RefreshRequest req, string ipAddress, CancellationToken ct)
    {
        var tokenHash = HashToken(req.RefreshToken);
        var stored = await db.RefreshTokens
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.TokenHash == tokenHash, ct);

        if (stored is null || stored.RevokedAt is not null || stored.ExpiresAt < DateTimeOffset.UtcNow)
        {
            logger.LogWarning("Refresh token invalid or expired from {Ip}", ipAddress);
            return null;
        }

        if (stored.User.Status != "active")
        {
            logger.LogWarning("Refresh blocked: user {UserId} inactive", stored.UserId);
            return null;
        }

        // Revogação de device invalida todos os refresh tokens desse device imediatamente
        var device = await db.Devices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == stored.DeviceId, ct);
        if (device is null || device.Status == "revoked")
        {
            stored.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Refresh blocked: device {DeviceId} revoked or missing", stored.DeviceId);
            return null;
        }

        // Rotate refresh token (revoke old, issue new)
        stored.RevokedAt = DateTimeOffset.UtcNow;
        var newRefresh = await IssueRefreshTokenAsync(stored.UserId, stored.DeviceId, ct);
        var tenantId = await ResolveUserTenantAsync(stored.UserId, ct);
        var (accessToken, expiresAt) = IssueAccessToken(stored.User, tenantId);

        logger.LogInformation("Token refreshed for user {UserId} device {DeviceId}", stored.UserId, stored.DeviceId);
        return new RefreshResponse(accessToken, newRefresh, expiresAt);
    }

    public async Task<bool> LogoutAsync(string refreshToken, CancellationToken ct)
    {
        var tokenHash = HashToken(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == tokenHash, ct);
        if (stored is null) return false;

        stored.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Logout: refresh token revoked for user {UserId}", stored.UserId);
        return true;
    }

    private (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(UserEntity user, Guid? tenantId)
    {
        var key = GetSigningKey();
        var expires = DateTimeOffset.UtcNow.Add(AccessTokenLifetime);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("display_name", user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        // tenant_id ativa a guarda cross-tenant do PermissionEvaluator (defense-in-depth).
        // Emitido só quando o tenant do usuário é único e resolvível (ver ResolveUserTenantAsync).
        if (tenantId.HasValue)
            claims.Add(new Claim("tenant_id", tenantId.Value.ToString()));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expires.UtcDateTime,
            Issuer = config["Jwt:Issuer"],
            Audience = config["Jwt:Audience"],
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);
        return (handler.WriteToken(token), expires);
    }

    private async Task<string> IssueRefreshTokenAsync(Guid userId, Guid deviceId, CancellationToken ct)
    {
        var rawBytes = RandomNumberGenerator.GetBytes(RefreshTokenByteLength);
        var rawToken = Convert.ToBase64String(rawBytes);

        db.RefreshTokens.Add(new RefreshTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DeviceId = deviceId,
            TokenHash = HashToken(rawToken),
            ExpiresAt = DateTimeOffset.UtcNow.Add(RefreshTokenLifetime),
        });
        await db.SaveChangesAsync(ct);
        return rawToken;
    }

    private SymmetricSecurityKey GetSigningKey()
        => new(DeploymentConfig.ResolveJwtSigningKey(config));

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string HashForLog(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }
}
