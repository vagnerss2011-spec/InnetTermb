using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteOps.Cloud.Auth;
using RemoteOps.Cloud.Data.Entities;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

public sealed class AuthTests
{
    private static IConfiguration TestConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = "remoteops-test-signing-key-32bytes!!",
                ["Jwt:Issuer"] = "remoteops-test",
                ["Jwt:Audience"] = "remoteops-test",
            })
            .Build();

    // Replica da lógica de hash do TokenService (SHA-256 hex lowercase)
    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ── Refresh com device revogado ───────────────────────────────────────────

    [Fact]
    public async Task Refresh_ReturnsNull_WhenDeviceRevoked()
    {
        using var ctx = new CloudTestContext();
        var (_, _, user, _) = await ctx.SeedActiveUserAsync();

        var device = new DeviceEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Name = "Laptop Roubado",
            Status = "active",
            RegisteredAt = DateTimeOffset.UtcNow,
        };
        ctx.Db.Devices.Add(device);

        const string rawToken = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";
        ctx.Db.RefreshTokens.Add(new RefreshTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceId = device.Id,
            TokenHash = HashToken(rawToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await ctx.Db.SaveChangesAsync();

        // Administrador revoga o device (ex: dispositivo perdido/roubado)
        device.Status = "revoked";
        device.RevokedAt = DateTimeOffset.UtcNow;
        await ctx.Db.SaveChangesAsync();

        var svc = new TokenService(ctx.Db, TestConfig(), NullLogger<TokenService>.Instance);
        var result = await svc.RefreshAsync(
            new RefreshRequest(rawToken, device.Id.ToString()), "1.2.3.4", default);

        // Refresh deve ser bloqueado
        Assert.Null(result);

        // O refresh token deve ter sido revogado no DB (cascade)
        var stored = ctx.Db.RefreshTokens.First();
        Assert.NotNull(stored.RevokedAt);
    }

    // ── Refresh com device removido do DB ─────────────────────────────────────

    [Fact]
    public async Task Refresh_ReturnsNull_WhenDeviceNotFound()
    {
        using var ctx = new CloudTestContext();
        var (_, _, user, _) = await ctx.SeedActiveUserAsync();

        // Refresh token aponta para device que não existe mais no DB
        const string rawToken = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB==";
        ctx.Db.RefreshTokens.Add(new RefreshTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceId = Guid.NewGuid(), // device inexistente
            TokenHash = HashToken(rawToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await ctx.Db.SaveChangesAsync();

        var svc = new TokenService(ctx.Db, TestConfig(), NullLogger<TokenService>.Instance);
        var result = await svc.RefreshAsync(
            new RefreshRequest(rawToken, Guid.NewGuid().ToString()), "1.2.3.4", default);

        Assert.Null(result);
    }
}
