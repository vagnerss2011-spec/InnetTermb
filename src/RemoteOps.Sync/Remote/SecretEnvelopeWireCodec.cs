using RemoteOps.Security.Vault;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Traduz o <see cref="SecretEnvelope"/> do cofre para o <see cref="SecretEnvelopeDto"/> do fio e de
/// volta. É o único lugar que conhece as DIVERGÊNCIAS entre os dois contratos — e elas não são
/// cosméticas: o AAD do AES-GCM é montado de <c>envelopeId|workspaceId|version|type</c>
/// (<c>EnvelopeCipher.BuildAad</c>), então qualquer campo que o round-trip altere faz o envelope não
/// abrir no outro device, com um erro de cripto que não explica nada.
///
/// <para><b>1. Formato do GUID (armadilha silenciosa).</b> O cofre gera o EnvelopeId como
/// <c>Guid.NewGuid().ToString("n")</c> (32 hex, SEM hífens). O servidor guarda num <c>Guid</c> e
/// devolve com <c>e.Id.ToString()</c> — formato "D", COM hífens. Sem normalizar na volta, o device B
/// montaria o AAD com um id diferente do que o A selou. Aqui o id é sempre canonizado para "n".</para>
///
/// <para><b>2. WorkspaceId é DOIS conceitos.</b> No envelope é a identidade do COFRE
/// (<c>AppRuntime.CredentialsWorkspace</c> = "ws-local"), que alimenta a derivação da WDK e o AAD do
/// embrulho; no fio é o GUID do workspace no SERVIDOR. O codec mapeia um no outro — e na volta usa o
/// workspace LOCAL informado pelo chamador, nunca o que o servidor ecoou.</para>
///
/// <para><b>3. <c>CredentialId</c> e <c>Type</c> não existem no DTO do backend</b> — e os dois são
/// necessários: o <c>Type</c> entra no AAD (sem ele o envelope não abre) e o <c>CredentialId</c> liga
/// o envelope à credencial. Como o backend está deployado e não pode mudar nesta frente, eles viajam
/// no <c>keyVersion</c>, o único campo string que o servidor exige mas nunca interpreta (e para o
/// qual o cliente não tem significado próprio — não há versão de chave POR ENVELOPE; a versão do
/// esquema da AMK é da CONTA). Formato: <c>"&lt;amkKeyVersion&gt;|&lt;type&gt;|&lt;credentialId&gt;"</c>,
/// mantendo a versão como primeiro token para o campo não mentir sobre o próprio nome (spec §4.2).
/// Não há vazamento novo: <c>type</c> e o id da credencial já viajam em claro no changelog
/// (<c>credential_ref</c>). <b>É uma gambiarra assumida</b> — a correção certa é o backend ganhar
/// colunas <c>credentialId</c>/<c>type</c>; enquanto não ganha, este codec é a fronteira.</para>
///
/// <para><b>4. <c>CreatedAt</c> não trafega</b> → o device que recebe carimba a hora da chegada. Não
/// entra no AAD, então não afeta a decifração; só a data exibida diverge entre devices.</para>
///
/// <para><b>5. <c>RevokedAt</c> não trafega</b> → tombstone NÃO sobe (ver <see cref="IsSyncable"/>).
/// O backend recusa base64 vazio, e o tombstone zera o material. Consequência real: revogação não
/// propaga nesta fase.</para>
/// </summary>
internal static class SecretEnvelopeWireCodec
{
    private const char HeaderSeparator = '|';
    private const int HeaderParts = 3;

    /// <summary>Limite da coluna no backend (<c>HasMaxLength(100)</c> em keyVersion e algorithm).</summary>
    private const int ServerStringLimit = 100;

    /// <summary>
    /// Só sobe o que faz sentido no servidor: envelope ativo, com material, e ENRAIZADO NA AMK.
    ///
    /// <para>O filtro da raiz não é zelo: um envelope ainda sob a raiz DPAPI tem WDK aleatória POR
    /// MÁQUINA — nenhum outro device abriria. Subir publicaria lixo indecifrável e queimaria cursor
    /// no servidor. Tombstone também não sobe: o backend exige base64 não-vazio e o tombstone zera
    /// tudo, então cada ciclo viraria um erro de servidor, pra sempre.</para>
    /// </summary>
    internal static bool IsSyncable(SecretEnvelope envelope) =>
        envelope.RevokedAt is null
        && string.Equals(envelope.Algorithm, VaultAlgorithms.AmkRootedV1, StringComparison.Ordinal)
        && envelope.Ciphertext.Length > 0
        && envelope.WrappedCek.Length > 0
        && envelope.Nonce.Length > 0
        && envelope.Tag.Length > 0
        && envelope.CekNonce.Length > 0
        && envelope.CekTag.Length > 0;

    internal static SecretEnvelopeDto ToWire(
        SecretEnvelope envelope, string serverWorkspaceId, int amkKeyVersion)
    {
        // O backend faz Guid.TryParse no id: um id fora do formato viraria 400/500 a cada ciclo.
        // Falhar aqui, alto e claro, é melhor que um envelope preso pra sempre no outbox.
        if (!Guid.TryParse(envelope.EnvelopeId, out _))
        {
            throw new CloudSyncException(
                $"EnvelopeId '{envelope.EnvelopeId}' não é um GUID — o backend de segredos exige GUID.");
        }

        string keyVersion = BuildHeader(amkKeyVersion, envelope.Type, envelope.CredentialId);

        return new SecretEnvelopeDto(
            Id: envelope.EnvelopeId,
            WorkspaceId: serverWorkspaceId,
            Ciphertext: Convert.ToBase64String(envelope.Ciphertext),
            Nonce: Convert.ToBase64String(envelope.Nonce),
            Tag: Convert.ToBase64String(envelope.Tag),
            WrappedCek: Convert.ToBase64String(envelope.WrappedCek),
            CekNonce: Convert.ToBase64String(envelope.CekNonce),
            CekTag: Convert.ToBase64String(envelope.CekTag),
            KeyVersion: keyVersion,
            Version: envelope.Version)
        {
            Algorithm = envelope.Algorithm,
        };
    }

    /// <param name="vaultWorkspaceId">
    /// Identidade do cofre local sob a qual o envelope será gravado. Vem do chamador de propósito: o
    /// workspace que o servidor ecoa é o GUID dele, não o do cofre.
    /// </param>
    internal static SecretEnvelope FromWire(SecretEnvelopeDto dto, string vaultWorkspaceId)
    {
        (string type, string credentialId) = ParseHeader(dto.KeyVersion);

        return new SecretEnvelope
        {
            // Canoniza para "n": é o formato que o cofre gera e o que o AAD do device A usou.
            EnvelopeId = NormalizeId(dto.Id),
            WorkspaceId = vaultWorkspaceId,
            CredentialId = credentialId,
            Type = type,
            Version = dto.Version,
            Algorithm = dto.Algorithm ?? VaultAlgorithms.AmkRootedV1,
            WrappedCek = Decode(dto.WrappedCek, nameof(dto.WrappedCek)),
            CekNonce = Decode(dto.CekNonce, nameof(dto.CekNonce)),
            CekTag = Decode(dto.CekTag, nameof(dto.CekTag)),
            Ciphertext = Decode(dto.Ciphertext, nameof(dto.Ciphertext)),
            Nonce = Decode(dto.Nonce, nameof(dto.Nonce)),
            Tag = Decode(dto.Tag, nameof(dto.Tag)),
            // O fio não carrega CreatedAt: quem recebe carimba a chegada. Não entra no AAD.
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static string NormalizeId(string id) =>
        Guid.TryParse(id, out Guid parsed)
            ? parsed.ToString("n")
            : throw new CloudSyncException($"Id de envelope inválido no fio: '{id}' não é um GUID.");

    private static string BuildHeader(int amkKeyVersion, string type, string credentialId)
    {
        // Um separador dentro de um campo faria o outro device remontar o Type errado — e Type errado
        // é AAD errado é cofre que não abre. Recusar aqui é a única forma de o erro aparecer no
        // device que ERROU, e não três meses depois no device que não abre.
        if (type.Contains(HeaderSeparator) || credentialId.Contains(HeaderSeparator))
        {
            throw new CloudSyncException(
                $"Type/CredentialId não podem conter '{HeaderSeparator}' (separador do cabeçalho do envelope).");
        }

        string header = string.Join(HeaderSeparator, amkKeyVersion, type, credentialId);
        if (header.Length > ServerStringLimit)
        {
            throw new CloudSyncException(
                $"Cabeçalho do envelope tem {header.Length} chars e o servidor aceita {ServerStringLimit}.");
        }

        return header;
    }

    private static (string Type, string CredentialId) ParseHeader(string keyVersion)
    {
        string[] parts = keyVersion.Split(HeaderSeparator);
        if (parts.Length != HeaderParts
            || !int.TryParse(parts[0], out _)
            || string.IsNullOrEmpty(parts[1])
            || string.IsNullOrEmpty(parts[2]))
        {
            // Um envelope de um cliente que não fala este esquema não pode ser gravado "mais ou
            // menos": sem Type certo ele nunca abriria, e um envelope que não abre no cofre é pior
            // que um envelope ausente (parece que está lá).
            throw new CloudSyncException(
                $"keyVersion '{keyVersion}' não está no formato '<amkKeyVersion>|<type>|<credentialId>'.");
        }

        return (parts[1], parts[2]);
    }

    private static byte[] Decode(string base64, string field)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new CloudSyncException($"Campo '{field}' não é base64 válido.");
        }
    }
}
