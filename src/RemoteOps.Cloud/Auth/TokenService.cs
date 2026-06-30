using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;

namespace RemoteOps.Cloud.Auth;

public sealed class TokenService(AppDbContext db, IConfiguration config, ILogger<TokenService> logger)
{
    private const int RefreshTokenByteLength = 64;
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    public async Task<LoginResponse?> LoginAsync(LoginRequest req, string ipAddress, CancellationToken ct)
    {
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Email == req.Email && u.Status == "active", ct);

        if (user is null || !VerifyPassword(req.Password, user.PasswordHash))
        {
            logger.LogWarning("Login failed for email hash {EmailHash} from {Ip}",
                HashForLog(req.Email), ipAddress);
            return null;
        }

        var device = await EnsureDeviceAsync(user.Id, req.DeviceId, req.DeviceName, ct);
        if (device.Status == "revoked")
        {
            logger.LogWarning("Login blocked: device {DeviceId} revoked for user {UserId}", device.Id, user.Id);
            return null;
        }

        var (accessToken, expiresAt) = IssueAccessToken(user);
        var refreshToken = await IssueRefreshTokenAsync(user.Id, device.Id, ct);

        logger.LogInformation("Login ok for user {UserId} device {DeviceId}", user.Id, device.Id);
        return new LoginResponse(accessToken, refreshToken, expiresAt);
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
        var (accessToken, expiresAt) = IssueAccessToken(stored.User);

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

    private (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(UserEntity user)
    {
        var key = GetSigningKey();
        var expires = DateTimeOffset.UtcNow.Add(AccessTokenLifetime);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("display_name", user.DisplayName),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

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

    private async Task<DeviceEntity> EnsureDeviceAsync(Guid userId, string deviceIdStr, string deviceName, CancellationToken ct)
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

    private SymmetricSecurityKey GetSigningKey()
    {
        var keyStr = config["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey não configurada. Use variável de ambiente.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keyStr));
    }

    private static bool VerifyPassword(string password, string hash)
        => BCryptNet.Verify(password, hash);

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

// PBKDF2-SHA256 (OWASP 2023: 310 000 iterations). Hash format: "v1:salt_b64:hash_b64"
file static class BCryptNet
{
    private const int Iterations = 310_000;
    private const int HashLength = 32;

    public static bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        var parts = hash.Split(':');
        if (parts.Length != 3 || parts[0] != "v1") return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt,
            Iterations, HashAlgorithmName.SHA256, HashLength);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt,
            Iterations, HashAlgorithmName.SHA256, HashLength);
        return $"v1:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }
}
