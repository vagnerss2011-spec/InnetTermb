namespace RemoteOps.Desktop.Terminal.Vt;

/// <summary>
/// Cor de primeiro plano/fundo de uma célula do terminal: ou "padrão" (a View decide, a partir
/// do tema) ou um RGB concreto. Cores indexadas ANSI (16) e 256 são resolvidas para RGB na hora
/// do parse (paleta padrão xterm), então a grade guarda sempre RGB ou "padrão".
/// </summary>
public readonly record struct TerminalColor(bool IsDefault, byte R, byte G, byte B)
{
    /// <summary>Cor padrão do tema (fg/bg definidos pela View).</summary>
    public static readonly TerminalColor Default = new(true, 0, 0, 0);

    public static TerminalColor FromRgb(byte r, byte g, byte b) => new(false, r, g, b);
}
