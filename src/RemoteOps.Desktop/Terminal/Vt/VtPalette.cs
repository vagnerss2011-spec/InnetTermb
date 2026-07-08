namespace RemoteOps.Desktop.Terminal.Vt;

/// <summary>
/// Resolve cores indexadas do terminal (0–255) para RGB, usando a paleta padrão do xterm:
/// 0–15 = 16 cores ANSI; 16–231 = cubo 6×6×6; 232–255 = 24 tons de cinza.
/// </summary>
internal static class VtPalette
{
    // 16 cores base ANSI (paleta xterm padrão): 0-7 normais, 8-15 brilhantes.
    private static readonly (byte R, byte G, byte B)[] Ansi16 =
    {
        (0x00, 0x00, 0x00), // 0 preto
        (0xCD, 0x00, 0x00), // 1 vermelho
        (0x00, 0xCD, 0x00), // 2 verde
        (0xCD, 0xCD, 0x00), // 3 amarelo
        (0x00, 0x00, 0xEE), // 4 azul
        (0xCD, 0x00, 0xCD), // 5 magenta
        (0x00, 0xCD, 0xCD), // 6 ciano
        (0xE5, 0xE5, 0xE5), // 7 branco
        (0x7F, 0x7F, 0x7F), // 8 preto brilhante (cinza)
        (0xFF, 0x00, 0x00), // 9 vermelho brilhante
        (0x00, 0xFF, 0x00), // 10 verde brilhante
        (0xFF, 0xFF, 0x00), // 11 amarelo brilhante
        (0x5C, 0x5C, 0xFF), // 12 azul brilhante
        (0xFF, 0x00, 0xFF), // 13 magenta brilhante
        (0x00, 0xFF, 0xFF), // 14 ciano brilhante
        (0xFF, 0xFF, 0xFF), // 15 branco brilhante
    };

    /// <summary>Índice 0–255 → RGB. Fora do intervalo devolve <see cref="TerminalColor.Default"/>.</summary>
    public static TerminalColor Resolve(int index)
    {
        if (index is >= 0 and < 16)
        {
            var c = Ansi16[index];
            return TerminalColor.FromRgb(c.R, c.G, c.B);
        }

        if (index is >= 16 and < 232)
        {
            int i = index - 16;
            int r = i / 36;
            int g = (i % 36) / 6;
            int b = i % 6;
            return TerminalColor.FromRgb(CubeLevel(r), CubeLevel(g), CubeLevel(b));
        }

        if (index is >= 232 and < 256)
        {
            byte v = (byte)(8 + (index - 232) * 10);
            return TerminalColor.FromRgb(v, v, v);
        }

        return TerminalColor.Default;
    }

    // Nível de um eixo do cubo 6×6×6: 0 → 0, senão 55 + n*40.
    private static byte CubeLevel(int n) => (byte)(n == 0 ? 0 : 55 + n * 40);
}
