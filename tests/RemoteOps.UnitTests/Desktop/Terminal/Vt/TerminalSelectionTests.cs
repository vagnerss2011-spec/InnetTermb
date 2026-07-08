using System.Text;
using RemoteOps.Desktop.Terminal.Vt;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Terminal.Vt;

public sealed class TerminalSelectionTests
{
    private static TerminalScreen ScreenWith(int cols, int rows, string ascii)
    {
        var s = new TerminalScreen(cols, rows);
        new AnsiParser(s).Feed(Encoding.UTF8.GetBytes(ascii));
        return s;
    }

    [Fact]
    public void ExtractText_SingleLineRange()
    {
        var s = ScreenWith(20, 2, "hello world");
        Assert.Equal("hello", TerminalSelection.ExtractText(s, 0, 0, 0, 4));
    }

    [Fact]
    public void ExtractText_TrimsTrailingSpaces()
    {
        var s = ScreenWith(10, 2, "hi");
        Assert.Equal("hi", TerminalSelection.ExtractText(s, 0, 0, 0, 9)); // linha inteira → sem padding
    }

    [Fact]
    public void ExtractText_MultiLine_JoinsWithNewline()
    {
        var s = ScreenWith(10, 3, "abc\r\ndef");
        Assert.Equal("bc\nde", TerminalSelection.ExtractText(s, 0, 1, 1, 1));
    }

    [Fact]
    public void ExtractText_ReversedPoints_NormalizesToSameResult()
    {
        var s = ScreenWith(20, 2, "hello world");
        Assert.Equal("hello", TerminalSelection.ExtractText(s, 0, 4, 0, 0));
    }

    [Fact]
    public void NormalizePaste_ConvertsNewlinesToCarriageReturn()
    {
        Assert.Equal("a\rb\rc", TerminalSelection.NormalizePaste("a\r\nb\nc"));
        Assert.Equal(string.Empty, TerminalSelection.NormalizePaste(""));
    }
}
