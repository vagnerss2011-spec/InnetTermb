using System.Text;
using RemoteOps.Cloud.Auth;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// TOTP (RFC 6238) puro: os vetores de teste do Apêndice B da RFC provam a implementação do HMAC-SHA1
/// + truncamento dinâmico. Se um destes cair, nenhum app autenticador (Google Authenticator/Authy)
/// concordaria com o código gerado — o operador ficaria trancado fora da própria conta.
///
/// Segredo dos vetores: os 20 bytes ASCII "12345678901234567890" (o mesmo do Apêndice B, modo SHA1).
/// </summary>
public sealed class TotpServiceTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    // ── Vetores oficiais RFC 6238 (SHA1), reduzidos aos 6 dígitos da direita ──────────────
    // A RFC lista TOTP de 8 dígitos; nós usamos 6. Ex.: T=59s → 94287082 → "287082".
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void ComputeCode_MatchesRfc6238Vectors(long unixSeconds, string expected)
    {
        long step = TotpService.TimeStep(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        string code = TotpService.ComputeCode(RfcSecret, step);
        Assert.Equal(expected, code);
    }

    [Fact]
    public void ComputeCode_AlwaysSixDigits()
    {
        // Um passo cujo OTP começa com zero tem que vir com o zero à esquerda (senão o app diverge).
        for (long step = 0; step < 200; step++)
        {
            string code = TotpService.ComputeCode(RfcSecret, step);
            Assert.Equal(6, code.Length);
            Assert.All(code, c => Assert.True(char.IsAsciiDigit(c)));
        }
    }

    // ── Verificação com janela ±1 (clock skew) ────────────────────────────────────────────

    [Fact]
    public void Verify_AcceptsCurrentStep()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890L);
        string code = TotpService.ComputeCode(RfcSecret, TotpService.TimeStep(now));
        Assert.True(TotpService.Verify(RfcSecret, code, now));
    }

    [Fact]
    public void Verify_AcceptsPreviousStep_WithinWindow()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890L);
        // Código do passo ANTERIOR (30s atrás): a janela ±1 aceita p/ tolerar relógio adiantado.
        string prev = TotpService.ComputeCode(RfcSecret, TotpService.TimeStep(now) - 1);
        Assert.True(TotpService.Verify(RfcSecret, prev, now));
    }

    [Fact]
    public void Verify_AcceptsNextStep_WithinWindow()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890L);
        string next = TotpService.ComputeCode(RfcSecret, TotpService.TimeStep(now) + 1);
        Assert.True(TotpService.Verify(RfcSecret, next, now));
    }

    [Fact]
    public void Verify_RejectsCodeTwoStepsAway()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890L);
        // Dois passos fora (60s) está além da janela ±1 → rejeita.
        string faraway = TotpService.ComputeCode(RfcSecret, TotpService.TimeStep(now) + 2);
        Assert.False(TotpService.Verify(RfcSecret, faraway, now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]     // curto demais
    [InlineData("1234567")]   // longo demais
    [InlineData("12ab56")]    // não-dígito
    [InlineData("000000")]    // formato ok mas valor errado
    public void Verify_RejectsMalformedOrWrongCode(string? code)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890L);
        Assert.False(TotpService.Verify(RfcSecret, code, now));
    }

    [Fact]
    public void Verify_RejectsCodeFromDifferentSecret()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890L);
        byte[] other = TotpService.GenerateSecret();
        string code = TotpService.ComputeCode(other, TotpService.TimeStep(now));
        Assert.False(TotpService.Verify(RfcSecret, code, now));
    }

    // ── Geração de segredo + Base32 + otpauth URI ─────────────────────────────────────────

    [Fact]
    public void GenerateSecret_Is20RandomBytes()
    {
        byte[] a = TotpService.GenerateSecret();
        byte[] b = TotpService.GenerateSecret();
        Assert.Equal(20, a.Length);
        Assert.Equal(20, b.Length);
        Assert.NotEqual(a, b); // CSPRNG: praticamente impossível colidir
    }

    [Fact]
    public void ToBase32_MatchesKnownVector()
    {
        // "12345678901234567890" em Base32 (RFC 4648, sem padding) é conhecido.
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", TotpService.ToBase32(RfcSecret));
    }

    [Fact]
    public void ToBase32_20Bytes_Produces32CharsWithoutPadding()
    {
        string b32 = TotpService.ToBase32(TotpService.GenerateSecret());
        Assert.Equal(32, b32.Length);
        Assert.DoesNotContain('=', b32);
        Assert.All(b32, c => Assert.True("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(c)));
    }

    [Fact]
    public void BuildOtpauthUri_HasIssuerAccountAndParams()
    {
        string uri = TotpService.BuildOtpauthUri("op@innet.tec.br", RfcSecret);

        Assert.StartsWith("otpauth://totp/", uri);
        // Label = "RemoteOps:op@innet.tec.br" (issuer:account), URL-encoded.
        Assert.Contains("RemoteOps%3Aop%40innet.tec.br", uri);
        Assert.Contains("secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", uri);
        Assert.Contains("issuer=RemoteOps", uri);
        Assert.Contains("algorithm=SHA1", uri);
        Assert.Contains("digits=6", uri);
        Assert.Contains("period=30", uri);
    }
}
