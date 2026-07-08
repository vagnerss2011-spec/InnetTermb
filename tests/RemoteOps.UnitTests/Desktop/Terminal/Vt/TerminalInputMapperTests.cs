using System.Windows.Input;
using RemoteOps.Desktop.Terminal.Vt;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Terminal.Vt;

public sealed class TerminalInputMapperTests
{
    [Fact]
    public void Enter_SendsCarriageReturn()
        => Assert.Equal(new byte[] { 0x0D }, TerminalInputMapper.MapKey(Key.Enter, ModifierKeys.None));

    [Fact]
    public void Backspace_SendsDel()
        => Assert.Equal(new byte[] { 0x7F }, TerminalInputMapper.MapKey(Key.Back, ModifierKeys.None));

    [Fact]
    public void Escape_SendsEsc()
        => Assert.Equal(new byte[] { 0x1B }, TerminalInputMapper.MapKey(Key.Escape, ModifierKeys.None));

    [Theory]
    [InlineData(Key.Up, (byte)'A')]
    [InlineData(Key.Down, (byte)'B')]
    [InlineData(Key.Right, (byte)'C')]
    [InlineData(Key.Left, (byte)'D')]
    public void ArrowKeys_SendCsiSequences(Key key, byte final)
        => Assert.Equal(new byte[] { 0x1B, (byte)'[', final }, TerminalInputMapper.MapKey(key, ModifierKeys.None));

    [Fact]
    public void CtrlC_SendsEtx_0x03()
        => Assert.Equal(new byte[] { 0x03 }, TerminalInputMapper.MapKey(Key.C, ModifierKeys.Control));

    [Fact]
    public void CtrlA_And_CtrlZ_MapToControlRange()
    {
        Assert.Equal(new byte[] { 0x01 }, TerminalInputMapper.MapKey(Key.A, ModifierKeys.Control));
        Assert.Equal(new byte[] { 0x1A }, TerminalInputMapper.MapKey(Key.Z, ModifierKeys.Control));
    }

    [Fact]
    public void F1_SendsSs3_And_F5_SendsCsiTilde()
    {
        Assert.Equal(new byte[] { 0x1B, (byte)'O', (byte)'P' }, TerminalInputMapper.MapKey(Key.F1, ModifierKeys.None));
        Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'1', (byte)'5', (byte)'~' }, TerminalInputMapper.MapKey(Key.F5, ModifierKeys.None));
    }

    [Fact]
    public void PageUp_SendsCsiTilde5()
        => Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'5', (byte)'~' }, TerminalInputMapper.MapKey(Key.PageUp, ModifierKeys.None));

    [Fact]
    public void PlainLetter_ReturnsNull_SoTextInputHandlesIt()
        => Assert.Null(TerminalInputMapper.MapKey(Key.A, ModifierKeys.None));

    // ESPAÇO precisa ser mapeado no KeyDown: com o KeyboardSink (TextBox) focado, o WPF NÃO
    // dispara PreviewTextInput pra barra de espaço no teclado físico — sem este mapa o espaço
    // some ("display clock" → "displayclock"). Regressão do bug de campo da v1.2.19.
    [Fact]
    public void Space_SendsSpaceByte()
        => Assert.Equal(new byte[] { 0x20 }, TerminalInputMapper.MapKey(Key.Space, ModifierKeys.None));

    [Fact]
    public void ShiftSpace_AlsoSendsSpaceByte()
        => Assert.Equal(new byte[] { 0x20 }, TerminalInputMapper.MapKey(Key.Space, ModifierKeys.Shift));

    [Fact]
    public void CtrlSpace_SendsNul()
        => Assert.Equal(new byte[] { 0x00 }, TerminalInputMapper.MapKey(Key.Space, ModifierKeys.Control));

    // AltGr chega como Ctrl+Alt: NÃO pode virar byte de controle (AltGr+Q em ABNT2 viraria
    // 0x11/XON e engoliria o caractere composto). Tem que devolver null → TextInput entrega o char.
    [Theory]
    [InlineData(Key.Q)]
    [InlineData(Key.W)]
    [InlineData(Key.E)]
    [InlineData(Key.OemOpenBrackets)]
    public void AltGr_Combos_ReturnNull_SoComposedCharArrivesViaTextInput(Key key)
        => Assert.Null(TerminalInputMapper.MapKey(key, ModifierKeys.Control | ModifierKeys.Alt));
}
