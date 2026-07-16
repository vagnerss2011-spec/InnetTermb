using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteOps.Cloud.Data;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

public sealed class DatabaseBootstrapperTests
{
    [Fact]
    public void ProviderNaoRelacional_NaoTentaMigrar()
    {
        // O startup aplica as migrations, mas a suíte inteira sobe a API com o provider
        // InMemory, que não tem migrations e joga InvalidOperationException. Sem a guarda
        // de "é relacional?", incluir o Migrate() no Program.cs derrubaria todos os testes
        // de API — e a guarda só fica honesta se um teste segurar ela.
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bootstrapper-{Guid.NewGuid()}")
            .Options);

        var migrated = DatabaseBootstrapper.MigrateIfRelational(db, NullLogger.Instance);

        Assert.False(migrated);
    }
}
