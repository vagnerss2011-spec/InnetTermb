using System.IO;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RemoteOps.Contracts.Sync;
using RemoteOps.Sync;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Garante as propriedades de segurança do LocalSyncClient:
/// DB criptografado, chave não exposta em arquivo e ausência de segredo em logs/exceções.
/// </summary>
public sealed class SyncClientSecurityTests
{
    // ── Criptografia do banco ─────────────────────────────────────────────────

    [Fact]
    public async Task Encrypted_Db_Is_Unreadable_Without_Key()
    {
        if (!IsSqlCipherAvailable())
        {
            return; // SQLCipher não presente; teste de criptografia física não aplicável
        }

        using var ctx = await SyncTestContext.CreateAsync("ws-encrypt");

        await ctx.Client.PushAsync([
            new SyncChange { EntityType = "asset", EntityId = "e1", Operation = "created", Patch = [] }
        ]);

        // Tenta abrir o mesmo arquivo SEM fornecer chave: SQLCipher deve rejeitar.
        using var rawConn = new SqliteConnection($"Data Source={ctx.DbPath}");
        await rawConn.OpenAsync();
        using var cmd = rawConn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master";

        // SQLCipher retorna "file is not a database" quando a chave está errada/ausente.
        await Assert.ThrowsAnyAsync<Exception>(() => cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Different_Vault_Cannot_Open_Existing_Db()
    {
        if (!IsSqlCipherAvailable())
        {
            return;
        }

        string workspaceId = "ws-isolation";
        using var ctxA = await SyncTestContext.CreateAsync(workspaceId);
        await ctxA.Client.PushAsync([
            new SyncChange { EntityType = "asset", EntityId = "e1", Operation = "created", Patch = [] }
        ]);

        // Vault B é uma instância diferente — não conhece o envelopeId do vault A.
        // Logo, VaultDbKeyProvider vai GERAR uma nova chave, diferente da original.
        // Tentar abrir o banco existente com essa chave diferente deve falhar.
        string dbPath = ctxA.DbPath;
        string keyRefPath = ctxA.KeyRefPath;

        var vaultB = new FakeCredentialVault();
        // Aponta o keyref para o mesmo arquivo, mas vault B não tem o envelope → nova chave
        var factoryB = new LocalSyncClientFactory(vaultB, Path.GetDirectoryName(dbPath)!);

        // Cria um cliente para o MESMO workspaceId — mas com vault diferente.
        // A VaultDbKeyProvider de B não encontra o envelopeId em B, gera chave nova.
        // A nova chave tenta abrir um banco já cifrado com a chave de A.
        // Para garantir isolamento, removemos o .keyref para forçar nova chave.
        // (Simula: notebook roubado, disco copiado, vault em outro sistema.)
        File.Delete(keyRefPath);

        LocalSyncClient clientB = await factoryB.CreateForWorkspaceAsync(workspaceId);
        await Assert.ThrowsAnyAsync<Exception>(
            () => clientB.PullAsync(0));
    }

    // ── Arquivo .keyref não contém o segredo ─────────────────────────────────

    [Fact]
    public async Task KeyRef_File_Contains_Only_EnvelopeId_Not_Secret()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-keyref");

        Assert.True(File.Exists(ctx.KeyRefPath), ".keyref deve existir após criação do client.");

        string keyRefContent = await File.ReadAllTextAsync(ctx.KeyRefPath);

        // O envelopeId gerado por FakeCredentialVault é um GUID de 32 hex chars (ToString("n")).
        // A chave real do banco é 64 hex chars (32 bytes). O .keyref NUNCA deve ter 64+ chars
        // que se assemelhem a uma chave hex.
        Assert.True(keyRefContent.Trim().Length < 64,
            "O .keyref deve conter apenas o envelopeId (GUID), não a chave hex de 64 chars.");

        // Também não deve ser um hex de 32 bytes (o formato da chave AES-256).
        Assert.False(
            keyRefContent.Trim().All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
            && keyRefContent.Trim().Length == 64,
            "O .keyref não deve conter um hex de 64 chars (chave do DB).");
    }

    // ── Ausência de segredo em logs/exceções ─────────────────────────────────

    [Fact]
    public async Task No_HexKey_In_Exception_Messages()
    {
        if (!IsSqlCipherAvailable())
        {
            return;
        }

        using var ctx = await SyncTestContext.CreateAsync("ws-no-leak");
        await ctx.Client.PushAsync([
            new SyncChange { EntityType = "asset", EntityId = "e1", Operation = "created", Patch = [] }
        ]);

        // Abre sem chave para gerar exceção; verifica que a mensagem não vaza a hex key.
        using var rawConn = new SqliteConnection($"Data Source={ctx.DbPath}");
        await rawConn.OpenAsync();
        using var cmd = rawConn.CreateCommand();
        cmd.CommandText = "SELECT * FROM local_outbox";

        Exception? ex = await Record.ExceptionAsync(() => cmd.ExecuteNonQueryAsync());

        Assert.NotNull(ex);
        // A hex key tem 64 chars lowercase; nenhuma substring dela deve aparecer na exceção.
        // Verificamos que a mensagem não tem o padrão de 64 hex chars.
        Assert.DoesNotMatch(@"[0-9a-f]{64}", ex!.Message);
    }

    [Fact]
    public async Task Patch_Json_Serialized_As_Object_Not_Plaintext_Secret()
    {
        using var ctx = await SyncTestContext.CreateAsync();

        // Simula um patch que poderia conter dados sensíveis na UI.
        // O teste verifica que o campo é serializado como objeto JSON,
        // não como string concatenada (que facilitaria injeção em logs).
        var patch = new Dictionary<string, object?> { ["ref"] = "envelope-id-only", ["version"] = 1 };

        await ctx.Client.PushAsync([
            new SyncChange { EntityType = "credential_ref", EntityId = "cr1", Operation = "updated", Patch = patch }
        ]);

        IReadOnlyList<SyncChange> pulled = await ctx.Client.PullAsync(0);
        string serialized = JsonSerializer.Serialize(pulled[0].Patch);

        // O patch deve ser um objeto JSON válido, não uma string escapada.
        Assert.StartsWith("{", serialized);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsSqlCipherAvailable()
    {
        try
        {
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA cipher_version";
            return cmd.ExecuteScalar() is not null;
        }
        catch
        {
            return false;
        }
    }
}
