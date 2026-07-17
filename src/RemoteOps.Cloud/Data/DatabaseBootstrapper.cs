using Microsoft.EntityFrameworkCore;

namespace RemoteOps.Cloud.Data;

/// <summary>
/// Aplica as migrations no startup (spec cloud-sync-e2ee-phase1 §9).
///
/// Por que no startup e não num passo separado: o deploy do operador é um
/// `docker compose up` só — um passo manual de `database update` seria mais uma
/// chance de subir a API contra um schema velho. Single-node por design; quando
/// houver mais de uma réplica, isto vira job de release (duas réplicas migrando
/// juntas é corrida).
/// </summary>
public static class DatabaseBootstrapper
{
    /// <summary>
    /// Migra quando o provider é relacional. Devolve <c>false</c> (sem migrar) para
    /// providers sem migrations — é o caso do InMemory usado nos testes.
    /// </summary>
    public static bool MigrateIfRelational(AppDbContext db, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);

        if (!db.Database.IsRelational())
        {
            logger.LogInformation("Provider não relacional ({Provider}); migrations ignoradas.",
                db.Database.ProviderName);
            return false;
        }

        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Banco já está na última migration.");
            return true;
        }

        logger.LogInformation("Aplicando {Count} migration(s): {Migrations}",
            pending.Count, string.Join(", ", pending));
        db.Database.Migrate();
        logger.LogInformation("Migrations aplicadas.");
        return true;
    }
}
