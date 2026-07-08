using System.Text;
using System.Windows.Input;

namespace RemoteOps.Desktop.Terminal.Vt;

/// <summary>
/// Traduz teclas ESPECIAIS (Enter, setas, F-keys, Ctrl+letra…) para os bytes VT/xterm que o host
/// espera. Retorna null para teclas de texto comum — nesse caso a View usa o evento TextInput, que
/// já entrega o caractere digitado (inclusive acentos/IME) para ser enviado como UTF-8.
/// </summary>
public static class TerminalInputMapper
{
    public static byte[]? MapKey(Key key, ModifierKeys modifiers)
    {
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;

        // Ctrl + letra → caractere de controle (Ctrl+A=0x01 … Ctrl+Z=0x1A). Inclui Ctrl+C=0x03.
        if (ctrl && key is >= Key.A and <= Key.Z)
        {
            return new[] { (byte)(key - Key.A + 1) };
        }

        if (ctrl)
        {
            switch (key)
            {
                case Key.OemOpenBrackets: return new byte[] { 0x1B }; // Ctrl+[  = ESC
                case Key.Oem5: return new byte[] { 0x1C };            // Ctrl+\
                case Key.OemCloseBrackets: return new byte[] { 0x1D };// Ctrl+]
            }
        }

        return key switch
        {
            Key.Enter => new byte[] { 0x0D },
            Key.Back => new byte[] { 0x7F }, // DEL — o que a maioria dos hosts espera do Backspace
            Key.Tab => new byte[] { 0x09 },
            Key.Escape => new byte[] { 0x1B },
            Key.Up => Csi('A'),
            Key.Down => Csi('B'),
            Key.Right => Csi('C'),
            Key.Left => Csi('D'),
            Key.Home => Csi('H'),
            Key.End => Csi('F'),
            Key.PageUp => CsiTilde(5),
            Key.PageDown => CsiTilde(6),
            Key.Insert => CsiTilde(2),
            Key.Delete => CsiTilde(3),
            Key.F1 => Ss3('P'),
            Key.F2 => Ss3('Q'),
            Key.F3 => Ss3('R'),
            Key.F4 => Ss3('S'),
            Key.F5 => CsiTilde(15),
            Key.F6 => CsiTilde(17),
            Key.F7 => CsiTilde(18),
            Key.F8 => CsiTilde(19),
            Key.F9 => CsiTilde(20),
            Key.F10 => CsiTilde(21),
            Key.F11 => CsiTilde(23),
            Key.F12 => CsiTilde(24),
            _ => null,
        };
    }

    private static byte[] Csi(char final) => new byte[] { 0x1B, (byte)'[', (byte)final };

    private static byte[] Ss3(char final) => new byte[] { 0x1B, (byte)'O', (byte)final };

    private static byte[] CsiTilde(int n) => Encoding.ASCII.GetBytes("\x1b[" + n + "~");
}
