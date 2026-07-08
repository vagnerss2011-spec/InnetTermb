namespace RemoteOps.Desktop.Terminal.Vt;

/// <summary>
/// Uma célula da grade do terminal: um caractere visível mais cor e atributos. Struct para
/// evitar alocação por célula (a grade é um array plano de TerminalCell).
/// MVP: caracteres do BMP (sem pares substitutos); suficiente para CLI de roteador.
/// </summary>
public struct TerminalCell
{
    public char Rune;
    public TerminalColor Foreground;
    public TerminalColor Background;
    public bool Bold;
    public bool Underline;
    public bool Inverse;

    /// <summary>Célula vazia (espaço, cores padrão) — usada ao limpar/rolar.</summary>
    public static TerminalCell Blank => new()
    {
        Rune = ' ',
        Foreground = TerminalColor.Default,
        Background = TerminalColor.Default,
    };
}
