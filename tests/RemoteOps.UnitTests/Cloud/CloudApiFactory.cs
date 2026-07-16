using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteOps.Cloud.Data;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// Sobe a API real (Program.cs) com o AppDbContext trocado por InMemory.
///
/// Cobre o que o teste de serviço não alcança: rota, binding JSON (camelCase),
/// mapeamento de RbacDeniedException → 403 e o rate limiter.
///
/// LIMITAÇÃO: o provider InMemory não é Postgres — não valida SQL, índice único
/// nem concorrência real. Ver §12 do spec (Testcontainers fica pendente).
/// </summary>
internal sealed class CloudApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"remoteops-api-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        // Program.cs exige estas configs no startup. A connection string nunca é
        // usada: o DbContext é substituído logo abaixo.
        builder.UseSetting("ConnectionStrings:Default", "Host=invalid;Database=remoteops-test");
        builder.UseSetting("Jwt:SigningKey", "remoteops-test-signing-key-32bytes!!");
        builder.UseSetting("Jwt:Issuer", "remoteops-test");
        builder.UseSetting("Jwt:Audience", "remoteops-test");
        builder.UseSetting("Auth:KdfDecoyKeyBase64", Convert.ToBase64String(new byte[32]));

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
