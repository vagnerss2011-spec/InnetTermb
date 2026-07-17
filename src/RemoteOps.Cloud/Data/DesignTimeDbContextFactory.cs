using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RemoteOps.Cloud.Data;

/// <summary>
/// Fábrica usada SÓ pelas ferramentas de design (<c>dotnet ef migrations add/script</c>).
///
/// Sem ela, o `dotnet ef` executaria o Program.cs, que exige REMOTEOPS_DB_CONNECTION e
/// a chave do JWT e aborta — criar uma migration passaria a depender de ter as env vars
/// de produção na máquina do dev/CI. Com a fábrica, `migrations add` só precisa do
/// modelo: nada aqui abre conexão. Para comandos que falam com o banco de verdade
/// (`database update`), a connection string real vem de REMOTEOPS_DB_CONNECTION.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    // Sem senha de propósito: é placeholder de design-time, não credencial.
    private const string OfflinePlaceholder = "Host=localhost;Port=5432;Database=remoteops;Username=postgres";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("REMOTEOPS_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            connection = OfflinePlaceholder;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection)
            .Options;

        return new AppDbContext(options);
    }
}
