using System.Security.Cryptography;
using System.Text;

namespace RemoteOps.Security.Account;

/// <summary>
/// Chave de recuperação: 160 bits CSPRNG codificados em Base32 (RFC 4648, sem padding), em grupos
/// de 4 separados por hífen — ex.: <c>ABCD-EFGH-...</c>. É alta-entropia (não precisa de KDF caro).
/// <see cref="Parse"/> tolera minúsculas, espaços e hífens. Exibida uma única vez ao operador.
/// </summary>
public static class RecoveryKeyCodec
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int RawBytes = 20; // 160 bits → 32 chars base32 exatos

    public static string Generate()
    {
        byte[] raw = RandomNumberGenerator.GetBytes(RawBytes);
        try
        {
            string b32 = Encode(raw);
            var sb = new StringBuilder(b32.Length + b32.Length / 4);
            for (int i = 0; i < b32.Length; i++)
            {
                if (i > 0 && i % 4 == 0)
                {
                    sb.Append('-');
                }
                sb.Append(b32[i]);
            }
            return sb.ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    public static byte[] Parse(string recoveryKey)
    {
        var cleaned = new StringBuilder(recoveryKey.Length);
        foreach (char c in recoveryKey)
        {
            if (char.IsWhiteSpace(c) || c == '-')
            {
                continue;
            }
            cleaned.Append(char.ToUpperInvariant(c));
        }
        return Decode(cleaned.ToString());
    }

    private static string Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }
        return sb.ToString();
    }

    private static byte[] Decode(string s)
    {
        int buffer = 0, bits = 0;
        var outp = new List<byte>(s.Length * 5 / 8);
        foreach (char c in s)
        {
            int val = Alphabet.IndexOf(c);
            if (val < 0)
            {
                throw new FormatException($"Caractere inválido na chave de recuperação: '{c}'.");
            }
            buffer = (buffer << 5) | val;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                outp.Add((byte)((buffer >> bits) & 0xFF));
            }
        }
        return [.. outp];
    }
}
