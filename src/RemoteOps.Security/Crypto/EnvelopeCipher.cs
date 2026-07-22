using System.Security.Cryptography;
using System.Text;

using RemoteOps.Security.Vault;

namespace RemoteOps.Security.Crypto;

/// <summary>
/// Núcleo de envelope encryption. Cada segredo recebe uma CEK aleatória
/// (AES-256-GCM); a CEK é embrulhada pela Workspace Data Key (também GCM).
/// O Associated Data (AAD) liga cada ciphertext à identidade do envelope,
/// impedindo troca/replay entre envelopes ou workspaces.
///
/// O identificador do esquema (<c>SecretEnvelope.Algorithm</c>) NÃO mora aqui: quem carimba é a
/// raiz de chave (<see cref="IWorkspaceKeyRing.AlgorithmId"/>), porque o cifrador é o mesmo para
/// as duas raízes (DPAPI e AMK) — ver <see cref="Vault.VaultAlgorithms"/>.
/// </summary>
internal static class EnvelopeCipher
{
    private const int KeySize = 32;   // AES-256
    private const int NonceSize = 12; // tamanho padrão recomendado para GCM
    private const int TagSize = 16;

    internal static SealedSecret Seal(
        ReadOnlySpan<byte> workspaceKey,
        ReadOnlySpan<byte> plaintext,
        byte[] aad,
        byte[] wrapAad)
    {
        Span<byte> cek = stackalloc byte[KeySize];
        RandomNumberGenerator.Fill(cek);
        try
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] tag = new byte[TagSize];
            byte[] ciphertext = new byte[plaintext.Length];
            using (var gcm = new AesGcm(cek, TagSize))
            {
                gcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            }

            byte[] cekNonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] cekTag = new byte[TagSize];
            byte[] wrappedCek = new byte[KeySize];
            using (var gcm = new AesGcm(workspaceKey, TagSize))
            {
                gcm.Encrypt(cekNonce, cek, wrappedCek, cekTag, wrapAad);
            }

            return new SealedSecret(wrappedCek, cekNonce, cekTag, ciphertext, nonce, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cek);
        }
    }

    internal static byte[] Open(
        ReadOnlySpan<byte> workspaceKey,
        SecretEnvelope envelope,
        byte[] aad,
        byte[] wrapAad)
    {
        Span<byte> cek = stackalloc byte[KeySize];
        try
        {
            // Desembrulha a CEK. Chave de workspace errada (ex.: outro usuário/máquina)
            // ou material adulterado faz o tag GCM falhar -> CryptographicException.
            using (var gcm = new AesGcm(workspaceKey, TagSize))
            {
                gcm.Decrypt(envelope.CekNonce, envelope.WrappedCek, envelope.CekTag, cek, wrapAad);
            }

            byte[] plaintext = new byte[envelope.Ciphertext.Length];
            try
            {
                using var gcm = new AesGcm(cek, TagSize);
                gcm.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, aad);
                return plaintext;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cek);
        }
    }

    // ---- Construção do AAD (fonte ÚNICA da verdade) ----
    //
    // Vive aqui, e não em quem chama, porque o LocalVaultMigrator re-sela envelopes existentes e
    // precisa reproduzir o AAD byte-a-byte — duplicar essas strings em dois lugares seria uma
    // bomba-relógio: divergiu, o tag GCM falha e o cofre não abre mais.

    /// <summary>
    /// AAD do payload: liga o ciphertext à identidade do envelope. O <paramref name="type"/> entra
    /// para autenticar o campo estrutural — mesmo sem RBAC por tipo hoje, impede que um atacante
    /// com escrita no store altere o Type sem quebrar a verificação GCM.
    ///
    /// <para><b>Formato CONGELADO.</b> É o AAD do <c>DpapiRootedV1</c> e do <c>AmkRootedV1</c>:
    /// alterar um único byte aqui torna ilegível TUDO o que já está selado em produção. Campo novo
    /// no AAD entra por esquema NOVO — ver a sobrecarga com <c>credentialId</c>.</para>
    /// </summary>
    internal static byte[] BuildAad(string envelopeId, string workspaceId, int version, string type) =>
        Encoding.UTF8.GetBytes($"env|{envelopeId}|{workspaceId}|v{version}|{type}");

    /// <summary>
    /// AAD por ESQUEMA. No <see cref="VaultAlgorithms.WkRootedV1"/> (a raiz do time) o AAD prende
    /// também o <paramref name="credentialId"/> e o próprio <paramref name="algorithm"/>; nas raízes
    /// antigas cai, byte a byte, no formato congelado acima.
    ///
    /// <para><b>Por que os dois campos:</b> hoje o <c>Algorithm</c> e o cabeçalho
    /// <c>type|credentialId</c> viajam FORA de qualquer AAD (o cabeçalho vai no <c>keyVersion</c>,
    /// que o servidor guarda como string opaca). Um servidor malicioso poderia então RE-ASSOCIAR um
    /// envelope a outra credencial — e num cofre de TIME o efeito é concreto: o colega abriria a
    /// senha do equipamento X achando que é a do equipamento Y, e a usaria. O <c>algorithm</c> entra
    /// junto para fechar o downgrade: sem ele, rebaixar o carimbo para <c>AmkRootedV1</c> pediria o
    /// AAD antigo, que não tem credentialId.</para>
    ///
    /// <para>Corrigir o esquema ANTIGO era impossível sem reescrever todos os envelopes já selados —
    /// por isso a correção entra onde é de graça: nos envelopes que nascem agora.</para>
    /// </summary>
    internal static byte[] BuildAad(
        string envelopeId,
        string workspaceId,
        int version,
        string type,
        string credentialId,
        string algorithm) =>
        string.Equals(algorithm, VaultAlgorithms.WkRootedV1, StringComparison.Ordinal)
            ? Encoding.UTF8.GetBytes($"env|{envelopeId}|{workspaceId}|v{version}|{type}|{credentialId}|{algorithm}")
            : BuildAad(envelopeId, workspaceId, version, type);

    /// <summary>
    /// AAD de um envelope existente. O esquema sai do próprio <c>Algorithm</c> gravado — e é isso
    /// que faz a adulteração do carimbo se denunciar: com o Algorithm trocado, o AAD montado na
    /// leitura não é o que selou, e o tag GCM não fecha.
    /// </summary>
    internal static byte[] BuildAad(SecretEnvelope envelope) =>
        BuildAad(
            envelope.EnvelopeId,
            envelope.WorkspaceId,
            envelope.Version,
            envelope.Type,
            envelope.CredentialId,
            envelope.Algorithm);

    /// <summary>AAD do embrulho da CEK: liga a CEK embrulhada ao workspace.</summary>
    internal static byte[] BuildWrapAad(string workspaceId) =>
        Encoding.UTF8.GetBytes($"wdk|{workspaceId}");

    internal readonly record struct SealedSecret(
        byte[] WrappedCek,
        byte[] CekNonce,
        byte[] CekTag,
        byte[] Ciphertext,
        byte[] Nonce,
        byte[] Tag);
}
