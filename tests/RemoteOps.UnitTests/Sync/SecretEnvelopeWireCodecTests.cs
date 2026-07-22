using RemoteOps.Security.Vault;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// O tradutor entre o <see cref="SecretEnvelope"/> do cofre e o DTO do fio. Parece burocracia, mas é
/// aqui que mora o risco: o AAD do GCM é montado de <c>envelopeId|workspaceId|version|type</c>, então
/// qualquer campo que o round-trip mude — nem que seja o FORMATO de um GUID — faz o cofre não abrir
/// no outro device, com um erro de cripto que não explica nada.
/// </summary>
public sealed class SecretEnvelopeWireCodecTests
{
    private const string VaultWorkspace = "ws-local";
    private static readonly string ServerWorkspace = Guid.NewGuid().ToString();

    private static SecretEnvelope Envelope(string? id = null, string type = "password", int version = 1) => new()
    {
        EnvelopeId = id ?? Guid.NewGuid().ToString("n"),
        WorkspaceId = VaultWorkspace,
        CredentialId = Guid.NewGuid().ToString("n"),
        Type = type,
        Version = version,
        Algorithm = VaultAlgorithms.AmkRootedV1,
        WrappedCek = new byte[32],
        CekNonce = new byte[12],
        CekTag = new byte[16],
        Ciphertext = [1, 2, 3],
        Nonce = new byte[12],
        Tag = new byte[16],
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// O que o <c>CredentialVault.TombstoneAsync</c> grava ao revogar: material ZERADO, marca de
    /// revogação e versão incrementada (a revogação é uma versão nova do registro).
    /// </summary>
    private static SecretEnvelope Tombstone(string? id = null, int version = 2) => Envelope(id, version: version) with
    {
        RevokedAt = DateTimeOffset.UtcNow,
        WrappedCek = [],
        CekNonce = [],
        CekTag = [],
        Ciphertext = [],
        Nonce = [],
        Tag = [],
    };

    /// <summary>
    /// <b>A armadilha.</b> O cliente gera o EnvelopeId como GUID "N" (sem hífens); o servidor guarda
    /// num <c>Guid</c> e devolve com <c>ToString()</c>, ou seja "D" (com hífens). Sem normalizar, o
    /// device B montaria o AAD com um id diferente do que o device A selou → tag GCM inválida.
    /// </summary>
    [Fact]
    public void EnvelopeId_VoltandoNoFormatoD_ENormalizadoParaN()
    {
        var id = Guid.NewGuid();
        SecretEnvelope original = Envelope(id.ToString("n"));

        SecretEnvelopeDto dto = SecretEnvelopeWireCodec.ToWire(original, ServerWorkspace, amkKeyVersion: 1);
        // O servidor devolve no formato "D" — é o que o SecretsService.ToDto faz.
        SecretEnvelope back = SecretEnvelopeWireCodec.FromWire(dto with { Id = id.ToString() }, VaultWorkspace);

        Assert.Equal(id.ToString("n"), back.EnvelopeId);
        Assert.Equal(original.EnvelopeId, back.EnvelopeId);
    }

    /// <summary>
    /// O WorkspaceId do envelope é a identidade do COFRE ("ws-local") e entra na derivação da WDK e
    /// no AAD do embrulho. No fio vai o GUID do servidor — e na volta tem que virar o do cofre de
    /// novo, senão o envelope baixado não abre.
    /// </summary>
    [Fact]
    public void WorkspaceId_DoFio_EhOServidor_MasNoCofreVoltaOLocal()
    {
        SecretEnvelope original = Envelope();

        SecretEnvelopeDto dto = SecretEnvelopeWireCodec.ToWire(original, ServerWorkspace, amkKeyVersion: 1);
        Assert.Equal(ServerWorkspace, dto.WorkspaceId);

        SecretEnvelope back = SecretEnvelopeWireCodec.FromWire(dto, VaultWorkspace);
        Assert.Equal(VaultWorkspace, back.WorkspaceId);
    }

    /// <summary>
    /// Round-trip completo dos campos que o AAD usa + o material cripto, byte a byte.
    /// </summary>
    [Fact]
    public void RoundTrip_PreservaOsCamposQueOAadUsa_EOMaterialCripto()
    {
        SecretEnvelope original = Envelope(type: "privateKeyPassphrase", version: 3);

        SecretEnvelopeDto dto = SecretEnvelopeWireCodec.ToWire(original, ServerWorkspace, amkKeyVersion: 1);
        SecretEnvelope back = SecretEnvelopeWireCodec.FromWire(dto, VaultWorkspace);

        Assert.Equal(original.EnvelopeId, back.EnvelopeId);
        Assert.Equal(original.WorkspaceId, back.WorkspaceId);
        Assert.Equal(original.CredentialId, back.CredentialId);
        Assert.Equal(original.Type, back.Type);
        Assert.Equal(original.Version, back.Version);
        Assert.Equal(original.Algorithm, back.Algorithm);
        Assert.Equal(original.WrappedCek, back.WrappedCek);
        Assert.Equal(original.CekNonce, back.CekNonce);
        Assert.Equal(original.CekTag, back.CekTag);
        Assert.Equal(original.Ciphertext, back.Ciphertext);
        Assert.Equal(original.Nonce, back.Nonce);
        Assert.Equal(original.Tag, back.Tag);
    }

    /// <summary>
    /// A revogação tem que ATRAVESSAR o fio: sem ela o outro device grava o envelope como vivo e a
    /// senha velha continua decifrável lá. O material vem vazio de propósito (o cofre zera ao
    /// revogar) e volta vazio — é isso que apaga o segredo no device que recebe.
    /// </summary>
    [Fact]
    public void Tombstone_LevaARevogacaoNoFio_EVolta()
    {
        SecretEnvelope original = Tombstone();

        SecretEnvelopeDto dto = SecretEnvelopeWireCodec.ToWire(original, ServerWorkspace, amkKeyVersion: 1);
        Assert.Equal(original.RevokedAt, dto.RevokedAt);
        Assert.Equal(string.Empty, dto.Ciphertext);

        SecretEnvelope back = SecretEnvelopeWireCodec.FromWire(dto, VaultWorkspace);
        Assert.Equal(original.RevokedAt, back.RevokedAt);
        Assert.Empty(back.Ciphertext);
        Assert.Empty(back.WrappedCek);
        Assert.Equal(original.Version, back.Version);
    }

    /// <summary>
    /// Compatibilidade: um servidor/cliente ANTIGO não manda <c>revokedAt</c>. O envelope volta VIVO
    /// — que é exatamente o comportamento de hoje. O campo novo ADICIONA, não troca.
    /// </summary>
    [Fact]
    public void SemRevokedAtNoFio_EnvelopeVoltaVivo()
    {
        SecretEnvelopeDto dto = SecretEnvelopeWireCodec.ToWire(Envelope(), ServerWorkspace, 1);

        Assert.Null(dto.RevokedAt);
        Assert.Null(SecretEnvelopeWireCodec.FromWire(dto, VaultWorkspace).RevokedAt);
    }

    /// <summary>
    /// <c>CredentialId</c> e <c>Type</c> não existem no DTO do backend — viajam no
    /// <c>keyVersion</c>, que o servidor trata como string opaca (ver a divergência documentada no
    /// <see cref="SecretEnvelopeWireCodec"/>). O cabeçalho tem que ser exatamente o combinado.
    /// </summary>
    [Fact]
    public void KeyVersion_CarregaOCabecalhoEstrutural()
    {
        SecretEnvelope original = Envelope(type: "password");

        SecretEnvelopeDto dto = SecretEnvelopeWireCodec.ToWire(original, ServerWorkspace, amkKeyVersion: 1);

        Assert.Equal($"1|password|{original.CredentialId}", dto.KeyVersion);
        Assert.True(dto.KeyVersion.Length <= 100, "keyVersion tem HasMaxLength(100) no servidor");
    }

    /// <summary>Um keyVersion que não é do nosso esquema não pode virar envelope corrompido em silêncio.</summary>
    [Theory]
    [InlineData("1")]
    [InlineData("1|password")]
    [InlineData("")]
    [InlineData("lixo")]
    public void KeyVersion_Invalido_Rejeita(string keyVersion)
    {
        SecretEnvelopeDto dto = SecretEnvelopeWireCodec.ToWire(Envelope(), ServerWorkspace, 1) with
        {
            KeyVersion = keyVersion,
        };

        Assert.Throws<CloudSyncException>(() => SecretEnvelopeWireCodec.FromWire(dto, VaultWorkspace));
    }

    /// <summary>
    /// O separador do cabeçalho não pode aparecer nos campos, senão o parse do outro device
    /// remontaria o Type errado — e Type errado é AAD errado é cofre que não abre.
    /// </summary>
    [Fact]
    public void Campo_ComOSeparador_EhRejeitadoNaSaida()
    {
        SecretEnvelope envenenado = Envelope() with { Type = "pass|word" };

        Assert.Throws<CloudSyncException>(
            () => SecretEnvelopeWireCodec.ToWire(envenenado, ServerWorkspace, 1));
    }

    /// <summary>Id que não é GUID não sobe: o backend exige Guid.TryParse e devolveria 400/500.</summary>
    [Fact]
    public void EnvelopeId_QueNaoEhGuid_EhRejeitado()
    {
        SecretEnvelope invalido = Envelope() with { EnvelopeId = "nao-e-guid" };

        Assert.Throws<CloudSyncException>(() => SecretEnvelopeWireCodec.ToWire(invalido, ServerWorkspace, 1));
    }

    // ── IsSyncable ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Syncable_SoOQueEstaSobAAmk() =>
        Assert.True(SecretEnvelopeWireCodec.IsSyncable(Envelope()));

    [Fact]
    public void NaoSyncable_EnvelopeDpapi() =>
        Assert.False(SecretEnvelopeWireCodec.IsSyncable(Envelope() with { Algorithm = VaultAlgorithms.DpapiRootedV1 }));

    /// <summary>
    /// <b>O tombstone TEM que subir.</b> Enquanto ele ficava em casa, trocar uma senha deixava o
    /// envelope antigo vivo e decifrável no disco do outro device PARA SEMPRE — a revogação morria
    /// no PC onde foi feita. É falha de segurança, não detalhe de transporte.
    /// </summary>
    [Fact]
    public void Syncable_Tombstone_ParaARevogacaoPropagar() =>
        Assert.True(SecretEnvelopeWireCodec.IsSyncable(Tombstone()));

    /// <summary>
    /// Material vazio só é legítimo em tombstone. Um envelope VIVO sem material é corrupção — subir
    /// isso publicaria um envelope que ninguém abre e que o servidor recusa.
    /// </summary>
    [Fact]
    public void NaoSyncable_EnvelopeVivoSemMaterial() =>
        Assert.False(SecretEnvelopeWireCodec.IsSyncable(Envelope() with { Ciphertext = [], WrappedCek = [] }));

    /// <summary>Tombstone de envelope DPAPI continua em casa: o vivo dele nunca subiu.</summary>
    [Fact]
    public void NaoSyncable_TombstoneDpapi() =>
        Assert.False(SecretEnvelopeWireCodec.IsSyncable(
            Tombstone() with { Algorithm = VaultAlgorithms.DpapiRootedV1 }));

    // ── Raiz do time (WkRootedV1) ────────────────────────────────────────────────────────
    //
    // Entra ADICIONANDO: a raiz do time passa a subir SEM que a da AMK pare de subir. É a classe de
    // falha mais traiçoeira desta base — um `IsSyncable` que TROCA a raiz aceita faria as senhas do
    // cofre pessoal pararem de sincronizar em silêncio, e ninguém veria erro nenhum.

    /// <summary>O envelope do time sobe: sem isso o cofre compartilhado nunca sai do primeiro PC.</summary>
    [Fact]
    public void Syncable_EnvelopeDoTime() =>
        Assert.True(SecretEnvelopeWireCodec.IsSyncable(Envelope() with { Algorithm = VaultAlgorithms.WkRootedV1 }));

    /// <summary>E a revogação dele também — senão a senha velha fica viva no PC do colega.</summary>
    [Fact]
    public void Syncable_TombstoneDoTime() =>
        Assert.True(SecretEnvelopeWireCodec.IsSyncable(Tombstone() with { Algorithm = VaultAlgorithms.WkRootedV1 }));

    /// <summary>
    /// O carimbo do time atravessa o fio intacto. Errar aqui não dá erro de transporte: dá um
    /// envelope que monta o AAD errado e simplesmente NÃO ABRE no outro device.
    /// </summary>
    [Fact]
    public void FromWire_PreservaOCarimboDoTime()
    {
        SecretEnvelopeDto dto = SecretEnvelopeWireCodec.ToWire(
            Envelope() with { Algorithm = VaultAlgorithms.WkRootedV1 }, ServerWorkspace, 1);

        Assert.Equal(VaultAlgorithms.WkRootedV1, dto.Algorithm);
        Assert.Equal(
            VaultAlgorithms.WkRootedV1,
            SecretEnvelopeWireCodec.FromWire(dto, VaultWorkspace).Algorithm);
    }

    /// <summary>
    /// Servidor antigo (ou registro gravado antes do campo existir) não ecoa <c>algorithm</c>. Quem
    /// sabe a raiz do cofre é o chamador — no cofre do time, o palpite "AMK" produziria um envelope
    /// que não abre.
    /// </summary>
    [Fact]
    public void FromWire_SemAlgorithmNoFio_UsaARaizQueOChamadorInformou()
    {
        SecretEnvelopeDto dto = SecretEnvelopeWireCodec.ToWire(Envelope(), ServerWorkspace, 1) with
        {
            Algorithm = null,
        };

        Assert.Equal(
            VaultAlgorithms.WkRootedV1,
            SecretEnvelopeWireCodec.FromWire(dto, VaultWorkspace, VaultAlgorithms.WkRootedV1).Algorithm);
    }

    /// <summary>
    /// Regressão do que NÃO mudou: sem raiz informada, o default continua sendo a AMK — exatamente
    /// o comportamento de hoje para o cofre pessoal.
    /// </summary>
    [Fact]
    public void FromWire_SemAlgorithmNoFio_ODefaultContinuaSendoAAmk()
    {
        SecretEnvelopeDto dto = SecretEnvelopeWireCodec.ToWire(Envelope(), ServerWorkspace, 1) with
        {
            Algorithm = null,
        };

        Assert.Equal(VaultAlgorithms.AmkRootedV1, SecretEnvelopeWireCodec.FromWire(dto, VaultWorkspace).Algorithm);
    }
}
