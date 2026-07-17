using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using RemoteOps.Cloud.Configuration;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// FIX 1 (HIGH): a API confia no X-Forwarded-For SÓ vindo de proxies conhecidos (bridges do
/// Docker), reescrevendo o RemoteIpAddress para o IP do cliente. Sem isso, atrás do Caddy o
/// rate limit por IP vira um balde global e os logs registram só o proxy.
/// </summary>
public sealed class ForwardedHeadersTests
{
    // ── Parser do TRUSTED_PROXY_CIDR ──────────────────────────────────────────

    [Fact]
    public void Parse_Default_WhenNullOrBlank()
    {
        var fromNull = ForwardedHeadersSetup.ParseTrustedNetworks(null);
        var fromBlank = ForwardedHeadersSetup.ParseTrustedNetworks("   ");

        // Default cobre as faixas das bridges do Docker: 172.16.0.0/12 e 10.0.0.0/8.
        Assert.Equal(2, fromNull.Count);
        Assert.Contains(fromNull, n => n.BaseAddress.Equals(IPAddress.Parse("172.16.0.0")) && n.PrefixLength == 12);
        Assert.Contains(fromNull, n => n.BaseAddress.Equals(IPAddress.Parse("10.0.0.0")) && n.PrefixLength == 8);
        Assert.Equal(fromNull.Count, fromBlank.Count);
    }

    [Fact]
    public void Parse_CustomCsv_WithWhitespace()
    {
        var nets = ForwardedHeadersSetup.ParseTrustedNetworks(" 192.168.0.0/16 , 10.10.0.0/16 ");

        Assert.Equal(2, nets.Count);
        Assert.Contains(nets, n => n.BaseAddress.Equals(IPAddress.Parse("192.168.0.0")) && n.PrefixLength == 16);
        Assert.Contains(nets, n => n.BaseAddress.Equals(IPAddress.Parse("10.10.0.0")) && n.PrefixLength == 16);
    }

    [Fact]
    public void Parse_Throws_OnInvalidCidr()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ForwardedHeadersSetup.ParseTrustedNetworks("172.16.0.0/12,garbage"));
        Assert.Contains("garbage", ex.Message);
    }

    [Fact]
    public void Parse_Throws_OnOnlySeparators()
        => Assert.Throws<InvalidOperationException>(() => ForwardedHeadersSetup.ParseTrustedNetworks(",, ,"));

    // ── Comportamento do middleware (confia x ignora) ─────────────────────────

    [Fact]
    public async Task ForwardedFor_Honored_FromTrustedProxy()
    {
        // Proxy na faixa confiável (172.16/12) → o X-Forwarded-For do cliente é aceito.
        var seenIp = await ResolveRemoteIpAsync(
            remoteIp: "172.20.0.5", forwardedFor: "203.0.113.7", trustedCidr: null);

        Assert.Equal("203.0.113.7", seenIp);
    }

    [Fact]
    public async Task ForwardedFor_Ignored_FromUntrustedProxy()
    {
        // Origem fora da faixa confiável (internet pública) → header ignorado (anti-spoof):
        // o IP visto continua sendo o da conexão, não o forjado no X-Forwarded-For.
        var seenIp = await ResolveRemoteIpAsync(
            remoteIp: "8.8.8.8", forwardedFor: "203.0.113.7", trustedCidr: null);

        Assert.Equal("8.8.8.8", seenIp);
    }

    [Fact]
    public async Task ForwardedFor_RespectsCustomCidr()
    {
        // Com TRUSTED_PROXY_CIDR custom, só a faixa configurada é confiável.
        var honored = await ResolveRemoteIpAsync("192.168.5.9", "203.0.113.7", "192.168.0.0/16");
        Assert.Equal("203.0.113.7", honored);

        // 172.x deixa de ser confiável quando o default é sobrescrito.
        var ignored = await ResolveRemoteIpAsync("172.20.0.5", "203.0.113.7", "192.168.0.0/16");
        Assert.Equal("172.20.0.5", ignored);
    }

    /// <summary>
    /// Sobe um host mínimo com o MESMO wiring da API (ForwardedHeadersSetup.Configure +
    /// UseForwardedHeaders), força um RemoteIpAddress de conexão e devolve o IP que o
    /// pipeline enxerga depois de processar o X-Forwarded-For.
    /// </summary>
    private static async Task<string> ResolveRemoteIpAsync(string remoteIp, string forwardedFor, string? trustedCidr)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TRUSTED_PROXY_CIDR"] = trustedCidr })
            .Build();
        ForwardedHeadersSetup.Configure(builder.Services, config);

        await using var app = builder.Build();

        // Simula o IP real da conexão (o do proxy). Roda ANTES do UseForwardedHeaders.
        app.Use((ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            return next(ctx);
        });
        app.UseForwardedHeaders();
        app.Run(ctx => ctx.Response.WriteAsync(ctx.Connection.RemoteIpAddress?.ToString() ?? "null"));

        await app.StartAsync();
        using var client = app.GetTestClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("X-Forwarded-For", forwardedFor);
        var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        await app.StopAsync();
        return body;
    }
}
