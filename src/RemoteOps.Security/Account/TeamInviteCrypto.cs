using System.Security.Cryptography;

namespace RemoteOps.Security.Account;

/// <summary>
/// A metade FORA-DE-BANDA do convite de time. O e-mail leva o link; o CÓDIGO — 160 bits, o mesmo
/// formato da chave de recuperação — vai por WhatsApp, telefone ou pessoalmente. São duas metades de
/// propósito: e-mail vazado sozinho não entrega o cofre do time.
///
/// <para><b>A fronteira E2EE está inteira aqui.</b> Deste arquivo saem duas coisas, e só elas vão
/// para o servidor: o <see cref="HashCode"/> (prova de posse) e o blob de
/// <see cref="WrapWorkspaceKey"/> (a WK cifrada). O código nunca sai da máquina de quem o gera nem
/// da de quem o digita — o servidor guarda hash e blob e não tem como abrir nenhum dos dois.</para>
///
/// <para><b>Por que o mesmo codec da chave de recuperação:</b> formato já conhecido do operador, já
/// testado, e — o que decide — tolerante a como um humano DITA: <see cref="RecoveryKeyCodec.Parse"/>
/// aceita minúsculas, espaços e hífens. Um código de 160 bits do CSPRNG é alta entropia, então não
/// precisa de KDF caro (Argon2): o HKDF basta, pela mesma razão que o escrow de recuperação usa.</para>
/// </summary>
public static class TeamInviteCrypto
{
    private const int KeySize = 32;

    /// <summary>
    /// Domain-separation do HKDF. É o que garante que a chave que ABRE a WK não tem relação
    /// computável com o hash que o servidor guarda: são funções diferentes sobre o mesmo segredo.
    /// </summary>
    private const string InviteKeyInfo = "remoteops:team:invite:v1";

    /// <summary>AAD do embrulho da WK sob a chave do convite. Prende o blob a este uso e a nenhum outro.</summary>
    private const string InviteWrapAad = "wk|invite|v1";

    /// <summary>Sorteia um código novo. Alta entropia (160 bits): não há o que adivinhar.</summary>
    public static string GenerateCode() => RecoveryKeyCodec.Generate();

    /// <summary>
    /// O que o SERVIDOR recebe: SHA-256 dos BYTES canônicos do código, em hex minúsculo (64 chars —
    /// o formato que o backend exige).
    ///
    /// <para>Hash dos bytes, e não da string digitada, porque o código é DITADO: "abcd-efgh" e
    /// "ABCD EFGH" precisam produzir a mesma prova, senão o colega vê "convite inválido" com o
    /// código certo na mão e ninguém descobre que o problema era um espaço.</para>
    ///
    /// <para>Isto NÃO entrega a chave: o servidor teria de inverter o SHA-256 para chegar ao código
    /// e, mesmo com o código, ainda precisaria do HKDF — que é outra função. Um cliente que mandasse
    /// o código cru no lugar do hash mataria o E2EE em silêncio; o backend recusa 400 justamente
    /// por isso.</para>
    /// </summary>
    public static string HashCode(string code)
    {
        byte[] raw = RecoveryKeyCodec.Parse(code);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(raw));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>
    /// Embrulha a WK do time sob <c>K_invite = HKDF(código)</c>. É este blob que sobe no convite —
    /// opaco para o servidor, que não tem o código.
    /// </summary>
    public static byte[] WrapWorkspaceKey(ReadOnlySpan<byte> workspaceKey, string code)
    {
        byte[] inviteKey = DeriveInviteKey(code);
        try
        {
            return AccountKeyService.WrapKey(workspaceKey.ToArray(), inviteKey, InviteWrapAad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inviteKey);
        }
    }

    /// <summary>
    /// Abre o blob do convite com o código. Código errado (ou blob adulterado por um servidor
    /// malicioso) LANÇA <see cref="CryptographicException"/> — o AES-GCM autentica. É o que impede o
    /// desfecho pior: devolver 32 bytes tortos que o convidado importaria como se fossem a chave do
    /// time, bifurcando o cofre em silêncio.
    /// </summary>
    public static byte[] UnwrapWorkspaceKey(byte[] wrapped, string code)
    {
        ArgumentNullException.ThrowIfNull(wrapped);

        byte[] inviteKey = DeriveInviteKey(code);
        try
        {
            return AccountKeyService.UnwrapKey(wrapped, inviteKey, InviteWrapAad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inviteKey);
        }
    }

    private static byte[] DeriveInviteKey(string code)
    {
        byte[] raw = RecoveryKeyCodec.Parse(code);
        try
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                raw,
                KeySize,
                salt: null,
                info: System.Text.Encoding.UTF8.GetBytes(InviteKeyInfo));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }
}
