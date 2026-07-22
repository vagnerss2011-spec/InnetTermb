using System.Linq;
using System.Net.Http;
using System.Text;

using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Servidor de segredos em memória que imita o backend REAL
/// (<c>remoteops-cloud@a94fb1e</c>, <c>Secrets/SecretsService.cs</c>) nas regras que o cliente
/// enxerga. Não é um mock complacente de propósito: se o fake aceitasse o que o servidor recusa, o
/// transporte passaria aqui e quebraria em campo.
///
/// <para>O que é copiado do servidor, e por quê importa:</para>
/// <list type="bullet">
///   <item><b>Id/WorkspaceId são GUID</b> (<c>Guid.TryParse</c> ou 400) e voltam no formato "D"
///   (<c>Guid.ToString()</c>, com hífens) porque o <c>ToDto</c> real faz <c>e.Id.ToString()</c>. O
///   cliente gera o EnvelopeId como "N" (sem hífens) — este fake é o que prova que o round-trip
///   normaliza, e o AAD do GCM não muda.</item>
///   <item><b>Cursor monotônico por workspace</b>, atribuído a CADA upsert aceito.</item>
///   <item><b>version &lt;= atual → conflict SEM avançar cursor</b> (re-push do mesmo envelope é
///   no-op no servidor).</item>
///   <item><b>Envelope pertence a UM workspace pra sempre</b> → conflict em id repetido noutro.</item>
///   <item><b>base64 obrigatório e não-vazio</b>, EXCETO em tombstone (<c>revokedAt</c> preenchido),
///   o único caso em que o servidor aceita material vazio.</item>
///   <item><b>Revogação é só de ida</b>: upsert vivo por cima de tombstone → <c>envelope.revoked</c>.</item>
/// </list>
///
/// <para>E a garantia central da fase: <see cref="Forbid"/> registra plaintexts que NUNCA podem
/// aparecer no fio. Todo campo string que passa por aqui é varrido — o transporte só vê base64
/// opaco.</para>
/// </summary>
internal sealed class FakeSecretsApi : ISecretsApi
{
    private readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _cursors = new(StringComparer.Ordinal);
    private readonly List<string> _forbidden = [];

    /// <summary>Toda tentativa de upsert (inclusive as que viram conflict), na ordem.</summary>
    public List<string> UpsertAttempts { get; } = [];

    /// <summary>Upserts ACEITOS (status "ok") — é o que mede duplicação real.</summary>
    public List<string> Accepted { get; } = [];

    public int PullCalls { get; private set; }

    /// <summary>Quando setado, o próximo PullAsync com <c>since</c> igual ao valor estoura (queda de rede).</summary>
    public long? FailPullOnCursor { get; set; }

    /// <summary>
    /// Respostas de upsert forçadas, consumidas antes do comportamento normal. Serve pra encenar os
    /// conflitos que o servidor real emite mas que este fake não produz sozinho (<c>cursor.race-retry</c>,
    /// <c>envelope.workspace-mismatch</c>).
    /// </summary>
    public Queue<SecretUpsertResult> ForcedUpsertResults { get; } = new();

    /// <summary>Segredo em claro que não pode existir em NENHUM campo do fio.</summary>
    public void Forbid(string plaintext) => _forbidden.Add(plaintext);

    /// <summary>
    /// Planta uma linha no servidor SEM passar pelo upsert. Serve para encenar o que o caminho
    /// normal já barra e que mesmo assim precisa ser sobrevivido na descida: uma cópia VIVA de um
    /// envelope que este device já revogou.
    /// </summary>
    public void SeedRaw(SecretEnvelopeDto dto)
    {
        Guid id = RequireGuid(dto.Id, nameof(dto.Id));
        Guid ws = RequireGuid(dto.WorkspaceId, nameof(dto.WorkspaceId));
        _rows[id.ToString()] = new Row(
            id, ws, Convert.FromBase64String(dto.Ciphertext), dto.Nonce, dto.Tag, dto.WrappedCek,
            dto.CekNonce, dto.CekTag, dto.KeyVersion, dto.Version, NextCursor(ws),
            dto.Algorithm ?? "AES-256-GCM", dto.RevokedAt);
    }

    /// <summary>
    /// Planta uma linha cujo <c>algorithm</c> desce <b>NULO</b> — registro gravado antes de o campo
    /// existir, ou servidor antigo que não o ecoa. É o único caso em que o cliente precisa PALPITAR
    /// a raiz, e palpitar errado num cofre de time grava um envelope que nunca abre.
    /// </summary>
    public void SeedRawWithoutAlgorithm(SecretEnvelopeDto dto)
    {
        SeedRaw(dto);
        Guid id = RequireGuid(dto.Id, nameof(dto.Id));
        _rows[id.ToString()] = _rows[id.ToString()] with { Algorithm = null };
    }

    public Task<IReadOnlyList<SecretUpsertResult>> PushAsync(
        string workspaceId, IReadOnlyList<SecretEnvelopeDto> envelopes, CancellationToken ct = default)
    {
        var results = new List<SecretUpsertResult>();
        foreach (SecretEnvelopeDto dto in envelopes)
        {
            results.Add(Upsert(workspaceId, dto));
        }

        return Task.FromResult<IReadOnlyList<SecretUpsertResult>>(results);
    }

    public Task<SecretsPullResponse> PullAsync(
        string workspaceId, long since, int pageSize, CancellationToken ct = default)
    {
        PullCalls++;
        if (FailPullOnCursor == since)
        {
            // Uma queda no meio do pull: a retomada não pode perder nem duplicar envelope.
            throw new HttpRequestException("queda de rede simulada no pull de segredos");
        }

        Guid ws = RequireGuid(workspaceId, nameof(workspaceId));
        int limit = Math.Clamp(pageSize, 1, 1000);
        List<Row> ordered = _rows.Values
            .Where(r => r.WorkspaceId == ws && r.Cursor > since)
            .OrderBy(r => r.Cursor)
            .Take(limit + 1)
            .ToList();

        bool hasMore = ordered.Count > limit;
        List<Row> page = ordered.Take(limit).ToList();
        long nextCursor = page.Count > 0 ? page[^1].Cursor : since;

        IReadOnlyList<SecretEnvelopeDto> dtos = page.Select(r => r.ToDto()).ToList();
        return Task.FromResult(new SecretsPullResponse(dtos, nextCursor, hasMore));
    }

    private SecretUpsertResult Upsert(string workspaceId, SecretEnvelopeDto dto)
    {
        AssertOpaque(dto);
        UpsertAttempts.Add(dto.Id);

        if (ForcedUpsertResults.Count > 0)
        {
            return ForcedUpsertResults.Dequeue();
        }

        Guid ws = RequireGuid(workspaceId, nameof(workspaceId));
        Guid id = RequireGuid(dto.Id, nameof(dto.Id));
        Assert.True(dto.Version >= 1, "o servidor real exige version >= 1");
        Assert.False(string.IsNullOrWhiteSpace(dto.KeyVersion), "keyVersion é obrigatório no servidor real");
        Assert.True(dto.KeyVersion.Length <= 100, "keyVersion tem HasMaxLength(100) no servidor real");
        Assert.True((dto.Algorithm?.Length ?? 0) <= 100, "algorithm tem HasMaxLength(100) no servidor real");

        // Tombstone é o ÚNICO envelope que pode chegar sem material: o cofre zera tudo ao revogar.
        bool tombstone = dto.RevokedAt is not null;
        byte[] ciphertext = RequireBase64(dto.Ciphertext, nameof(dto.Ciphertext), tombstone);
        RequireBase64(dto.Nonce, nameof(dto.Nonce), tombstone);
        RequireBase64(dto.Tag, nameof(dto.Tag), tombstone);
        RequireBase64(dto.WrappedCek, nameof(dto.WrappedCek), tombstone);
        RequireBase64(dto.CekNonce, nameof(dto.CekNonce), tombstone);
        RequireBase64(dto.CekTag, nameof(dto.CekTag), tombstone);

        string key = id.ToString();
        if (_rows.TryGetValue(key, out Row? existing))
        {
            if (existing.WorkspaceId != ws)
            {
                return new SecretUpsertResult("conflict", 0, null, "envelope.workspace-mismatch");
            }

            if (existing.RevokedAt is not null && !tombstone)
            {
                // Revogação é caminho só de ida: aceitar o vivo de volta republicaria a senha velha.
                return new SecretUpsertResult("conflict", existing.Cursor, existing.Version, "envelope.revoked");
            }

            if (dto.Version <= existing.Version)
            {
                // Sem avanço de cursor: é isso que torna o re-push idempotente do lado servidor.
                return new SecretUpsertResult("conflict", existing.Cursor, existing.Version, "version.conflict");
            }
        }

        long cursor = NextCursor(ws);
        _rows[key] = new Row(id, ws, ciphertext, dto.Nonce, dto.Tag, dto.WrappedCek, dto.CekNonce,
            dto.CekTag, dto.KeyVersion, dto.Version, cursor, dto.Algorithm ?? "AES-256-GCM", dto.RevokedAt);
        Accepted.Add(dto.Id);
        return new SecretUpsertResult("ok", cursor, dto.Version);
    }

    private long NextCursor(Guid workspaceId)
    {
        string key = workspaceId.ToString();
        long next = (_cursors.TryGetValue(key, out long current) ? current : 0) + 1;
        _cursors[key] = next;
        return next;
    }

    /// <summary>
    /// A prova de que o transporte não decifra: se qualquer plaintext proibido aparecer em qualquer
    /// campo string do DTO, o segredo vazou pro fio.
    /// </summary>
    private void AssertOpaque(SecretEnvelopeDto dto)
    {
        string[] fields =
        [
            dto.Id, dto.WorkspaceId, dto.Ciphertext, dto.Nonce, dto.Tag,
            dto.WrappedCek, dto.CekNonce, dto.CekTag, dto.KeyVersion, dto.Algorithm ?? string.Empty,
        ];

        foreach (string plaintext in _forbidden)
        {
            foreach (string field in fields)
            {
                Assert.DoesNotContain(plaintext, field, StringComparison.Ordinal);
            }

            // Também na forma base64: o ciphertext não pode ser o plaintext "codificado".
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
            foreach (string field in fields)
            {
                Assert.DoesNotContain(encoded, field, StringComparison.Ordinal);
            }
        }
    }

    private static Guid RequireGuid(string value, string field)
    {
        Assert.True(Guid.TryParse(value, out Guid parsed), $"{field} precisa ser GUID no servidor real (veio '{value}')");
        return parsed;
    }

    private static byte[] RequireBase64(string value, string field, bool allowEmpty = false)
    {
        if (allowEmpty && string.IsNullOrEmpty(value))
        {
            return [];
        }

        Assert.False(string.IsNullOrWhiteSpace(value), $"{field} é obrigatório no servidor real");
        byte[] bytes = Convert.FromBase64String(value);
        Assert.True(bytes.Length > 0 || allowEmpty, $"{field} não pode ser vazio no servidor real");
        return bytes;
    }

    private sealed record Row(
        Guid Id, Guid WorkspaceId, byte[] Ciphertext, string Nonce, string Tag, string WrappedCek,
        string CekNonce, string CekTag, string KeyVersion, int Version, long Cursor, string? Algorithm,
        DateTimeOffset? RevokedAt)
    {
        /// <summary>
        /// Espelha o <c>SecretsService.ToDto</c>: Id/WorkspaceId voltam do Guid, logo no formato "D"
        /// (com hífens) — e NÃO no "N" que o cliente mandou.
        /// </summary>
        public SecretEnvelopeDto ToDto() => new(
            Id: Id.ToString(),
            WorkspaceId: WorkspaceId.ToString(),
            Ciphertext: Convert.ToBase64String(Ciphertext),
            Nonce: Nonce,
            Tag: Tag,
            WrappedCek: WrappedCek,
            CekNonce: CekNonce,
            CekTag: CekTag,
            KeyVersion: KeyVersion,
            Version: Version)
        {
            Algorithm = Algorithm,
            RevokedAt = RevokedAt,
        };
    }
}
