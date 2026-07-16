using System;
using System.Collections.Generic;
using System.Text.Json;

using RemoteOps.Security.Vault;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Contrato de fio entre o cliente e os endpoints REAIS de segredos do backend (T5).
///
/// <para><b>Por que DTOs espelhados aqui</b> (mesma razão do <see cref="AccountContractsWireTests"/>):
/// o backend E2EE vive no branch <c>feature/cloud-backend</c> (worktree <c>C:/dev/remoteops-cloud</c>)
/// e o <c>src/RemoteOps.Cloud</c> DESTE branch é a cópia pré-E2EE — referenciar o projeto local
/// provaria o contrato ERRADO. Os records abaixo são cópia literal de
/// <c>remoteops-cloud/src/RemoteOps.Cloud/Secrets/SecretsModels.cs</c> no commit a94fb1e.</para>
/// </summary>
public sealed class SecretsContractsWireTests
{
    private static readonly JsonSerializerOptions s_web = new(JsonSerializerDefaults.Web);

    // ── Espelho do backend T5 (remoteops-cloud@a94fb1e, Secrets/SecretsModels.cs) ────────

    private sealed record ServerSecretEnvelopeDto(
        string Id,
        string WorkspaceId,
        string Ciphertext,
        string Nonce,
        string Tag,
        string WrappedCek,
        string CekNonce,
        string CekTag,
        string KeyVersion,
        int Version)
    {
        public string? Algorithm { get; init; }
    }

    private sealed record ServerSecretsPullResponse(
        IReadOnlyList<ServerSecretEnvelopeDto> Envelopes,
        long NextCursor,
        bool HasMore);

    private sealed record ServerSecretUpsertResult(
        string Status,
        long Cursor,
        int? CurrentVersion = null,
        string? Reason = null);

    private sealed record ServerSecretsUpsertRequest(string WorkspaceId, ServerSecretEnvelopeDto Envelope);

    // ── POST /secrets ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O corpo do upsert do cliente cai inteiro na forma do servidor. Note o formato REAL: o
    /// backend recebe <c>{workspaceId, envelope}</c> — UM envelope por request, não um lote.
    /// </summary>
    [Fact]
    public void UpsertRequest_DeserializesIntoServerShape()
    {
        var client = new SecretsUpsertRequest(
            "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            new SecretEnvelopeDto(
                Id: "11111111111122223333444444444444",
                WorkspaceId: "6f9619ff-8b86-d011-b42d-00c04fc964ff",
                Ciphertext: Convert.ToBase64String([1, 2, 3]),
                Nonce: Convert.ToBase64String(new byte[12]),
                Tag: Convert.ToBase64String(new byte[16]),
                WrappedCek: Convert.ToBase64String(new byte[32]),
                CekNonce: Convert.ToBase64String(new byte[12]),
                CekTag: Convert.ToBase64String(new byte[16]),
                KeyVersion: "1|password|abc",
                Version: 1)
            {
                Algorithm = VaultAlgorithms.AmkRootedV1,
            });

        string wire = JsonSerializer.Serialize(client, s_web);
        ServerSecretsUpsertRequest? server = JsonSerializer.Deserialize<ServerSecretsUpsertRequest>(wire, s_web);

        Assert.NotNull(server);
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", server!.WorkspaceId);
        Assert.NotNull(server.Envelope);
        Assert.Equal("11111111111122223333444444444444", server.Envelope.Id);
        Assert.Equal(1, server.Envelope.Version);
        Assert.Equal("1|password|abc", server.Envelope.KeyVersion);
        Assert.Equal(VaultAlgorithms.AmkRootedV1, server.Envelope.Algorithm);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), server.Envelope.Ciphertext);
    }

    /// <summary>
    /// O <c>Algorithm</c> do cliente cabe no <c>HasMaxLength(100)</c> da coluna. É o campo que
    /// carimba a RAIZ do envelope (DPAPI vs AMK) — se estourasse, o upsert daria 500 no Postgres.
    /// </summary>
    [Fact]
    public void AlgorithmIds_FitServerColumn()
    {
        Assert.True(VaultAlgorithms.AmkRootedV1.Length <= 100);
        Assert.True(VaultAlgorithms.DpapiRootedV1.Length <= 100);
    }

    /// <summary>O resultado do upsert (200 ok / 409 conflict) tem que caber no tipo do cliente.</summary>
    [Fact]
    public void UpsertResult_FromServerShape_BindsOnClient()
    {
        var server = new ServerSecretUpsertResult("conflict", 7, 3, "version.conflict");

        string wire = JsonSerializer.Serialize(server, s_web);
        SecretUpsertResult? client = JsonSerializer.Deserialize<SecretUpsertResult>(wire, s_web);

        Assert.NotNull(client);
        Assert.Equal("conflict", client!.Status);
        Assert.Equal(7, client.Cursor);
        Assert.Equal(3, client.CurrentVersion);
        Assert.Equal("version.conflict", client.Reason);
    }

    /// <summary>Upsert "ok" vem sem currentVersion/reason — os opcionais não podem estourar.</summary>
    [Fact]
    public void UpsertResult_Ok_WithoutOptionalFields_StillDeserializes()
    {
        var server = new ServerSecretUpsertResult("ok", 42);

        string wire = JsonSerializer.Serialize(server, s_web);
        SecretUpsertResult? client = JsonSerializer.Deserialize<SecretUpsertResult>(wire, s_web);

        Assert.NotNull(client);
        Assert.Equal("ok", client!.Status);
        Assert.Equal(42, client.Cursor);
        Assert.Null(client.Reason);
    }

    // ── GET /secrets ─────────────────────────────────────────────────────────────────────

    /// <summary>A página de segredos do servidor real → tipo do cliente, campo a campo.</summary>
    [Fact]
    public void PullResponse_FromServerShape_BindsOnClient()
    {
        var server = new ServerSecretsPullResponse(
            [
                new ServerSecretEnvelopeDto(
                    Id: "6f9619ff-8b86-d011-b42d-00c04fc964ff",
                    WorkspaceId: "00000000-0000-0000-0000-000000000001",
                    Ciphertext: Convert.ToBase64String([9, 9]),
                    Nonce: Convert.ToBase64String(new byte[12]),
                    Tag: Convert.ToBase64String(new byte[16]),
                    WrappedCek: Convert.ToBase64String(new byte[32]),
                    CekNonce: Convert.ToBase64String(new byte[12]),
                    CekTag: Convert.ToBase64String(new byte[16]),
                    KeyVersion: "1|password|cred-1",
                    Version: 2)
                {
                    Algorithm = VaultAlgorithms.AmkRootedV1,
                },
            ],
            NextCursor: 12,
            HasMore: true);

        string wire = JsonSerializer.Serialize(server, s_web);
        SecretsPullResponse? client = JsonSerializer.Deserialize<SecretsPullResponse>(wire, s_web);

        Assert.NotNull(client);
        Assert.Equal(12, client!.NextCursor);
        Assert.True(client.HasMore);

        SecretEnvelopeDto dto = Assert.Single(client.Envelopes);
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", dto.Id);
        Assert.Equal(2, dto.Version);
        Assert.Equal("1|password|cred-1", dto.KeyVersion);
        Assert.Equal(new byte[] { 9, 9 }, Convert.FromBase64String(dto.Ciphertext));
    }

    /// <summary>Página vazia (device já em dia) desserializa sem estourar.</summary>
    [Fact]
    public void PullResponse_Empty_StillDeserializes()
    {
        var server = new ServerSecretsPullResponse([], NextCursor: 5, HasMore: false);

        string wire = JsonSerializer.Serialize(server, s_web);
        SecretsPullResponse? client = JsonSerializer.Deserialize<SecretsPullResponse>(wire, s_web);

        Assert.NotNull(client);
        Assert.Empty(client!.Envelopes);
        Assert.Equal(5, client.NextCursor);
        Assert.False(client.HasMore);
    }
}
