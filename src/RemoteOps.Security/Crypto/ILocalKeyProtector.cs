namespace RemoteOps.Security.Crypto;

/// <summary>
/// Proteção local de material de chave. No Windows é implementado via DPAPI
/// (escopo do usuário), de modo que a chave protegida só seja recuperável pelo
/// mesmo usuário na mesma máquina. Abstraído para permitir teste cross-platform.
/// </summary>
public interface ILocalKeyProtector
{
    /// <summary>Protege <paramref name="plaintext"/> ligando ao usuário/máquina atual.</summary>
    byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy);

    /// <summary>
    /// Recupera o material protegido. Lança se o blob foi protegido por outro
    /// usuário/máquina ou se foi adulterado.
    /// </summary>
    byte[] Unprotect(ReadOnlySpan<byte> protectedBlob, ReadOnlySpan<byte> entropy);
}
