using System.Text;

namespace RemoteOps.Desktop.Terminal.Vt;

/// <summary>
/// Lógica pura de seleção/cópia/colagem do terminal — sem UI nem clipboard, para ser testável.
/// A View (<see cref="TerminalScreenControl"/>) cuida do mouse, do highlight e do clipboard.
/// </summary>
public static class TerminalSelection
{
    /// <summary>
    /// Extrai o texto de uma seleção LINEAR (fluxo) entre dois pontos 0-based, em ordem de leitura.
    /// Espaços à direita de cada linha são aparados; linhas são unidas por '\n'. A ordem dos pontos
    /// não importa (é normalizada).
    /// </summary>
    public static string ExtractText(TerminalScreen screen, int startRow, int startCol, int endRow, int endCol)
    {
        if (screen is null)
        {
            return string.Empty;
        }

        Normalize(ref startRow, ref startCol, ref endRow, ref endCol);

        var sb = new StringBuilder();
        for (int row = startRow; row <= endRow && row < screen.Rows; row++)
        {
            int from = row == startRow ? startCol : 0;
            int to = row == endRow ? endCol : screen.Columns - 1;

            var line = new StringBuilder();
            for (int c = from; c <= to && c < screen.Columns; c++)
            {
                line.Append(screen[row, c].Rune);
            }

            sb.Append(TrimTrailingSpaces(line));
            if (row < endRow)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Normaliza quebras de linha de um texto colado para CR — o que o host espera para submeter
    /// cada linha (colar uma config multi-linha executa linha a linha).
    /// </summary>
    public static string NormalizePaste(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\r\n", "\r").Replace('\n', '\r');

    // garante (startRow,startCol) <= (endRow,endCol) em ordem de leitura
    private static void Normalize(ref int sr, ref int sc, ref int er, ref int ec)
    {
        bool startAfterEnd = sr > er || (sr == er && sc > ec);
        if (startAfterEnd)
        {
            (sr, er) = (er, sr);
            (sc, ec) = (ec, sc);
        }
    }

    private static string TrimTrailingSpaces(StringBuilder sb)
    {
        int end = sb.Length;
        while (end > 0 && sb[end - 1] == ' ')
        {
            end--;
        }
        return sb.ToString(0, end);
    }
}
