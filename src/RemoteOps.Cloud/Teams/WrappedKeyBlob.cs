namespace RemoteOps.Cloud.Teams;

/// <summary>
/// A única definição do que o servidor aceita como "chave embrulhada". Vale para os TRÊS blobs do
/// time (o do convite, o do aceite e o publicado em <c>PUT /workspaces/{id}/key</c>).
///
/// <para><b>Por que uma classe só:</b> esta é uma guarda de FORMATO com peso de segurança e ela
/// existe em mais de um endpoint. Duas cópias com limites diferentes é exatamente o tipo de coisa
/// que envelhece torto — um caminho passa a aceitar o que o outro recusa, e o defeito só aparece
/// como "o cofre não abre" na máquina do colega, meses depois.</para>
///
/// <para><b>O que o servidor NÃO faz aqui:</b> interpretar o conteúdo. Ele não tem a AMK de ninguém
/// nem a chave do convite, então o blob é opaco por definição — só o tamanho é verificável.</para>
/// </summary>
internal static class WrappedKeyBlob
{
    /// <summary>
    /// Piso: nonce(12) + tag(16) = 28 bytes já sem nenhum ciphertext. Abaixo disso o blob está
    /// truncado, e é melhor falhar AQUI do que meses depois, do outro lado.
    /// </summary>
    private const int MinBytes = 28;

    /// <summary>Teto folgado: um embrulho de chave de 32 bytes nunca chega perto disto.</summary>
    private const int MaxBytes = 1024;

    /// <summary><exception cref="ArgumentException">Vazio, base64 inválido ou fora do tamanho (vira 400).</exception></summary>
    public static byte[] Decode(string? b64, string field)
    {
        if (string.IsNullOrWhiteSpace(b64))
            throw new ArgumentException($"{field} é obrigatório.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { throw new ArgumentException($"{field} não é base64 válido."); }

        if (bytes.Length is < MinBytes or > MaxBytes)
            throw new ArgumentException($"{field} não tem tamanho de chave embrulhada.");

        return bytes;
    }
}
