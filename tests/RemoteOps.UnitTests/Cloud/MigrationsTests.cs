using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RemoteOps.Cloud.Data;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// Guarda das migrations EF. O startup roda <c>Database.Migrate()</c>: se o modelo
/// andar sem migration, o deploy sobe com o schema velho e quebra em produção —
/// aqui isso vira teste vermelho.
///
/// Nada aqui conecta no banco: o provider Npgsql monta o modelo e gera SQL offline
/// (é o mesmo caminho do `dotnet ef migrations script`).
/// </summary>
public sealed class MigrationsTests
{
    // Nunca é aberta — só faz o Npgsql resolver o provider relacional.
    private const string OfflineConnection = "Host=localhost;Port=5432;Database=remoteops;Username=postgres";

    private static AppDbContext OfflineNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(OfflineConnection)
            .Options);

    [Fact]
    public void ExisteMigrationInicial()
    {
        using var db = OfflineNpgsqlContext();

        Assert.NotEmpty(db.Database.GetMigrations());
    }

    [Fact]
    public void Modelo_NaoTemMudancasPendentes()
    {
        using var db = OfflineNpgsqlContext();

        // Falhou? Rode: dotnet ef migrations add <Nome> -p src/RemoteOps.Cloud
        Assert.False(
            db.Database.HasPendingModelChanges(),
            "O modelo EF mudou sem migration nova — o deploy subiria com o schema velho.");
    }

    [Fact]
    public void MigrationInicial_CobreOSchemaE2ee()
    {
        using var db = OfflineNpgsqlContext();
        var sql = db.GetService<IMigrator>().GenerateScript();

        // As colunas E2EE (spec §4.2) são a razão desta fase existir: sem elas o
        // /auth/register grava escrow em coluna inexistente.
        Assert.Contains("\"Argon2Salt\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"AuthHashHash\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"WrappedAmkPwd\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"WrappedAmkRec\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"AmkKeyVersion\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationInicial_CriaTodasAsTabelasDoModelo()
    {
        using var db = OfflineNpgsqlContext();
        var sql = db.GetService<IMigrator>().GenerateScript();

        foreach (var table in db.Model.GetEntityTypes()
                     .Select(e => e.GetTableName())
                     .Where(t => t is not null)
                     .Distinct())
        {
            // O Npgsql só põe aspas em identificador que precisa (tabela minúscula sai
            // crua, coluna PascalCase sai citada) — o regex aceita as duas formas.
            Assert.Matches($"CREATE TABLE \"?{Regex.Escape(table!)}\"? \\(", sql);
        }
    }

    [Fact]
    public void MigrationInicial_TemOIndiceUnicoDoCursorDeSegredos()
    {
        using var db = OfflineNpgsqlContext();
        var sql = db.GetService<IMigrator>().GenerateScript();

        // O índice único (WorkspaceId, Cursor) é o que transforma corrida de upsert
        // em 409 em vez de envelope perdido no pull. Migration sem ele = regressão.
        Assert.Contains("CREATE UNIQUE INDEX", sql, StringComparison.Ordinal);
        Assert.Contains("secret_envelopes", sql, StringComparison.Ordinal);
    }
}
