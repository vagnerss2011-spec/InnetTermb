using System.IO;

using Microsoft.Data.Sqlite;

using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;
using RemoteOps.Sync.Storage;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Fase 2, item C: na reabertura, antes de confiar no cofre/outbox, verificar integridade do banco e
/// consistência dos cursores — recuperar o que der, sinalizar o grave, e NUNCA travar o boot.
/// SQLCipher real via <see cref="SyncTestContext"/>.
/// </summary>
public sealed class StartupIntegrityValidatorTests
{
    private sealed class RecordingBackup : IIntegrityBackup
    {
        public int Calls { get; private set; }
        public bool ThrowOnBackup { get; init; }

        public Task<string> BackupAsync(string dbPath, CancellationToken ct = default)
        {
            Calls++;
            if (ThrowOnBackup)
            {
                throw new IOException("disco cheio (simulado)");
            }

            return Task.FromResult(dbPath + ".bak-test");
        }
    }

    private static SyncChange Change(string id)
        => new() { EntityType = "asset", EntityId = id, Operation = "created", Patch = [] };

    private static async Task SetOutboxCursorRawAsync(SyncTestContext ctx, string workspaceId, long value)
    {
        using SqliteConnection conn = await ctx.Workspace.OpenConnectionAsync();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sync_cursor SET outbox_cursor = $v WHERE workspace_id = $ws;";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$ws", workspaceId);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Fresh_Db_Passes_Silently()
    {
        // Banco recém-criado (nunca sincronizou): sem tabelas de outbox/cursor → nada a verificar.
        using var ctx = await SyncTestContext.CreateAsync("ws-int-fresh");
        var backup = new RecordingBackup();
        var validator = new StartupIntegrityValidator(backup);

        IntegrityReport report = await validator.ValidateAndRecoverAsync(ctx.Workspace, ctx.DbPath);

        Assert.Equal(IntegrityOutcome.Healthy, report.Outcome);
        Assert.False(report.ShouldWarnOperator);
        Assert.Equal(0, backup.Calls);
    }

    [Fact]
    public async Task Consistent_Cursor_Is_Healthy()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-int-ok");
        await ctx.Client.PushAsync([Change("e1"), Change("e2")]); // local_outbox id 1,2 → MAX = 2
        var metadata = new SqliteSyncMetadataStore(ctx.Workspace);
        await metadata.SaveOutboxCursorAsync("ws-int-ok", 2); // consistente: 2 == MAX

        var backup = new RecordingBackup();
        IntegrityReport report =
            await new StartupIntegrityValidator(backup).ValidateAndRecoverAsync(ctx.Workspace, ctx.DbPath);

        Assert.Equal(IntegrityOutcome.Healthy, report.Outcome);
        Assert.Equal(0, backup.Calls);
    }

    [Fact]
    public async Task Cursor_Ahead_Of_Data_Is_Detected_And_Clamped_After_Backup()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-int-ahead");
        await ctx.Client.PushAsync([Change("e1"), Change("e2")]); // MAX = 2
        var metadata = new SqliteSyncMetadataStore(ctx.Workspace);
        await metadata.SaveOutboxCursorAsync("ws-int-ahead", 2);
        await SetOutboxCursorRawAsync(ctx, "ws-int-ahead", 99); // corrompe: cursor à frente dos dados

        var backup = new RecordingBackup();
        IntegrityReport report =
            await new StartupIntegrityValidator(backup).ValidateAndRecoverAsync(ctx.Workspace, ctx.DbPath);

        Assert.Equal(IntegrityOutcome.Recovered, report.Outcome);
        Assert.Equal(1, backup.Calls); // backup ANTES do reparo
        SyncCursors cursors = await metadata.GetCursorsAsync("ws-int-ahead");
        Assert.Equal(2, cursors.OutboxCursor); // clampado pro MAX real
    }

    [Fact]
    public async Task Without_A_Successful_Backup_The_Cursor_Is_Not_Touched()
    {
        // "Não corrompa nada; faça backup antes de qualquer reparo": se o backup falha, o reparo é
        // adiado — o cursor fica como está, o operador é avisado, e o boot NÃO trava.
        using var ctx = await SyncTestContext.CreateAsync("ws-int-nobackup");
        await ctx.Client.PushAsync([Change("e1")]); // MAX = 1
        var metadata = new SqliteSyncMetadataStore(ctx.Workspace);
        await metadata.SaveOutboxCursorAsync("ws-int-nobackup", 1);
        await SetOutboxCursorRawAsync(ctx, "ws-int-nobackup", 50);

        var backup = new RecordingBackup { ThrowOnBackup = true };
        IntegrityReport report =
            await new StartupIntegrityValidator(backup).ValidateAndRecoverAsync(ctx.Workspace, ctx.DbPath);

        Assert.Equal(IntegrityOutcome.Warned, report.Outcome);
        Assert.True(report.ShouldWarnOperator);
        Assert.Equal(1, backup.Calls);
        SyncCursors cursors = await metadata.GetCursorsAsync("ws-int-nobackup");
        Assert.Equal(50, cursors.OutboxCursor); // intocado — nada foi corrompido
    }

    [Fact]
    public async Task Checkpoint_Runs_Without_Error_On_Healthy_Db()
    {
        // A recuperação do WAL é uma tentativa de rotina: num banco íntegro não deve quebrar nada.
        using var ctx = await SyncTestContext.CreateAsync("ws-int-wal");
        await ctx.Client.PushAsync([Change("e1")]);

        IntegrityReport report =
            await new StartupIntegrityValidator().ValidateAndRecoverAsync(ctx.Workspace, ctx.DbPath);

        // Sem WAL órfão, nada a recuperar → segue íntegro (sem aviso ao operador).
        Assert.False(report.ShouldWarnOperator);
    }
}
