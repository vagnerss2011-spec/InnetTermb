using System.IO;
using System.Security.Cryptography;

using RemoteOps.Security.Account;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

using Xunit;

namespace RemoteOps.UnitTests.Security;

/// <summary>
/// T3 — troca da RAIZ do cofre local: DPAPI (WDK aleatória por máquina) → AMK (WDK derivada,
/// portável entre devices). A prova é feita SÓ pela API pública do vault: o
/// <see cref="EnvelopeCipher"/> é internal e RemoteOps.Security não tem InternalsVisibleTo —
/// não afrouxamos visibilidade de cripto por causa de teste. Logo o padrão aqui é
/// "grava com a raiz antiga, lê com a raiz nova".
/// </summary>
public sealed class LocalVaultMigratorTests : IDisposable
{
    private const string Workspace = "ws-alpha";
    private const string Identity = "userA@machine1";
    private const string Actor = "op";

    // Um diretório por teste: os .bak que a migração gera caem aqui e somem no Dispose.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"remoteops-migr-{Guid.NewGuid():n}");
    private readonly byte[] _amk = RandomNumberGenerator.GetBytes(32);

    private string VaultPath => Path.Combine(_dir, "vault.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ── (a) + (d): o que estava selado sob a raiz DPAPI abre sob a raiz AMK, byte-idêntico ──

    [Fact]
    public async Task SecretsSealedUnderDpapiRoot_OpenUnderAmkRoot_WithIdenticalContent()
    {
        var store = new FileVaultStore(VaultPath);
        // Unicode/acentos/tamanho: prova round-trip byte-idêntico, não "parecido".
        (string Cred, string Secret)[] data =
        [
            ("switch-core", "s3nh@-do-switch"),
            ("ne8000", "çÃo-ü-éè-#!$%^&*()"),
            ("rb-borda", new string('x', 512)),
        ];

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        CredentialVault legacy = LegacyVault(store);
        foreach ((string cred, string secret) in data)
        {
            SecretEnvelope env = await StoreAsync(legacy, cred, secret);
            ids[cred] = env.EnvelopeId;
            Assert.Equal(secret, await ReadAsync(legacy, env.EnvelopeId)); // sanidade: abre sob DPAPI
        }

        VaultMigrationResult result = await Migrator(store).MigrateWorkspaceAsync(Workspace, _amk);

        Assert.Equal(3, result.Migrated);

        CredentialVault amk = AmkVault(store);
        foreach ((string cred, string secret) in data)
        {
            Assert.Equal(secret, await ReadAsync(amk, ids[cred]));
        }
    }

    [Fact]
    public async Task Migration_PreservesEnvelopeIdentityAndMetadata()
    {
        var store = new FileVaultStore(VaultPath);
        SecretEnvelope before = await StoreAsync(LegacyVault(store), "cred-1", "senha", type: "privateKey");

        await Migrator(store).MigrateWorkspaceAsync(Workspace, _amk);

        SecretEnvelope after = await RequireAsync(store, before.EnvelopeId);
        Assert.Equal(before.EnvelopeId, after.EnvelopeId);
        Assert.Equal(before.WorkspaceId, after.WorkspaceId);
        Assert.Equal(before.CredentialId, after.CredentialId);
        Assert.Equal(before.Type, after.Type);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Null(after.RevokedAt);

        // O material foi re-selado sob outra raiz e o Algorithm passa a dizer a verdade.
        Assert.NotEqual(before.WrappedCek, after.WrappedCek);
        Assert.Equal(VaultAlgorithms.DpapiRootedV1, before.Algorithm);
        Assert.Equal(VaultAlgorithms.AmkRootedV1, after.Algorithm);
    }

    [Fact]
    public async Task AfterMigration_NeitherLegacyRootNorWrongAmk_OpensTheSecret()
    {
        var store = new FileVaultStore(VaultPath);
        SecretEnvelope env = await StoreAsync(LegacyVault(store), "cred-1", "senha");

        await Migrator(store).MigrateWorkspaceAsync(Workspace, _amk);

        // A raiz REALMENTE trocou: a WDK antiga (DPAPI) não abre mais (tag GCM não bate).
        await Assert.ThrowsAnyAsync<CryptographicException>(() => ReadAsync(LegacyVault(store), env.EnvelopeId));
        // E o cofre não virou texto claro: outra AMK também não abre.
        var wrongAmk = new CredentialVault(
            store, new AmkWorkspaceKeyRing(RandomNumberGenerator.GetBytes(32)), NullVaultAuditSink.Instance);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => ReadAsync(wrongAmk, env.EnvelopeId));
    }

    // ── (b) idempotência ──

    [Fact]
    public async Task Migration_IsIdempotent_SecondRunRewritesNothing()
    {
        var store = new FileVaultStore(VaultPath);
        SecretEnvelope env = await StoreAsync(LegacyVault(store), "cred-1", "senha-secreta");
        LocalVaultMigrator migrator = Migrator(store);

        VaultMigrationResult first = await migrator.MigrateWorkspaceAsync(Workspace, _amk);
        SecretEnvelope afterFirst = await RequireAsync(store, env.EnvelopeId);

        VaultMigrationResult second = await migrator.MigrateWorkspaceAsync(Workspace, _amk);
        SecretEnvelope afterSecond = await RequireAsync(store, env.EnvelopeId);

        Assert.Equal(1, first.Migrated);
        Assert.Equal(0, second.Migrated);
        Assert.Null(second.BackupPath); // no-op não faz backup

        // Byte-idêntico: a 2ª execução não re-selou nem corrompeu nada.
        Assert.Equal(afterFirst.WrappedCek, afterSecond.WrappedCek);
        Assert.Equal(afterFirst.Ciphertext, afterSecond.Ciphertext);
        Assert.Equal(afterFirst.Nonce, afterSecond.Nonce);
        Assert.Equal("senha-secreta", await ReadAsync(AmkVault(store), env.EnvelopeId));
    }

    // ── (c) falha no meio não perde dados ──

    [Fact]
    public async Task FailureMidWrite_LosesNoData_AndResumesOnRerun()
    {
        var store = new FileVaultStore(VaultPath);
        CredentialVault legacy = LegacyVault(store);
        var ids = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            ids.Add((await StoreAsync(legacy, $"cred-{i}", $"senha-{i}")).EnvelopeId);
        }

        // Disco morre na 3ª escrita: parte migrada, parte não.
        var failing = new FailOnNthSaveStore(store, failOnSave: 3);
        await Assert.ThrowsAsync<IOException>(
            () => new LocalVaultMigrator(failing, LegacyKeyRing(store)).MigrateWorkspaceAsync(Workspace, _amk));

        // A migração NÃO é dada como concluída...
        Assert.NotEqual(VaultKeyRooting.AmkDerived, await store.LoadKeyRootingAsync(Workspace));

        // ...e nenhum segredo se perdeu: cada envelope ainda abre sob a raiz que ele declara.
        for (int i = 0; i < 4; i++)
        {
            SecretEnvelope env = await RequireAsync(store, ids[i]);
            CredentialVault vault = env.Algorithm == VaultAlgorithms.AmkRootedV1 ? AmkVault(store) : LegacyVault(store);
            Assert.Equal($"senha-{i}", await ReadAsync(vault, ids[i]));
        }

        // Re-executar (com o store são) retoma de onde parou e conclui.
        VaultMigrationResult resumed = await Migrator(store).MigrateWorkspaceAsync(Workspace, _amk);
        Assert.True(resumed.Migrated > 0);
        Assert.Equal(VaultKeyRooting.AmkDerived, await store.LoadKeyRootingAsync(Workspace));

        CredentialVault amk = AmkVault(store);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal($"senha-{i}", await ReadAsync(amk, ids[i]));
        }
    }

    [Fact]
    public async Task Migration_BacksUpTheVaultBeforeRewriting_AndTheBackupIsRestorable()
    {
        var store = new FileVaultStore(VaultPath);
        SecretEnvelope env = await StoreAsync(LegacyVault(store), "cred-1", "senha-original");

        VaultMigrationResult result = await Migrator(store).MigrateWorkspaceAsync(Workspace, _amk);

        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));

        // O backup é o cofre PRÉ-migração de verdade: reabre com a raiz DPAPI antiga.
        var restored = new FileVaultStore(result.BackupPath!);
        Assert.Equal("senha-original", await ReadAsync(LegacyVault(restored), env.EnvelopeId));
    }

    [Fact]
    public async Task RevokedEnvelopes_AreSkipped_AndDoNotBreakMigration()
    {
        var store = new FileVaultStore(VaultPath);
        CredentialVault legacy = LegacyVault(store);
        SecretEnvelope live = await StoreAsync(legacy, "cred-live", "viva");
        SecretEnvelope dead = await StoreAsync(legacy, "cred-dead", "revogada");
        await legacy.RevokeAsync(dead.EnvelopeId, new VaultAccessContext { ActorUserId = Actor });

        VaultMigrationResult result = await Migrator(store).MigrateWorkspaceAsync(Workspace, _amk);

        // Tombstone não tem material pra re-selar: é pulado, não derruba a migração.
        Assert.Equal(1, result.Migrated);
        Assert.Equal(1, result.SkippedRevoked);
        Assert.Equal("viva", await ReadAsync(AmkVault(store), live.EnvelopeId));
        Assert.NotNull((await RequireAsync(store, dead.EnvelopeId)).RevokedAt);
    }

    [Fact]
    public async Task EmptyWorkspace_IsMarkedAmkRooted_WithoutBackup()
    {
        // Instalação nova: nada a migrar, mas a raiz já fica registrada como AMK.
        var store = new FileVaultStore(VaultPath);

        VaultMigrationResult result = await Migrator(store).MigrateWorkspaceAsync(Workspace, _amk);

        Assert.Equal(0, result.Migrated);
        Assert.Null(result.BackupPath);
        Assert.Equal(VaultKeyRooting.AmkDerived, await store.LoadKeyRootingAsync(Workspace));
    }

    [Fact]
    public async Task Migration_TouchesOnlyTheTargetWorkspace()
    {
        var store = new FileVaultStore(VaultPath);
        CredentialVault legacy = LegacyVault(store);
        SecretEnvelope alpha = await StoreAsync(legacy, "cred-a", "senha-alpha");
        SecretEnvelope beta = await StoreAsync(legacy, "cred-b", "senha-beta", workspaceId: "ws-beta");

        await Migrator(store).MigrateWorkspaceAsync(Workspace, _amk);

        Assert.Equal("senha-alpha", await ReadAsync(AmkVault(store), alpha.EnvelopeId));
        // ws-beta ficou intacto sob a raiz antiga.
        Assert.Equal(VaultAlgorithms.DpapiRootedV1, (await RequireAsync(store, beta.EnvelopeId)).Algorithm);
        Assert.Equal("senha-beta", await ReadAsync(LegacyVault(store), beta.EnvelopeId));
        Assert.Null(await store.LoadKeyRootingAsync("ws-beta"));
    }

    [Fact]
    public async Task MissingLegacyWorkspaceKey_FailsLoudly_WithoutTouchingTheVault()
    {
        var store = new FileVaultStore(VaultPath);
        SecretEnvelope env = await StoreAsync(LegacyVault(store), "cred-1", "senha");

        // WDK antiga sumiu (key store vazio) mas há segredos selados com ela: a migração tem de
        // parar — jamais criar uma WDK nova por baixo (o GetOrCreate faria isso e mascararia a perda).
        var orphanKeyRing = new WorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), new FakeKeyProtector(Identity));
        await Assert.ThrowsAsync<VaultException>(
            () => new LocalVaultMigrator(store, orphanKeyRing).MigrateWorkspaceAsync(Workspace, _amk));

        // O envelope continua íntegro sob a raiz original.
        Assert.Equal(VaultAlgorithms.DpapiRootedV1, (await RequireAsync(store, env.EnvelopeId)).Algorithm);
        Assert.Equal("senha", await ReadAsync(LegacyVault(store), env.EnvelopeId));
    }

    [Fact]
    public async Task WrongAmkLength_IsRejected()
    {
        var store = new FileVaultStore(VaultPath);
        await Assert.ThrowsAsync<ArgumentException>(
            () => Migrator(store).MigrateWorkspaceAsync(Workspace, new byte[16]));
    }

    // ── Helpers ──

    private LocalVaultMigrator Migrator(FileVaultStore store) => new(store, LegacyKeyRing(store));

    // Raiz LEGADA: WDK aleatória protegida pelo "DPAPI". O FakeKeyProtector modela a ligação
    // usuário/máquina de forma determinística e cross-platform (mesmo dublê dos testes de vault).
    private static WorkspaceKeyRing LegacyKeyRing(FileVaultStore store) => new(store, new FakeKeyProtector(Identity));

    private static CredentialVault LegacyVault(FileVaultStore store) =>
        new(store, LegacyKeyRing(store), NullVaultAuditSink.Instance);

    // Raiz NOVA: WDK = HKDF(AMK, workspaceId).
    private CredentialVault AmkVault(FileVaultStore store) =>
        new(store, new AmkWorkspaceKeyRing(_amk), NullVaultAuditSink.Instance);

    private static Task<SecretEnvelope> StoreAsync(
        CredentialVault vault, string credentialId, string secret, string type = "password", string workspaceId = Workspace) =>
        vault.StoreAsync(
            new VaultStoreRequest { WorkspaceId = workspaceId, CredentialId = credentialId, Type = type, ActorUserId = Actor },
            secret.AsMemory());

    private static async Task<string> ReadAsync(CredentialVault vault, string envelopeId)
    {
        using VaultSecret secret = await vault.RetrieveAsync(envelopeId, new VaultAccessContext { ActorUserId = Actor });
        return secret.RevealString();
    }

    private static async Task<SecretEnvelope> RequireAsync(FileVaultStore store, string envelopeId) =>
        await store.GetAsync(envelopeId) ?? throw new InvalidOperationException($"Envelope '{envelopeId}' sumiu do store.");

    /// <summary>Store que explode na N-ésima escrita de envelope — simula falha no meio da migração.</summary>
    private sealed class FailOnNthSaveStore : IVaultMigrationStore
    {
        private readonly IVaultMigrationStore _inner;
        private readonly int _failOnSave;
        private int _saves;

        public FailOnNthSaveStore(IVaultMigrationStore inner, int failOnSave)
        {
            _inner = inner;
            _failOnSave = failOnSave;
        }

        public Task SaveAsync(SecretEnvelope envelope, CancellationToken ct = default) =>
            ++_saves == _failOnSave
                ? throw new IOException("disco cheio (simulado)")
                : _inner.SaveAsync(envelope, ct);

        public Task<SecretEnvelope?> GetAsync(string envelopeId, CancellationToken ct = default) =>
            _inner.GetAsync(envelopeId, ct);

        public Task DeleteAsync(string envelopeId, CancellationToken ct = default) =>
            _inner.DeleteAsync(envelopeId, ct);

        public Task<IReadOnlyList<SecretEnvelope>> ListEnvelopesAsync(string workspaceId, CancellationToken ct = default) =>
            _inner.ListEnvelopesAsync(workspaceId, ct);

        public Task<string> CreateBackupAsync(string reason, CancellationToken ct = default) =>
            _inner.CreateBackupAsync(reason, ct);

        public Task<VaultKeyRooting?> LoadKeyRootingAsync(string workspaceId, CancellationToken ct = default) =>
            _inner.LoadKeyRootingAsync(workspaceId, ct);

        public Task SaveKeyRootingAsync(string workspaceId, VaultKeyRooting rooting, CancellationToken ct = default) =>
            _inner.SaveKeyRootingAsync(workspaceId, rooting, ct);
    }
}
