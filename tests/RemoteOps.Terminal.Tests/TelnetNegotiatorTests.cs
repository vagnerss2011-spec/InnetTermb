using FluentAssertions;
using RemoteOps.Terminal.Telnet;

namespace RemoteOps.Terminal.Tests;

public sealed class TelnetNegotiatorTests
{
    private static readonly byte IAC  = 0xFF;
    private static readonly byte WILL = 0xFB;
    private static readonly byte WONT = 0xFC;
    private static readonly byte DO   = 0xFD;
    private static readonly byte DONT = 0xFE;

    [Fact]
    public void PlainText_PassesThrough()
    {
        var input = "Hello\r\n"u8.ToArray();
        var (data, resp) = TelnetNegotiator.Process(input);

        data.Should().BeEquivalentTo(input);
        resp.Should().BeEmpty();
    }

    [Fact]
    public void EscapedIac_BecomesLiteralByte()
    {
        var input = new byte[] { IAC, IAC };
        var (data, resp) = TelnetNegotiator.Process(input);

        data.Should().BeEquivalentTo(new byte[] { 0xFF });
        resp.Should().BeEmpty();
    }

    [Fact]
    public void WillNaws_RespondsWithDo()
    {
        // Server sends WILL NAWS (31) → we respond DO NAWS
        var input = new byte[] { IAC, WILL, 31 };
        var (data, resp) = TelnetNegotiator.Process(input);

        data.Should().BeEmpty();
        resp.Should().BeEquivalentTo(new byte[] { IAC, DO, 31 });
    }

    [Fact]
    public void WillUnknownOption_RespondsWithDont()
    {
        // Server sends WILL ECHO (1) — not accepted
        var input = new byte[] { IAC, WILL, 1 };
        var (_, resp) = TelnetNegotiator.Process(input);

        resp.Should().BeEquivalentTo(new byte[] { IAC, DONT, 1 });
    }

    [Fact]
    public void DoUnknownOption_RespondsWithWont()
    {
        // Server sends DO BINARY (0) — not accepted
        var input = new byte[] { IAC, DO, 0 };
        var (_, resp) = TelnetNegotiator.Process(input);

        resp.Should().BeEquivalentTo(new byte[] { IAC, WONT, 0 });
    }

    [Fact]
    public void IacCommandStrippedFromData()
    {
        var input = new byte[] { (byte)'A', IAC, WILL, 1, (byte)'B' };
        var (data, _) = TelnetNegotiator.Process(input);

        data.Should().BeEquivalentTo(new byte[] { (byte)'A', (byte)'B' });
    }

    [Fact]
    public void BuildNawsCommand_HasCorrectShape()
    {
        var naws = TelnetNegotiator.BuildNawsCommand(cols: 80, rows: 24);

        naws.Length.Should().Be(9);
        naws[0].Should().Be(IAC);  // IAC
        naws[1].Should().Be(0xFA); // SB
        naws[2].Should().Be(31);   // NAWS
        naws[3].Should().Be(0);    // cols-hi
        naws[4].Should().Be(80);   // cols-lo
        naws[5].Should().Be(0);    // rows-hi
        naws[6].Should().Be(24);   // rows-lo
        naws[7].Should().Be(IAC);  // IAC
        naws[8].Should().Be(0xF0); // SE
    }
}
