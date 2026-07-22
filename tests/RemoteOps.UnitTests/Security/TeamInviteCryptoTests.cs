using System;
using System.Security.Cryptography;
using System.Text;

using RemoteOps.Security.Account;

using Xunit;

namespace RemoteOps.UnitTests.Security;

/// <summary>
/// A metade FORA-DE-BANDA do convite: o código de 160 bits que viaja por WhatsApp/telefone enquanto
/// o link viaja por e-mail. É ele que faz uma caixa de entrada invadida NÃO virar acesso ao cofre do
/// time — por isso o que o servidor recebe é blob + hash, nunca o código.
/// </summary>
public sealed class TeamInviteCryptoTests
{
    private static byte[] Wk() => RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// O código é o MESMO formato da chave de recuperação (base32 em grupos de 4): formato já
    /// conhecido do operador, já testado, e que o <see cref="RecoveryKeyCodec"/> sabe reler tolerando
    /// minúscula, espaço e hífen — porque ele vai ser DITADO ao telefone.
    /// </summary>
    [Fact]
    public void Codigo_TemOFormatoDaChaveDeRecuperacao_E160Bits()
    {
        string code = TeamInviteCrypto.GenerateCode();

        Assert.Equal(20, RecoveryKeyCodec.Parse(code).Length); // 160 bits
        Assert.Contains('-', code);
        Assert.NotEqual(code, TeamInviteCrypto.GenerateCode()); // sorteado, não fixo
    }

    /// <summary>Round-trip: quem tem o código abre a WK, byte a byte.</summary>
    [Fact]
    public void CodigoCerto_DesembrulhaAWk()
    {
        byte[] wk = Wk();
        string code = TeamInviteCrypto.GenerateCode();

        byte[] blob = TeamInviteCrypto.WrapWorkspaceKey(wk, code);

        Assert.Equal(wk, TeamInviteCrypto.UnwrapWorkspaceKey(blob, code));
    }

    /// <summary>
    /// Código errado NÃO abre — e não abre "meio": o AES-GCM autentica, então o erro é uma exceção,
    /// não bytes tortos. Devolver lixo silenciosamente faria o convidado importar 32 bytes aleatórios
    /// como se fossem a chave do time, e o cofre bifurcaria sem ninguém perceber.
    /// </summary>
    [Fact]
    public void CodigoErrado_NaoAbre_ELancaEmVezDeDevolverLixo()
    {
        byte[] blob = TeamInviteCrypto.WrapWorkspaceKey(Wk(), TeamInviteCrypto.GenerateCode());

        Assert.ThrowsAny<CryptographicException>(
            () => TeamInviteCrypto.UnwrapWorkspaceKey(blob, TeamInviteCrypto.GenerateCode()));
    }

    /// <summary>Blob adulterado (servidor malicioso trocando um byte) também não abre.</summary>
    [Fact]
    public void BlobAdulterado_NaoAbre()
    {
        string code = TeamInviteCrypto.GenerateCode();
        byte[] blob = TeamInviteCrypto.WrapWorkspaceKey(Wk(), code);
        blob[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => TeamInviteCrypto.UnwrapWorkspaceKey(blob, code));
    }

    /// <summary>
    /// O código é DITADO: quem digita "abcd efgh" minúsculo, sem hífen, tem que entrar do mesmo
    /// jeito. Se a normalização falhasse, o convidado veria "convite inválido" com o código certo na
    /// mão — e ninguém descobriria que o problema era um espaço.
    /// </summary>
    [Fact]
    public void CodigoDigitadoTorto_AindaAbre_EDaOMesmoHash()
    {
        byte[] wk = Wk();
        string code = TeamInviteCrypto.GenerateCode();
        string torto = "  " + code.Replace("-", " ").ToLowerInvariant() + "  ";

        byte[] blob = TeamInviteCrypto.WrapWorkspaceKey(wk, code);

        Assert.Equal(wk, TeamInviteCrypto.UnwrapWorkspaceKey(blob, torto));
        Assert.Equal(TeamInviteCrypto.HashCode(code), TeamInviteCrypto.HashCode(torto));
    }

    /// <summary>
    /// <b>A fronteira E2EE desta fatia.</b> O que sobe é o hash: 64 hex (o formato que o servidor
    /// exige) e que não contém o código nem em texto nem em base32. Um cliente que mandasse o código
    /// cru entregaria a chave do time ao servidor — e o E2EE morreria em silêncio.
    /// </summary>
    [Fact]
    public void Hash_Tem64Hex_ENaoCarregaOCodigo()
    {
        string code = TeamInviteCrypto.GenerateCode();

        string hash = TeamInviteCrypto.HashCode(code);

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f', $"'{c}' não é hex"));
        Assert.DoesNotContain(code.Replace("-", string.Empty), hash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Hashes diferentes para códigos diferentes — senão qualquer código abriria o convite.</summary>
    [Fact]
    public void Hash_EhDoCodigo_NaoConstante()
    {
        Assert.NotEqual(
            TeamInviteCrypto.HashCode(TeamInviteCrypto.GenerateCode()),
            TeamInviteCrypto.HashCode(TeamInviteCrypto.GenerateCode()));
    }

    /// <summary>
    /// O blob que sobe é OPACO: nem a WK nem os bytes do código aparecem dentro dele. Este teste é a
    /// rede de segurança contra um "wrap" que um dia vire concatenação.
    /// </summary>
    [Fact]
    public void BlobQueSobe_NaoContemAWkNemOCodigo()
    {
        byte[] wk = Wk();
        string code = TeamInviteCrypto.GenerateCode();

        byte[] blob = TeamInviteCrypto.WrapWorkspaceKey(wk, code);

        Assert.False(Contains(blob, wk), "a WK aparece em claro dentro do blob do convite");
        Assert.False(
            Contains(blob, RecoveryKeyCodec.Parse(code)),
            "os bytes do código aparecem dentro do blob do convite");
        Assert.False(
            Contains(blob, Encoding.UTF8.GetBytes(code)),
            "o código em texto aparece dentro do blob do convite");
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
