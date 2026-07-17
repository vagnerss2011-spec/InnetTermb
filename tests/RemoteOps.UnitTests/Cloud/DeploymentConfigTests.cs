using Microsoft.Extensions.Configuration;
using RemoteOps.Cloud.Configuration;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// Contrato de configuração com o OPERADOR (spec §9 + runbook do Debian).
///
/// O deploy usa os nomes REMOTEOPS_DB_CONNECTION e Jwt__SecretKeyBase64; o código e
/// os testes já usavam ConnectionStrings__Default e Jwt__SigningKey. Os dois valem —
/// estes testes travam a precedência e, principalmente, a FALHA RÁPIDA: erro de
/// configuração tem que estourar no startup do container, não no primeiro login.
/// </summary>
public sealed class DeploymentConfigTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    // ── Banco ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ConnectionString_UsaConnectionStringsDefault()
    {
        var config = Config(("ConnectionStrings:Default", "Host=db;Database=remoteops"));

        Assert.Equal("Host=db;Database=remoteops", DeploymentConfig.ResolveConnectionString(config));
    }

    [Fact]
    public void ConnectionString_AceitaAliasRemoteOpsDbConnection()
    {
        // É o nome que o compose/runbook usam; sem isto o container sobe sem banco.
        var config = Config(("REMOTEOPS_DB_CONNECTION", "Host=postgres;Database=remoteops"));

        Assert.Equal("Host=postgres;Database=remoteops", DeploymentConfig.ResolveConnectionString(config));
    }

    [Fact]
    public void ConnectionString_ConnectionStringsDefaultTemPrecedencia()
    {
        var config = Config(
            ("ConnectionStrings:Default", "Host=explicito"),
            ("REMOTEOPS_DB_CONNECTION", "Host=alias"));

        Assert.Equal("Host=explicito", DeploymentConfig.ResolveConnectionString(config));
    }

    [Fact]
    public void ConnectionString_SemNenhuma_Falha()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeploymentConfig.ResolveConnectionString(Config()));

        Assert.Contains("REMOTEOPS_DB_CONNECTION", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionString_VaziaOuEspacos_Falha()
    {
        // Env var declarada e vazia no .env é o erro clássico do operador: precisa
        // falhar igual a "não configurada", e não virar connection string inválida.
        Assert.Throws<InvalidOperationException>(
            () => DeploymentConfig.ResolveConnectionString(Config(("REMOTEOPS_DB_CONNECTION", "   "))));
    }

    // ── Chave do JWT ──────────────────────────────────────────────────────────

    [Fact]
    public void JwtKey_SecretKeyBase64_EhDecodificada()
    {
        var raw = new byte[32];
        for (var i = 0; i < raw.Length; i++) raw[i] = (byte)i;
        var config = Config(("Jwt:SecretKeyBase64", Convert.ToBase64String(raw)));

        Assert.Equal(raw, DeploymentConfig.ResolveJwtSigningKey(config));
    }

    [Fact]
    public void JwtKey_SigningKeyLegado_ViraBytesUtf8()
    {
        // Compatibilidade: deploys/testes existentes passam a chave como texto puro.
        const string legacy = "remoteops-test-signing-key-32bytes!!";
        var config = Config(("Jwt:SigningKey", legacy));

        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(legacy), DeploymentConfig.ResolveJwtSigningKey(config));
    }

    [Fact]
    public void JwtKey_SecretKeyBase64_TemPrecedenciaSobreLegado()
    {
        var raw = Enumerable.Repeat((byte)7, 32).ToArray();
        var config = Config(
            ("Jwt:SecretKeyBase64", Convert.ToBase64String(raw)),
            ("Jwt:SigningKey", "remoteops-test-signing-key-32bytes!!"));

        Assert.Equal(raw, DeploymentConfig.ResolveJwtSigningKey(config));
    }

    [Fact]
    public void JwtKey_SemNenhuma_Falha()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeploymentConfig.ResolveJwtSigningKey(Config()));

        Assert.Contains("Jwt__SecretKeyBase64", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JwtKey_Base64Invalido_Falha()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeploymentConfig.ResolveJwtSigningKey(Config(("Jwt:SecretKeyBase64", "isto nao e base64!!"))));

        Assert.Contains("base64", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JwtKey_CurtaDemais_Falha()
    {
        // HMAC-SHA256 com chave < 256 bits enfraquece a assinatura do access token.
        var config = Config(("Jwt:SecretKeyBase64", Convert.ToBase64String(new byte[16])));

        var ex = Assert.Throws<InvalidOperationException>(() => DeploymentConfig.ResolveJwtSigningKey(config));

        Assert.Contains("32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JwtKey_LegadoCurtoDemais_TambemFalha()
    {
        Assert.Throws<InvalidOperationException>(
            () => DeploymentConfig.ResolveJwtSigningKey(Config(("Jwt:SigningKey", "curta"))));
    }

    [Fact]
    public void JwtKey_MensagemDeErro_NaoVazaOValor()
    {
        // A mensagem vai para o log do container; não pode conter a chave.
        var segredo = Convert.ToBase64String(new byte[16]);
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeploymentConfig.ResolveJwtSigningKey(Config(("Jwt:SecretKeyBase64", segredo))));

        Assert.DoesNotContain(segredo, ex.Message, StringComparison.Ordinal);
    }
}
