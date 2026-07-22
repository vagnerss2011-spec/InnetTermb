using System.Security.Cryptography;

using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

namespace RemoteOps.Security.Account;

/// <summary>
/// Troca a RAIZ do cofre local: os segredos selados sob a WDK aleatória protegida por DPAPI (raiz
/// presa à máquina — por isso um 2º device baixa os metadados mas não decifra as senhas) passam a
/// ser selados sob a WDK derivada da AMK portável. Só a RAIZ muda: envelopeId, credentialId, tipo,
/// versão, CreatedAt e o conteúdo em claro de cada segredo permanecem idênticos, e a camada
/// CEK→segredo continua sendo o mesmo <see cref="EnvelopeCipher"/>. Ver spec §7.
///
/// <para><b>Por que mora em RemoteOps.Security e não em RemoteOps.Desktop:</b> re-selar
/// PRESERVANDO o envelopeId exige o <see cref="EnvelopeCipher"/> e a construção do AAD, ambos
/// internal deste assembly. Pôr o migrador no Desktop obrigaria a afrouxar a visibilidade do núcleo
/// de cripto só por conveniência de camada — e ele não depende de nada de UI/infra de lá.</para>
///
/// <para><b>Idempotente e retomável.</b> O marcador por envelope é o <c>Algorithm</c> (carimbado
/// pela chave que selou, ver <see cref="WorkspaceKey.AlgorithmId"/>); a conclusão do workspace é
/// gravada via <see cref="IVaultRootingStore.SaveKeyRootingAsync"/>. Rodar 2x é no-op. Morrer no meio
/// deixa cada envelope íntegro sob a raiz que ele próprio declara — nada é perdido — e a
/// re-execução migra só o que faltou.</para>
/// </summary>
public sealed class LocalVaultMigrator
{
    private const int AmkSize = 32;
    private const string BackupReason = "pre-amk";

    private readonly IVaultMigrationStore _store;
    private readonly IWorkspaceKeyRing _legacyKeyRing;

    /// <param name="store">Cofre local a migrar.</param>
    /// <param name="legacyKeyRing">A raiz ANTIGA (DPAPI) — só para abrir o que já está selado.</param>
    public LocalVaultMigrator(IVaultMigrationStore store, IWorkspaceKeyRing legacyKeyRing)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(legacyKeyRing);
        _store = store;
        _legacyKeyRing = legacyKeyRing;
    }

    /// <summary>
    /// Re-sela todos os segredos vivos do workspace sob a raiz AMK. Seguro para chamar em todo
    /// login/startup: se já estiver migrado, não toca em nada.
    /// </summary>
    /// <param name="amk">A AMK (32B) desembrulhada. O migrador não a retém.</param>
    /// <exception cref="VaultException">Há segredos selados mas a WDK antiga sumiu.</exception>
    public async Task<VaultMigrationResult> MigrateWorkspaceAsync(
        string workspaceId,
        ReadOnlyMemory<byte> amk,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (amk.Length != AmkSize)
        {
            throw new ArgumentException($"A AMK precisa ter {AmkSize} bytes (recebidos {amk.Length}).", nameof(amk));
        }

        // Fast-path: workspace já registrado como AMK-rooted. Não enumera, não faz backup.
        if (await _store.LoadKeyRootingAsync(workspaceId, ct).ConfigureAwait(false) == VaultKeyRooting.AmkDerived)
        {
            return new VaultMigrationResult(workspaceId, Migrated: 0, AlreadyRooted: 0, SkippedRevoked: 0, BackupPath: null);
        }

        IReadOnlyList<SecretEnvelope> all = await _store.ListEnvelopesAsync(workspaceId, ct).ConfigureAwait(false);

        // Tombstones não têm material pra re-selar (o CredentialVault zera os campos ao revogar):
        // tentar abri-los quebraria a migração por um envelope que já não guarda segredo nenhum.
        int revoked = all.Count(e => e.RevokedAt is not null);
        List<SecretEnvelope> live = all.Where(e => e.RevokedAt is null).ToList();
        int alreadyRooted = live.Count(e => e.Algorithm == VaultAlgorithms.AmkRootedV1);
        List<SecretEnvelope> pending = live.Where(e => e.Algorithm != VaultAlgorithms.AmkRootedV1).ToList();

        if (pending.Count == 0)
        {
            // Cofre novo, ou retomada cujas escritas já tinham passado: só falta registrar a raiz.
            await _store.SaveKeyRootingAsync(workspaceId, VaultKeyRooting.AmkDerived, ct).ConfigureAwait(false);
            return new VaultMigrationResult(workspaceId, 0, alreadyRooted, revoked, BackupPath: null);
        }

        using WorkspaceKey legacyKey = await _legacyKeyRing.TryGetWorkspaceKeyAsync(workspaceId, ct).ConfigureAwait(false)
            ?? throw new VaultException(
                $"O workspace '{workspaceId}' tem {pending.Count} segredo(s) selado(s) mas nenhuma WDK local: " +
                "a raiz antiga sumiu e a migração não conseguiria abrir os envelopes.");

        // A raiz nova é construída aqui (e não recebida pronta) para garantir que o migrador sela
        // EXATAMENTE com o mesmo rooting que o app vai usar pra abrir depois — sem chance de drift.
        using var newRoot = new AmkWorkspaceKeyRing(amk.Span);
        using WorkspaceKey newKey = await newRoot.GetOrCreateWorkspaceKeyAsync(workspaceId, ct).ConfigureAwait(false);

        // Fase 1 — cripto, SEM escrever: abre tudo com a raiz antiga e re-sela com a nova em
        // memória. Se um envelope não abrir, estoura aqui, com o cofre ainda intacto no disco.
        var resealed = new List<SecretEnvelope>(pending.Count);
        foreach (SecretEnvelope envelope in pending)
        {
            ct.ThrowIfCancellationRequested();
            resealed.Add(Reseal(envelope, legacyKey.Key.Span, newKey.Key.Span, newRoot.AlgorithmId));
        }

        // Backup imediatamente antes da primeira escrita — a rede de segurança do operador.
        string backupPath = await _store.CreateBackupAsync(BackupReason, ct).ConfigureAwait(false);

        // Fase 2 — escrita. Cada envelope já carrega o carimbo da raiz nova, então uma morte no
        // meio deixa os escritos válidos sob a AMK e os demais válidos sob o DPAPI: nada se perde,
        // e a re-execução recomeça exatamente de onde parou.
        foreach (SecretEnvelope envelope in resealed)
        {
            await _store.SaveAsync(envelope, ct).ConfigureAwait(false);
        }

        // Só agora o workspace é "AMK-rooted": marcar antes tornaria uma falha parcial invisível
        // (o fast-path pularia os envelopes que ficaram pra trás na raiz antiga).
        await _store.SaveKeyRootingAsync(workspaceId, VaultKeyRooting.AmkDerived, ct).ConfigureAwait(false);

        return new VaultMigrationResult(workspaceId, resealed.Count, alreadyRooted, revoked, backupPath);
    }

    private static SecretEnvelope Reseal(
        SecretEnvelope envelope,
        ReadOnlySpan<byte> legacyKey,
        ReadOnlySpan<byte> newKey,
        string algorithmId)
    {
        // Dois AADs, e não um: o de ABERTURA sai do envelope como ele está no disco (raiz antiga), o
        // de SELAGEM sai da raiz de DESTINO. Hoje os dois dão exatamente o mesmo resultado — DPAPI e
        // AMK compartilham o formato congelado — mas desde que o WkRootedV1 existe o AAD é função do
        // ESQUEMA. Reusar um só AAD faria a primeira migração para uma raiz de formato diferente
        // gravar envelopes que nunca mais abrem, e sem erro nenhum na hora da migração.
        byte[] openAad = EnvelopeCipher.BuildAad(envelope);
        byte[] sealAad = EnvelopeCipher.BuildAad(
            envelope.EnvelopeId,
            envelope.WorkspaceId,
            envelope.Version,
            envelope.Type,
            envelope.CredentialId,
            algorithmId);
        byte[] wrapAad = EnvelopeCipher.BuildWrapAad(envelope.WorkspaceId);

        byte[] plaintext = EnvelopeCipher.Open(legacyKey, envelope, openAad, wrapAad);
        try
        {
            EnvelopeCipher.SealedSecret payload = EnvelopeCipher.Seal(newKey, plaintext, sealAad, wrapAad);
            return envelope with
            {
                Algorithm = algorithmId,
                WrappedCek = payload.WrappedCek,
                CekNonce = payload.CekNonce,
                CekTag = payload.CekTag,
                Ciphertext = payload.Ciphertext,
                Nonce = payload.Nonce,
                Tag = payload.Tag,
            };
        }
        finally
        {
            // O segredo em claro passou pela memória do migrador: zere na saída (é o único ponto
            // de toda a Fase 1 em que plaintext de credencial existe fora de um VaultSecret).
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

/// <summary>Resumo de uma migração de raiz — para log/telemetria e para os testes.</summary>
/// <param name="Migrated">Envelopes re-selados AGORA sob a AMK.</param>
/// <param name="AlreadyRooted">Já estavam sob a AMK (retomada). Sempre 0 no fast-path, que não enumera.</param>
/// <param name="SkippedRevoked">Tombstones ignorados (sem material pra re-selar).</param>
/// <param name="BackupPath">Backup criado, ou <c>null</c> quando não houve reescrita.</param>
public sealed record VaultMigrationResult(
    string WorkspaceId,
    int Migrated,
    int AlreadyRooted,
    int SkippedRevoked,
    string? BackupPath);
