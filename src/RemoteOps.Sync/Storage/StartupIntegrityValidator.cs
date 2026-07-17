using Microsoft.Data.Sqlite;

namespace RemoteOps.Sync.Storage;

/// <summary>Desfecho da verificação de integridade na reabertura (Fase 2, item C).</summary>
public enum IntegrityOutcome
{
    /// <summary>Banco íntegro e cursores consistentes — nada a fazer.</summary>
    Healthy,

    /// <summary>Achou inconsistência e auto-recuperou (checkpoint do WAL e/ou clamp de cursor).</summary>
    Recovered,

    /// <summary>Achou algo que NÃO deu pra corrigir com segurança (corrupção grave, ou backup falhou):
    /// sinalizar ao operador. Mesmo assim NÃO trava o boot (fail-open).</summary>
    Warned,
}

/// <summary>Resultado da verificação: desfecho + mensagens (pt-BR, sem segredos) pra logar/avisar.</summary>
public sealed record IntegrityReport(IntegrityOutcome Outcome, IReadOnlyList<string> Messages)
{
    /// <summary>O boot deve mostrar um aviso ao operador? (Só quando <see cref="IntegrityOutcome.Warned"/>.)</summary>
    public bool ShouldWarnOperator => Outcome == IntegrityOutcome.Warned;
}

/// <summary>
/// Faz backup do arquivo do banco ANTES de qualquer reparo (Fase 2, item C: "não corrompa nada; faça
/// backup antes de qualquer reparo"). Injetável pra os testes provarem "backup antes do clamp" e
/// "sem backup, sem reparo".
/// </summary>
public interface IIntegrityBackup
{
    /// <summary>Copia o banco pra um arquivo de backup e devolve o caminho dele.</summary>
    Task<string> BackupAsync(string dbPath, CancellationToken ct = default);
}

/// <summary>Backup por cópia de arquivo (o banco + os sidecars <c>-wal</c>/<c>-shm</c> se existirem).</summary>
public sealed class FileIntegrityBackup : IIntegrityBackup
{
    public async Task<string> BackupAsync(string dbPath, CancellationToken ct = default)
    {
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        string backupPath = $"{dbPath}.bak-{stamp}";

        // Copia o arquivo cifrado como está (o backup continua protegido pela mesma chave SQLCipher).
        await CopyIfExistsAsync(dbPath, backupPath, ct);
        await CopyIfExistsAsync(dbPath + "-wal", backupPath + "-wal", ct);
        await CopyIfExistsAsync(dbPath + "-shm", backupPath + "-shm", ct);
        return backupPath;
    }

    private static async Task CopyIfExistsAsync(string source, string destination, CancellationToken ct)
    {
        if (!File.Exists(source))
        {
            return;
        }

        using FileStream src = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using FileStream dst = File.Create(destination);
        await src.CopyToAsync(dst, ct);
    }
}

/// <summary>
/// Verifica a integridade do banco SQLCipher na REABERTURA, antes de o app confiar no cofre/outbox
/// (Fase 2, item C). Roda <c>PRAGMA quick_check</c>, força a recuperação do WAL (checkpoint) e
/// confere a consistência dos cursores do outbox (<c>outbox_cursor</c> não pode estar além do
/// <c>MAX(local_outbox.id)</c> — senão uma edição pendente seria PULADA, "ficaria sem syncar").
///
/// <para><b>Fail-open, sempre:</b> nenhum caminho lança. Achou e recuperou → <see cref="IntegrityOutcome.Recovered"/>;
/// achou e não deu pra corrigir com segurança (corrupção grave, backup falhou) → <see cref="IntegrityOutcome.Warned"/>
/// (o boot avisa o operador mas NÃO trava). Banco íntegro → <see cref="IntegrityOutcome.Healthy"/>, silencioso.</para>
///
/// <para><b>Backup antes de reparar:</b> o único reparo que MUTA dados (o clamp do cursor) só acontece
/// depois de um backup bem-sucedido do banco. Se o backup falhar, o reparo é adiado (não se corrompe
/// nada) e o operador é avisado. O checkpoint do WAL não é "reparo" — é a recuperação de rotina que
/// qualquer abertura faz, preservando os frames já commitados —, então não exige backup.</para>
/// </summary>
public sealed class StartupIntegrityValidator
{
    private readonly IIntegrityBackup _backup;

    public StartupIntegrityValidator(IIntegrityBackup? backup = null)
    {
        _backup = backup ?? new FileIntegrityBackup();
    }

    public async Task<IntegrityReport> ValidateAndRecoverAsync(
        WorkspaceContext workspace, string dbPath, CancellationToken ct = default)
    {
        var messages = new List<string>();
        IntegrityOutcome outcome = IntegrityOutcome.Healthy;

        try
        {
            using SqliteConnection conn = await workspace.OpenConnectionAsync(ct);

            // 1) quick_check: mais barato que integrity_check e suficiente pra pegar corrupção real.
            string quick = await QuickCheckAsync(conn, ct);
            if (!string.Equals(quick, "ok", StringComparison.OrdinalIgnoreCase))
            {
                // Corrupção grave: NÃO tenta reparo destrutivo. Backup pra preservar evidência e avisa.
                string? backupNote = await TryBackupAsync(dbPath, ct);
                messages.Add(
                    "Verificação de integridade do banco falhou (quick_check). Seus dados podem estar "
                    + "corrompidos neste computador. " + (backupNote ?? "Não foi possível fazer backup.")
                    + " Recomendado: entrar na conta em outro dispositivo e/ou abrir um chamado.");
                return new IntegrityReport(IntegrityOutcome.Warned, messages);
            }

            // 2) Recuperação do WAL: força os frames commitados pro banco principal e trunca o WAL.
            // No-op se o banco não estiver em modo WAL. Não é "reparo" (preserva dados) — sem backup.
            if (await TryCheckpointWalAsync(conn, ct))
            {
                outcome = IntegrityOutcome.Recovered;
                messages.Add("Recuperação do WAL aplicada na reabertura (checkpoint).");
            }

            // 3) Consistência dos cursores do outbox: se algum aponta ALÉM da última linha do outbox,
            // uma mudança pendente seria pulada. Backup → clamp pro MAX real (idempotente no servidor).
            CursorRepair repair = await CheckOutboxCursorsAsync(conn, ct);
            if (repair.Inconsistencies.Count > 0)
            {
                string? backupNote = await TryBackupAsync(dbPath, ct);
                if (backupNote is null)
                {
                    // Sem backup não se toca em nada (Fase 2, item C). Avisa e deixa como está — o
                    // próximo boot tenta de novo; nada foi perdido nem corrompido.
                    messages.Add(
                        "Cursor de sincronização inconsistente, mas não foi possível fazer backup — "
                        + "correção adiada por segurança. Nada foi alterado.");
                    return new IntegrityReport(IntegrityOutcome.Warned, messages);
                }

                await ClampCursorsAsync(conn, repair, ct);
                outcome = IntegrityOutcome.Recovered;
                messages.Add(
                    $"Cursor do outbox estava à frente dos dados em {repair.Inconsistencies.Count} "
                    + $"workspace(s) — corrigido para reenviar o pendente. {backupNote}");
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelado no shutdown do boot — não é falha de integridade.
            throw;
        }
        catch (Exception)
        {
            // Fail-open (Fase 2, item C: "boot nunca trava"). Sem detalhe da exceção (ADR-013):
            // a verificação em si nunca pode ser o motivo de o app não abrir.
            return new IntegrityReport(
                IntegrityOutcome.Warned,
                ["A verificação de integridade não pôde ser concluída; o app abriu mesmo assim."]);
        }

        return new IntegrityReport(outcome, messages);
    }

    private static async Task<string> QuickCheckAsync(SqliteConnection conn, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check;";
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString() ?? "ok";
    }

    private static async Task<bool> TryCheckpointWalAsync(SqliteConnection conn, CancellationToken ct)
    {
        try
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            // Retorna (busy, log, checkpointed): log > 0 = havia frames de WAL a recuperar.
            using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct) && !reader.IsDBNull(1))
            {
                return reader.GetInt64(1) > 0;
            }
        }
        catch (SqliteException)
        {
            // Modo não-WAL ou pragma indisponível: recuperação de WAL não se aplica.
        }

        return false;
    }

    private static async Task<CursorRepair> CheckOutboxCursorsAsync(SqliteConnection conn, CancellationToken ct)
    {
        // Guarda: num banco recém-criado (nunca sincronizou) as tabelas/coluna podem não existir. Sem
        // elas não há cursor pra estar inconsistente — nada a verificar.
        if (!await TableExistsAsync(conn, "local_outbox", ct)
            || !await TableExistsAsync(conn, "sync_cursor", ct)
            || !await ColumnExistsAsync(conn, "sync_cursor", "outbox_cursor", ct))
        {
            return new CursorRepair(0, []);
        }

        long maxOutboxId = await ScalarLongAsync(conn, "SELECT COALESCE(MAX(id), 0) FROM local_outbox;", ct);

        var offenders = new List<string>();
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT workspace_id, outbox_cursor FROM sync_cursor WHERE outbox_cursor > $max;";
            cmd.Parameters.AddWithValue("$max", maxOutboxId);
            using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                offenders.Add(reader.GetString(0));
            }
        }

        return new CursorRepair(maxOutboxId, offenders);
    }

    private static async Task ClampCursorsAsync(SqliteConnection conn, CursorRepair repair, CancellationToken ct)
    {
        // Clamp direto (bypassa o MAX() monotônico do metadata store DE PROPÓSITO): aqui a intenção é
        // justamente REGREDIR o cursor errado pro último id real, pra o próximo push reenviar o tail.
        foreach (string workspaceId in repair.Inconsistencies)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE sync_cursor SET outbox_cursor = $max WHERE workspace_id = $ws;";
            cmd.Parameters.AddWithValue("$max", repair.MaxOutboxId);
            cmd.Parameters.AddWithValue("$ws", workspaceId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<string?> TryBackupAsync(string dbPath, CancellationToken ct)
    {
        try
        {
            string path = await _backup.BackupAsync(dbPath, ct);
            return $"Backup criado em {Path.GetFileName(path)}.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null; // sem backup: quem chamou decide adiar o reparo.
        }
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string table, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $t LIMIT 1;";
        cmd.Parameters.AddWithValue("$t", table);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        // table é constante interna (não entrada de usuário) — sem injeção.
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // PRAGMA table_info: cid(0), name(1), ...
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private sealed record CursorRepair(long MaxOutboxId, IReadOnlyList<string> Inconsistencies);
}
