extern alias cloud;
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
internal sealed class CloudApiFactory : WebApplicationFactory<cloud::Program>
{
    private readonly string _dbName = $"remoteops-api-{Guid.NewGuid()}";

    /// <summary>
    /// Enviador fake injetado no lugar do real: os testes de recuperação pescam o token do "email".
    /// Inofensivo para os demais testes (nenhum outro endpoint envia email).
    /// </summary>
    public FakeEmailSender Email { get; } = new();

    /// <summary>
    /// Roda uma ação sobre o MESMO banco que a API está usando.
    ///
    /// <para>Existe para encenar estados que <b>nenhum endpoint produz</b> — e o caso concreto é o
    /// time criado por uma versão anterior do cliente, cuja membership do dono ficou sem o embrulho
    /// da chave. Desde o <c>POST /workspaces</c>, workspace e embrulho nascem na mesma gravação,
    /// então esse estado é inalcançável por HTTP; sem este acesso, o desfecho <c>stored: true</c> do
    /// <c>PUT /key</c> ficaria sem cobertura justamente no estado que ele existe para consertar.</para>
    /// </summary>
    public async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

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

            // Troca o IEmailSender real pelo fake capturável (singleton para o teste ler o que "saiu").
            var emailDescriptors = services
                .Where(d => d.ServiceType == typeof(RemoteOps.Cloud.Email.IEmailSender))
                .ToList();
            foreach (var d in emailDescriptors) services.Remove(d);
            services.AddSingleton<RemoteOps.Cloud.Email.IEmailSender>(Email);
        });
    }
}
