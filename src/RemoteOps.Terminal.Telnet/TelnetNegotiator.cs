namespace RemoteOps.Terminal.Telnet;

/// <summary>
/// Minimal RFC 854 IAC command parser and responder.
/// Accepts NAWS (31) and TTYPE (24) offers; rejects everything else.
/// Strips IAC commands from the data stream so xterm.js only receives printable bytes.
/// </summary>
internal static class TelnetNegotiator
{
    private const byte IAC  = 0xFF;
    private const byte WILL = 0xFB;
    private const byte WONT = 0xFC;
    private const byte DO   = 0xFD;
    private const byte DONT = 0xFE;
    private const byte SB   = 0xFA; // subnegotiation begin
    private const byte SE   = 0xF0; // subnegotiation end

    private const byte OPT_TTYPE = 24;
    private const byte OPT_NAWS  = 31;

    internal static (byte[] data, byte[] responses) Process(ReadOnlySpan<byte> raw)
    {
        var data = new List<byte>(raw.Length);
        var resp = new List<byte>();
        int i = 0;

        while (i < raw.Length)
        {
            if (raw[i] != IAC)
            {
                data.Add(raw[i++]);
                continue;
            }

            i++; // consume IAC
            if (i >= raw.Length) break;

            byte cmd = raw[i++];

            if (cmd == IAC)
            {
                // Escaped 0xFF — literal data byte.
                data.Add(0xFF);
                continue;
            }

            if (cmd == SB)
            {
                // Skip subnegotiation until IAC SE.
                while (i < raw.Length - 1 && !(raw[i] == IAC && raw[i + 1] == SE))
                    i++;
                i += 2; // consume IAC SE
                continue;
            }

            if (cmd is WILL or WONT or DO or DONT)
            {
                if (i >= raw.Length) break;
                byte opt = raw[i++];
                Negotiate(cmd, opt, resp);
                continue;
            }

            // Unknown single-byte command — ignore.
        }

        return ([.. data], [.. resp]);
    }

    private static void Negotiate(byte cmd, byte opt, List<byte> resp)
    {
        // Accept TTYPE and NAWS; refuse everything else.
        if (opt is OPT_TTYPE or OPT_NAWS)
        {
            // DO → WILL, WILL → DO (affirmative)
            resp.Add(IAC);
            resp.Add(cmd == DO ? WILL : DO);
            resp.Add(opt);
        }
        else
        {
            // WILL → DONT, DO → WONT (negative)
            resp.Add(IAC);
            resp.Add(cmd == WILL ? DONT : WONT);
            resp.Add(opt);
        }
    }

    internal static byte[] BuildNawsCommand(int cols, int rows)
    {
        // IAC SB NAWS <cols-hi> <cols-lo> <rows-hi> <rows-lo> IAC SE
        return [IAC, SB, OPT_NAWS,
            (byte)(cols >> 8), (byte)(cols & 0xFF),
            (byte)(rows >> 8), (byte)(rows & 0xFF),
            IAC, SE];
    }
}
