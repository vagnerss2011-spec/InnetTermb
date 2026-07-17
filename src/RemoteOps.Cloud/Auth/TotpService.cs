using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RemoteOps.Cloud.Auth;

/// <summary>
/// TOTP (RFC 6238) — segundo fator de AUTENTICAÇÃO. HMAC-SHA1, passo de 30s, 6 dígitos, janela ±1.
///
/// <para><b>FRONTEIRA E2EE (importante — não confundir):</b> este TOTP prova IDENTIDADE ao servidor
/// (protege o LOGIN/acesso à conta). Ele <b>NÃO participa da criptografia do cofre</b>: a chave do
/// cofre continua vindo só da senha (Argon2id → KEK → AMK, spec Fase 1 §4). Consequência prática: um
/// "reset de 2FA" (admin desliga <c>MfaRequired</c>, ou o usuário usa a chave de recuperação de
/// acesso da Fase 4) devolve o ACESSO ao servidor — <b>nunca</b> decifra o cofre. Quem esquece a
/// senha E perde a chave de recuperação continua com o cofre irrecuperável por design, com ou sem
/// 2FA. Ver docs/superpowers/specs/2026-07-17-cloud-sync-2fa-phase3-design.md.</para>
///
/// <para><b>Cripto built-in de propósito:</b> HMACSHA1 do runtime, ~zero linhas de dependência. Não
/// vendoramos lib de TOTP no app de credenciais — é ~30 linhas de código auditável. PURO (sem
/// IO/rede/relógio implícito): o <see cref="Verify"/> recebe o <c>now</c> de fora, então o teste é
/// determinístico e o vetor da RFC prova a corretude.</para>
/// </summary>
public static class TotpService
{
    /// <summary>Segredo TOTP: 20 bytes CSPRNG (160 bits — casa exatamente com 32 chars Base32, sem padding).</summary>
    public const int SecretBytes = 20;

    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    /// <summary>Issuer exibido no app autenticador e embutido no otpauth URI.</summary>
    public const string Issuer = "RemoteOps";

    /// <summary>Aceita o passo anterior e o próximo além do atual (tolera relógio dessincronizado ±30s).</summary>
    private const int DefaultWindow = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>Contador de passo TOTP: floor(unixSeconds / 30).</summary>
    public static long TimeStep(DateTimeOffset time) => time.ToUnixTimeSeconds() / PeriodSeconds;

    /// <summary>Código de 6 dígitos para um passo (HMAC-SHA1 + truncamento dinâmico da RFC 4226).</summary>
    public static string ComputeCode(ReadOnlySpan<byte> secret, long timeStepCounter)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timeStepCounter);

        Span<byte> hmac = stackalloc byte[HMACSHA1.HashSizeInBytes]; // 20 bytes
        HMACSHA1.HashData(secret, counter, hmac);

        // Truncamento dinâmico (RFC 4226 §5.3): o nibble baixo do último byte escolhe o offset.
        int offset = hmac[^1] & 0x0F;
        int binary = ((hmac[offset] & 0x7F) << 24)
                     | ((hmac[offset + 1] & 0xFF) << 16)
                     | ((hmac[offset + 2] & 0xFF) << 8)
                     | (hmac[offset + 3] & 0xFF);

        int otp = binary % 1_000_000; // Digits = 6
        return otp.ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
    }

    /// <summary>
    /// Valida um código contra o segredo, na janela ±1. Tempo-constante quanto ao passo que casou
    /// (percorre os 3 passos SEMPRE e usa <see cref="CryptographicOperations.FixedTimeEquals"/>),
    /// para não vazar por timing qual passo bateu.
    ///
    /// <para><b>Sem anti-replay</b> (decisão documentada): o mesmo código pode ser reapresentado
    /// dentro da mesma janela. A RFC 6238 permite; um device único não sofre com isso, e um store de
    /// "último passo usado" por conta é complexidade que fica de follow-up. O rate-limit do /auth e a
    /// exigência de senha ANTES do TOTP já contêm o abuso.</para>
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, int window = DefaultWindow)
    {
        if (!IsWellFormed(code))
        {
            return false;
        }

        byte[] codeBytes = Encoding.ASCII.GetBytes(code!);
        long step = TimeStep(now);
        bool matched = false;

        // Sem short-circuit: avalia TODOS os passos da janela para manter o timing plano.
        for (int w = -window; w <= window; w++)
        {
            byte[] candidate = Encoding.ASCII.GetBytes(ComputeCode(secret, step + w));
            matched |= CryptographicOperations.FixedTimeEquals(candidate, codeBytes);
        }

        return matched;
    }

    /// <summary>Base32 (RFC 4648, maiúsculas, sem padding) — o formato que os apps autenticadores leem.</summary>
    public static string ToBase32(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bitsLeft = 0;

        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }

        if (bitsLeft > 0)
        {
            sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// otpauth:// URI (formato de facto do QR) — issuer "RemoteOps", account = e-mail. O app do
    /// usuário lê isto (via QR ou colado) e passa a gerar os códigos.
    /// </summary>
    public static string BuildOtpauthUri(string accountEmail, ReadOnlySpan<byte> secret)
    {
        string label = Uri.EscapeDataString($"{Issuer}:{accountEmail}");
        string secretB32 = ToBase32(secret);
        return $"otpauth://totp/{label}"
               + $"?secret={secretB32}"
               + $"&issuer={Uri.EscapeDataString(Issuer)}"
               + "&algorithm=SHA1"
               + $"&digits={Digits}"
               + $"&period={PeriodSeconds}";
    }

    private static bool IsWellFormed(string? code)
    {
        if (code is null || code.Length != Digits)
        {
            return false;
        }

        foreach (char c in code)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
