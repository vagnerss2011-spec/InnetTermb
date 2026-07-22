using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;
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

    /// <summary>
    /// O token de concorrência do envelope tem que valer no provider que vai para PRODUÇÃO.
    ///
    /// <para>A corrida em si é encenada no <c>SecretsTransportTests</c>, que roda no InMemory (o
    /// único provider com infra no repo). Como token de concorrência é comportamento de MODELO —
    /// o EF relacional põe todo token no <c>WHERE</c> da UPDATE —, esta asserção fecha o vão:
    /// garante que o Npgsql monta o mesmo modelo, e não só o banco de teste.</para>
    ///
    /// <para>Sem isso, alguém poderia remover a marcação e ver a suíte verde por acidente: o
    /// InMemory continuaria passando nos outros testes e a proteção sumiria em silêncio — a pior
    /// classe de regressão desta base.</para>
    /// </summary>
    [Fact]
    public void SecretEnvelope_TemTokenDeConcorrencia_NoProviderDeProducao()
    {
        using var db = OfflineNpgsqlContext();
        var envelope = db.Model.FindEntityType(typeof(SecretEnvelopeEntity));
        Assert.NotNull(envelope);

        // RevokedAt: impede que um upsert vivo concorrente republique material por cima da lápide.
        Assert.True(
            envelope.FindProperty(nameof(SecretEnvelopeEntity.RevokedAt))!.IsConcurrencyToken,
            "RevokedAt deixou de ser token de concorrência — o upsert volta a poder ressuscitar segredo revogado.");

        // Version: generaliza a proteção para qualquer sobrescrita cega do mesmo envelope.
        Assert.True(
            envelope.FindProperty(nameof(SecretEnvelopeEntity.Version))!.IsConcurrencyToken,
            "Version deixou de ser token de concorrência — dois upserts concorrentes voltam a ser last-write-wins.");
    }

    [Fact]
    public void MigrationDeReset_CriaTabelaComHashUnico()
    {
        using var db = OfflineNpgsqlContext();
        var sql = db.GetService<IMigrator>().GenerateScript();

        // A tabela de tokens de reset (Fase 4) e o índice ÚNICO no hash: sem a unicidade
        // dois tokens poderiam colidir de hash e o uso-único deixaria de valer.
        Assert.Matches("CREATE TABLE \"?password_reset_tokens\"? \\(", sql);
        Assert.Matches("CREATE UNIQUE INDEX .*password_reset_tokens.*TokenHash", sql);
    }

    /// <summary>
    /// Fatia 1: a migração dos times TEM DDL de verdade (tabela nova + duas colunas), diferente da
    /// do token de concorrência. Se o snapshot não fechar, o container sobe com o schema velho e o
    /// primeiro convite morre num 500.
    /// </summary>
    [Fact]
    public void MigrationDeTimes_CriaConvitesEAsColunasDaChavePorMembro()
    {
        using var db = OfflineNpgsqlContext();
        var sql = db.GetService<IMigrator>().GenerateScript();

        Assert.Matches("CREATE TABLE \"?invites\"? \\(", sql);
        Assert.Contains("\"CodeHash\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"WrappedWkByInvite\"", sql, StringComparison.Ordinal);

        // Aditivas na membership: o cofre PESSOAL continua com WrappedWk nulo e WkVersion 0 — nada
        // que já existe muda de comportamento.
        Assert.Matches("ALTER TABLE memberships\\s+ADD \"WrappedWk\" bytea", sql);
        Assert.Matches("ALTER TABLE memberships\\s+ADD \"WkVersion\" integer", sql);
    }

    /// <summary>
    /// Espelha o guarda do <c>SecretEnvelope</c>: token de concorrência é comportamento de MODELO, e
    /// a suíte roda no InMemory. Esta asserção garante que o provider de PRODUÇÃO monta o mesmo
    /// modelo — senão alguém removeria a marcação e veria a suíte verde por acidente.
    /// </summary>
    [Fact]
    public void Invite_TemTokenDeConcorrencia_NoProviderDeProducao()
    {
        using var db = OfflineNpgsqlContext();
        var invite = db.Model.FindEntityType(typeof(InviteEntity));
        Assert.NotNull(invite);

        Assert.True(
            invite.FindProperty(nameof(InviteEntity.AcceptedAt))!.IsConcurrencyToken,
            "AcceptedAt deixou de ser token de concorrência — a linha do convite volta a poder ser "
            + "remarcada por um aceite que perdeu a corrida.");
    }
}
