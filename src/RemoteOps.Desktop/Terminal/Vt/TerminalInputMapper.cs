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
    /// <param name="backspaceSendsControlH">
    /// Quando true, a tecla Backspace envia BS (0x08, = Ctrl+H) em vez de DEL (0x7F). Necessário
    /// para equipamentos legados (ex.: OLT Huawei) que não entendem o DEL padrão. Ver a opção
    /// "Backspace key" do PuTTY e <see cref="RemoteOps.Contracts.Assets.TerminalBackspaceModes"/>.
    /// </param>
    public static byte[]? MapKey(Key key, ModifierKeys modifiers, bool backspaceSendsControlH = false)
    {
        bool alt = (modifiers & ModifierKeys.Alt) != 0;
        // AltGr chega ao WPF como Ctrl+Alt: NÃO tratar como Ctrl+tecla, senão AltGr+letra em
        // teclados ABNT2/internacionais viraria byte de controle (ex.: AltGr+Q → 0x11/XON!) em vez
        // de deixar o caractere composto chegar pelo TextInput.
        bool ctrl = (modifiers & ModifierKeys.Control) != 0 && !alt;

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
                case Key.Space: return new byte[] { 0x00 };           // Ctrl+Espaço = NUL
            }
        }

        return key switch
        {
            // ESPAÇO fica AQUI (KeyDown), não no TextInput: quirk do WPF — com um TextBox focado
            // (nosso KeyboardSink), a barra de espaço NÃO dispara PreviewTextInput/TextInput no
            // teclado físico (só letras/símbolos passam por lá). Sem este mapa, o espaço morre e
            // "display clock" vira "displayclock". Marcar Handled no KeyDown também suprime
            // qualquer TextInput do espaço, então não há envio duplicado.
            Key.Space => new byte[] { 0x20 },
            Key.Enter => new byte[] { 0x0D },
            // Backspace: DEL (0x7F) é o padrão VT/xterm; alguns equipamentos legados (OLT Huawei)
            // só entendem BS (0x08 = Ctrl+H). Escolhível por host (ver EndpointProfile.BackspaceMode).
            Key.Back => backspaceSendsControlH ? new byte[] { 0x08 } : new byte[] { 0x7F },
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
