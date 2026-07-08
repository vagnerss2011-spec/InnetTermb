using System.Text;
using RemoteOps.Desktop.Terminal.Vt;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Terminal.Vt;

public sealed class AnsiParserTests
{
    private static (TerminalScreen screen, AnsiParser parser) New(int cols = 10, int rows = 4)
    {
        var s = new TerminalScreen(cols, rows);
        return (s, new AnsiParser(s));
    }

    private static void Feed(AnsiParser p, string text) => p.Feed(Encoding.UTF8.GetBytes(text));

    private static string RowTrim(TerminalScreen s, int row)
    {
        var sb = new StringBuilder();
        for (int c = 0; c < s.Columns; c++)
        {
            sb.Append(s[row, c].Rune);
        }
        return sb.ToString().TrimEnd();
    }

    [Fact]
    public void PlainText_WritesToRow_AndAdvancesCursor()
    {
        var (s, p) = New();
        Feed(p, "hi");
        Assert.Equal("hi", RowTrim(s, 0));
        Assert.Equal(0, s.CursorRow);
        Assert.Equal(2, s.CursorColumn);
    }

    [Fact]
    public void CarriageReturnLineFeed_MovesToNextRow()
    {
        var (s, p) = New();
        Feed(p, "ab\r\ncd");
        Assert.Equal("ab", RowTrim(s, 0));
        Assert.Equal("cd", RowTrim(s, 1));
    }

    [Fact]
    public void LineWrap_AtRightEdge_WrapsToNextRow()
    {
        var (s, p) = New(cols: 3, rows: 4);
        Feed(p, "abcd");
        Assert.Equal("abc", RowTrim(s, 0));
        Assert.Equal("d", RowTrim(s, 1));
    }

    [Fact]
    public void LineFeed_PastBottom_ScrollsUp()
    {
        var (s, p) = New(cols: 3, rows: 2);
        Feed(p, "a\r\nb\r\nc");
        Assert.Equal("b", RowTrim(s, 0)); // "a" rolou pra fora
        Assert.Equal("c", RowTrim(s, 1));
    }

    [Fact]
    public void Backspace_MovesCursorLeft_OverwriteReplaces()
    {
        var (s, p) = New();
        Feed(p, "abc\b\bX");
        Assert.Equal("aXc", RowTrim(s, 0));
    }

    [Fact]
    public void Tab_AdvancesToNextTabStop()
    {
        var (s, p) = New(cols: 20, rows: 2);
        Feed(p, "a\tb");
        Assert.Equal(0, s[0, 0].Rune == 'a' ? 0 : 1);
        Assert.Equal('a', s[0, 0].Rune);
        Assert.Equal('b', s[0, 8].Rune); // tab stop em coluna 8
    }

    [Fact]
    public void Csi_Cup_PositionsCursor_OneBased()
    {
        var (s, p) = New();
        Feed(p, "\x1b[2;3HX"); // linha 2, coluna 3 (1-based) → (1,2)
        Assert.Equal('X', s[1, 2].Rune);
    }

    [Fact]
    public void Csi_CursorForward_MovesRight()
    {
        var (s, p) = New();
        Feed(p, "\x1b[3CX");
        Assert.Equal('X', s[0, 3].Rune);
    }

    [Fact]
    public void Csi_EraseInLine_Mode0_ClearsCursorToEnd()
    {
        var (s, p) = New();
        Feed(p, "abcde\x1b[3D\x1b[0K"); // cursor volta 3 (col2), apaga col2..fim
        Assert.Equal("ab", RowTrim(s, 0));
    }

    [Fact]
    public void Csi_EraseInDisplay_Mode2_ClearsScreen()
    {
        var (s, p) = New();
        Feed(p, "abc\r\ndef\x1b[2J");
        Assert.Equal("", RowTrim(s, 0));
        Assert.Equal("", RowTrim(s, 1));
    }

    [Fact]
    public void Sgr_Foreground_SetsColor_ResetClearsIt()
    {
        var (s, p) = New();
        Feed(p, "\x1b[31mR\x1b[0mN");
        Assert.Equal(TerminalColor.FromRgb(0xCD, 0x00, 0x00), s[0, 0].Foreground);
        Assert.True(s[0, 1].Foreground.IsDefault);
    }

    [Fact]
    public void Sgr_BrightForeground_And256_And_Rgb()
    {
        var (s, p) = New(cols: 5, rows: 2);
        Feed(p, "\x1b[91mA");           // bright red (índice 9)
        Feed(p, "\x1b[38;5;9mB");       // 256 índice 9 = bright red
        Feed(p, "\x1b[38;2;10;20;30mC"); // RGB direto
        Assert.Equal(TerminalColor.FromRgb(0xFF, 0x00, 0x00), s[0, 0].Foreground);
        Assert.Equal(TerminalColor.FromRgb(0xFF, 0x00, 0x00), s[0, 1].Foreground);
        Assert.Equal(TerminalColor.FromRgb(10, 20, 30), s[0, 2].Foreground);
    }

    [Fact]
    public void Sgr_Attributes_BoldUnderlineInverse()
    {
        var (s, p) = New();
        Feed(p, "\x1b[1;4;7mX");
        Assert.True(s[0, 0].Bold);
        Assert.True(s[0, 0].Underline);
        Assert.True(s[0, 0].Inverse);
    }

    [Fact]
    public void Utf8_Multibyte_DecodesToChar()
    {
        var (s, p) = New();
        p.Feed(Encoding.UTF8.GetBytes("á"));
        Assert.Equal('á', s[0, 0].Rune);
    }

    [Fact]
    public void Utf8_SplitAcrossFeeds_StillDecodes()
    {
        var (s, p) = New();
        p.Feed(new byte[] { 0xC3 }); // 1º byte de "á"
        p.Feed(new byte[] { 0xA1 }); // 2º byte
        Assert.Equal('á', s[0, 0].Rune);
    }

    [Fact]
    public void Csi_SplitAcrossFeeds_StillDispatches()
    {
        var (s, p) = New();
        Feed(p, "\x1b[2");   // sequência CSI parcial
        Feed(p, ";3HX");     // completa: (1,2)
        Assert.Equal('X', s[1, 2].Rune);
    }

    [Fact]
    public void Osc_Title_IsIgnored_NotPrinted()
    {
        var (s, p) = New();
        Feed(p, "\x1b]0;meu titulo\aAB"); // \a = BEL (0x07) termina o OSC; \x07AB seria 1 char só
        Assert.Equal("AB", RowTrim(s, 0));
    }

    [Fact]
    public void UnknownPrivateCsi_IsIgnored_TextStillPrints()
    {
        var (s, p) = New();
        Feed(p, "\x1b[?25hAB"); // esconder/mostrar cursor — ignorado no MVP
        Assert.Equal("AB", RowTrim(s, 0));
    }
}
