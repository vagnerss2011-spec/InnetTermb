using System.Text;

namespace RemoteOps.Cloud.Configuration;

/// <summary>
/// Resolve a configuração de deploy (banco + chave do JWT) e FALHA RÁPIDO quando ela
/// está errada.
///
/// Por que existe: o contrato com o operador (spec cloud-sync-e2ee-phase1 §9 e o
/// runbook do Debian) usa <c>REMOTEOPS_DB_CONNECTION</c> e <c>Jwt__SecretKeyBase64</c>,
/// enquanto o código e os testes já usavam <c>ConnectionStrings__Default</c> e
/// <c>Jwt__SigningKey</c>. Aceitar os dois mantém o deploy documentado funcionando sem
/// desfazer o que já existe. Centralizar aqui garante que Program.cs (validação do
/// token), TokenService (assinatura) e AccountService (decoy do /auth/kdf) enxerguem
/// exatamente a MESMA chave — se cada um resolvesse por conta própria, configurar só
/// um dos nomes deixaria o servidor assinando com uma chave e validando com outra.
/// </summary>
public static class DeploymentConfig
{
    /// <summary>Piso da chave HMAC-SHA256 do access token: 256 bits.</summary>
    public const int MinJwtKeyBytes = 32;

    /// <summary>Connection string do PostgreSQL. Lança se não houver nenhuma configurada.</summary>
    public static string ResolveConnectionString(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var value = FirstNonBlank(
            config.GetConnectionString("Default"),
            config["REMOTEOPS_DB_CONNECTION"]);

        return value ?? throw new InvalidOperationException(
            "Banco não configurado. Defina REMOTEOPS_DB_CONNECTION (ou ConnectionStrings__Default) " +
            "com a connection string do PostgreSQL. Ver docs/runbook-deploy-debian.md.");
    }

    /// <summary>
    /// Bytes da chave de assinatura do JWT. <c>Jwt__SecretKeyBase64</c> (preferido) é
    /// decodificado de base64; <c>Jwt__SigningKey</c> (legado) vira UTF-8 puro.
    /// </summary>
    public static byte[] ResolveJwtSigningKey(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var base64 = FirstNonBlank(config["Jwt:SecretKeyBase64"]);
        if (base64 is not null)
        {
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                // Sem o valor na mensagem: ela vai parar no log do container.
                throw new InvalidOperationException(
                    "Jwt__SecretKeyBase64 não é base64 válido. Gere com: openssl rand -base64 32");
            }

            return Validated(decoded, "Jwt__SecretKeyBase64");
        }

        var legacy = FirstNonBlank(config["Jwt:SigningKey"])
            ?? throw new InvalidOperationException(
                "Chave do JWT não configurada. Defina Jwt__SecretKeyBase64 (recomendado, " +
                "gere com: openssl rand -base64 32) ou Jwt__SigningKey. Ver docs/runbook-deploy-debian.md.");

        return Validated(Encoding.UTF8.GetBytes(legacy), "Jwt__SigningKey");
    }

    private static byte[] Validated(byte[] key, string source)
    {
        if (key.Length < MinJwtKeyBytes)
            throw new InvalidOperationException(
                $"{source} precisa ter no mínimo {MinJwtKeyBytes} bytes ({MinJwtKeyBytes * 8} bits) — " +
                $"veio com {key.Length}. Gere com: openssl rand -base64 {MinJwtKeyBytes}");

        return key;
    }

    /// <summary>Trata env var declarada e vazia como ausente — é o erro clássico de .env.</summary>
    private static string? FirstNonBlank(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
}
