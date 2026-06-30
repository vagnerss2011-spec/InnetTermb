namespace RemoteOps.Terminal.Telnet;

/// <summary>
/// Processa o protocolo Telnet (RFC 854/855): separa bytes de dados dos comandos IAC
/// e produz as respostas que devem ser enviadas ao servidor.
/// </summary>
internal sealed class TelnetNegotiator
{
    // RFC 854 command bytes
    private const byte Iac = 255;
    private const byte Se = 240;   // End of subnegotiation
    private const byte Sb = 250;   // Start of subnegotiation
    private const byte Will = 251;
    private const byte Wont = 252;
    private const byte Do = 253;
    private const byte Dont = 254;

    // Options
    private const byte OptEcho = 1;
    private const byte OptSga = 3;   // Suppress Go Ahead
    private const byte OptNaws = 31; // Negotiate About Window Size

    private ParseState _state = ParseState.Data;
    private byte _command;
    private readonly List<byte> _subBuffer = [];

    /// <summary>
    /// true se o servidor pediu DO NAWS; envia NAWS com BuildNaws() e resete para false.
    /// </summary>
    public bool PendingNaws { get; set; }

    /// <summary>
    /// Processa bytes brutos da rede. Retorna:
    /// <list type="bullet">
    /// <item><description>Data — bytes a entregar ao usuário.</description></item>
    /// <item><description>Response — bytes a enviar de volta ao servidor.</description></item>
    /// </list>
    /// </summary>
    public (byte[] Data, byte[] Response) Process(ReadOnlySpan<byte> input)
    {
        var data = new List<byte>(input.Length);
        var response = new List<byte>(8);

        foreach (byte b in input)
        {
            switch (_state)
            {
                case ParseState.Data:
                    if (b == Iac) _state = ParseState.AfterIac;
                    else data.Add(b);
                    break;

                case ParseState.AfterIac:
                    if (b == Iac) { data.Add(Iac); _state = ParseState.Data; }           // escaped IAC
                    else if (b == Sb) { _subBuffer.Clear(); _state = ParseState.SubNeg; }
                    else if (b is Will or Wont or Do or Dont) { _command = b; _state = ParseState.AfterCommand; }
                    else _state = ParseState.Data; // GA, NOP, DM, etc. — ignorar
                    break;

                case ParseState.AfterCommand:
                    HandleOption(_command, b, response);
                    _state = ParseState.Data;
                    break;

                case ParseState.SubNeg:
                    if (b == Iac) _state = ParseState.SubNegIac;
                    else _subBuffer.Add(b);
                    break;

                case ParseState.SubNegIac:
                    if (b == Se) _state = ParseState.Data;   // fim da subnego, ignoramos o conteúdo
                    else { _subBuffer.Add(Iac); _subBuffer.Add(b); _state = ParseState.SubNeg; }
                    break;
            }
        }

        return ([.. data], [.. response]);
    }

    /// <summary>Constrói o pacote NAWS para enviar ao servidor.</summary>
    public static byte[] BuildNaws(int cols, int rows) =>
    [
        Iac, Sb, OptNaws,
        (byte)(cols >> 8), (byte)(cols & 0xFF),
        (byte)(rows >> 8), (byte)(rows & 0xFF),
        Iac, Se,
    ];

    private void HandleOption(byte command, byte option, List<byte> response)
    {
        switch (command)
        {
            case Will:
                // Servidor diz WILL <opt> → respondemos DO (aceitamos) para Echo e SGA.
                if (option is OptEcho or OptSga) response.AddRange([Iac, Do, option]);
                else response.AddRange([Iac, Dont, option]);
                break;

            case Wont:
                response.AddRange([Iac, Dont, option]);
                break;

            case Do:
                // Servidor pede DO <opt> → respondemos WILL se suportamos.
                if (option == OptNaws)
                {
                    PendingNaws = true;
                    response.AddRange([Iac, Will, OptNaws]);
                }
                else if (option == OptSga)
                {
                    response.AddRange([Iac, Will, OptSga]);
                }
                else
                {
                    response.AddRange([Iac, Wont, option]);
                }
                break;

            case Dont:
                response.AddRange([Iac, Wont, option]);
                break;
        }
    }

    private enum ParseState { Data, AfterIac, AfterCommand, SubNeg, SubNegIac }
}
