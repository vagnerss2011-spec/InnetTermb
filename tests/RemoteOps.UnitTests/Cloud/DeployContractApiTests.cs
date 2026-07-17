extern alias cloud;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteOps.Cloud.Data;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// Sobe a API com EXATAMENTE as variáveis que o docker-compose.yml define — e só
/// elas: REMOTEOPS_DB_CONNECTION e Jwt__SecretKeyBase64, sem os nomes legados e sem
/// Auth__KdfDecoyKeyBase64.
///
/// É o teste que prova o deploy do runbook, não a configuração dos outros testes.
/// </summary>
internal sealed class DeployContractApiFactory : WebApplicationFactory<cloud::Program>
{
    private readonly string _dbName = $"remoteops-deploy-{Guid.NewGuid()}";

    /// <summary>32 bytes aleatórios em base64 — a forma que o runbook manda gerar.</summary>
    public static string NewSecretKeyBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);

        // Nomes do compose. Nenhum ConnectionStrings:Default, nenhum Jwt:SigningKey:
        // se o startup depender dos legados, o app nem sobe e o teste denuncia.
        builder.UseSetting("REMOTEOPS_DB_CONNECTION", "Host=invalid;Database=remoteops-test");
        builder.UseSetting("Jwt:SecretKeyBase64", NewSecretKeyBase64());
        builder.UseSetting("Jwt:Issuer", "remoteops-cloud");
        builder.UseSetting("Jwt:Audience", "remoteops-desktop");

        builder.ConfigureServices(services =>
        {
            var doomed = services
                .Where(d =>
                    d.ServiceType == typeof(AppDbContext) ||
                    (d.ServiceType.FullName?.Contains("DbContextOptions", StringComparison.Ordinal) ?? false))
                .ToList();
            foreach (var d in doomed) services.Remove(d);

            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));
        });
    }
}

public sealed class DeployContractApiTests
{
    private static byte[] Rand(int n) => RandomNumberGenerator.GetBytes(n);

    private static object RegisterBody(string email, string deviceId) => new
    {
        email,
        argon2Salt = Convert.ToBase64String(Rand(16)),
        argon2Params = new { memoryKib = 65536, iterations = 3, parallelism = 1, outputBytes = 32 },
        authHash = Convert.ToBase64String(Rand(32)),
        wrappedAmkPwd = Convert.ToBase64String(Rand(60)),
        wrappedAmkRec = Convert.ToBase64String(Rand(60)),
        amkKeyVersion = 1,
        deviceId,
        deviceName = "Device do operador",
        workspaceName = "Meu Workspace",
    };

    [Fact]
    public async Task ApiSobeEAutenticaSoComAsVariaveisDoCompose()
    {
        using var factory = new DeployContractApiFactory();
        using var client = factory.CreateClient();

        var deviceId = Guid.NewGuid().ToString();
        var regResp = await client.PostAsJsonAsync("/auth/register", RegisterBody("operador@deploy.local", deviceId));
        Assert.Equal(HttpStatusCode.OK, regResp.StatusCode);

        var reg = await regResp.Content.ReadFromJsonAsync<JsonElement>();
        var token = reg.GetProperty("accessToken").GetString();
        var workspaceId = reg.GetProperty("workspaceId").GetString();

        // O pulo do gato: o TokenService ASSINA e o Program.cs VALIDA. Se um lesse a
        // chave como base64 e o outro como UTF-8, o token sairia válido e voltaria 401
        // em TODA request autenticada — com o deploy no ar e o login "funcionando".
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/sync/pull?workspaceId={workspaceId}&cursor=0&pageSize=50");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Device-Id", deviceId);

        var pull = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, pull.StatusCode);
    }

    [Fact]
    public async Task KdfFunciona_SemAuthKdfDecoyKeyConfigurada()
    {
        // O compose deixa Auth__KdfDecoyKeyBase64 vazia: o decoy anti-enumeração cai
        // no fallback que deriva da chave do JWT. Se esse fallback lesse o nome legado
        // (Jwt:SigningKey), o /auth/kdf estouraria 500 no deploy — e só no deploy.
        using var factory = new DeployContractApiFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/auth/kdf?email=naoexiste@deploy.local");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var kdf = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(16, Convert.FromBase64String(kdf.GetProperty("argon2Salt").GetString()!).Length);
    }
}
