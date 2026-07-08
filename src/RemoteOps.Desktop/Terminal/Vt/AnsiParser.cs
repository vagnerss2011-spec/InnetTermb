using System.Collections.Generic;

namespace RemoteOps.Desktop.Terminal.Vt;

/// <summary>
/// Parser incremental de VT100/ANSI: recebe bytes crus (texto UTF-8 + sequências de escape) e
/// aplica as operações num <see cref="TerminalScreen"/>. Mantém estado entre chamadas — tanto
/// sequências de escape quanto caracteres UTF-8 podem chegar quebrados entre pedaços (chunks) do
/// stream SSH. Cobre o subconjunto usado por CLIs de rede (MikroTik/Cisco/Huawei): texto,
/// CR/LF/BS/TAB, CSI (movimento de cursor, apagar linha/tela, SGR cores/atributos 16/256/RGB),
/// OSC (título — ignorado) e alguns escapes simples. Sequências desconhecidas são ignoradas com
/// segurança (nunca quebram o fluxo nem lançam).
/// </summary>
public sealed class AnsiParser
{
    private readonly TerminalScreen _screen;

    private State _state = State.Ground;

    // parâmetros CSI
    private readonly List<int> _params = new();
    private int _paramValue;
    private bool _hasParam;

    // cursor salvo (ESC 7 / CSI s)
    private int _savedRow;
    private int _savedCol;

    // decodificação UTF-8 incremental
    private int _utf8Remaining;
    private int _utf8Value;

    public AnsiParser(TerminalScreen screen) => _screen = screen;

    private enum State
    {
        Ground,
        Escape,
        Csi,
        Osc,
        OscEscape,
        IgnoreOne,
    }

    public void Feed(System.ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            Process(b);
        }
    }

    private void Process(byte b)
    {
        switch (_state)
        {
            case State.Ground: GroundByte(b); break;
            case State.Escape: EscapeByte(b); break;
            case State.Csi: CsiByte(b); break;
            case State.Osc: OscByte(b); break;
            case State.OscEscape: _state = State.Ground; break; // consome o '\' do ST
            case State.IgnoreOne: _state = State.Ground; break; // consome 1 byte (designação de charset)
        }
    }

    // ── Ground: texto + controles + UTF-8 ─────────────────────────────────────
    private void GroundByte(byte b)
    {
        if (_utf8Remaining > 0)
        {
            if ((b & 0xC0) == 0x80)
            {
                _utf8Value = (_utf8Value << 6) | (b & 0x3F);
                if (--_utf8Remaining == 0)
                {
                    EmitCodePoint(_utf8Value);
                }
                return;
            }

            // continuação esperada mas veio outra coisa: descarta e reprocessa o byte
            _utf8Remaining = 0;
        }

        if (b < 0x80)
        {
            HandleControlOrPrintable(b);
            return;
        }

        // byte líder de UTF-8
        if ((b & 0xE0) == 0xC0) { _utf8Value = b & 0x1F; _utf8Remaining = 1; }
        else if ((b & 0xF0) == 0xE0) { _utf8Value = b & 0x0F; _utf8Remaining = 2; }
        else if ((b & 0xF8) == 0xF0) { _utf8Value = b & 0x07; _utf8Remaining = 3; }
        else { _screen.PutChar('?'); } // byte inválido
    }

    private void EmitCodePoint(int cp)
    {
        // MVP: apenas BMP (sem pares substitutos); astral vira '?'. CLI de rede é ASCII/Latin.
        if (cp <= 0xFFFF && cp is < 0xD800 or > 0xDFFF)
        {
            _screen.PutChar((char)cp);
        }
        else
        {
            _screen.PutChar('?');
        }
    }

    private void HandleControlOrPrintable(byte b)
    {
        switch (b)
        {
            case 0x1B: _state = State.Escape; break;           // ESC
            case 0x0D: _screen.CarriageReturn(); break;        // CR
            case 0x0A: case 0x0B: case 0x0C: _screen.LineFeed(); break; // LF/VT/FF
            case 0x08: _screen.Backspace(); break;             // BS
            case 0x09: _screen.Tab(); break;                   // TAB
            case 0x07: break;                                  // BEL — ignora
            default:
                if (b is >= 0x20 and < 0x7F)
                {
                    _screen.PutChar((char)b);
                }
                break; // demais controles: ignora
        }
    }

    // ── Escape ────────────────────────────────────────────────────────────────
    private void EscapeByte(byte b)
    {
        switch ((char)b)
        {
            case '[': ResetCsi(); _state = State.Csi; break;
            case ']': _state = State.Osc; break;
            case '(': case ')': case '*': case '+': _state = State.IgnoreOne; break; // charset
            case 'c': _screen.EraseInDisplay(2); _screen.MoveCursorTo(0, 0); _screen.ResetPen(); _state = State.Ground; break;
            case '7': _savedRow = _screen.CursorRow; _savedCol = _screen.CursorColumn; _state = State.Ground; break;
            case '8': _screen.MoveCursorTo(_savedRow, _savedCol); _state = State.Ground; break;
            case 'M': _screen.MoveCursorBy(-1, 0); _state = State.Ground; break; // reverse index (MVP)
            default: _state = State.Ground; break;
        }
    }

    // ── CSI ─────────────────────────────────────────────────────────────────--
    private void CsiByte(byte b)
    {
        if (b is >= (byte)'0' and <= (byte)'9')
        {
            _paramValue = (_paramValue * 10) + (b - '0');
            _hasParam = true;
            return;
        }

        if (b == (byte)';')
        {
            _params.Add(_hasParam ? _paramValue : 0);
            _paramValue = 0;
            _hasParam = false;
            return;
        }

        if (b is (byte)'?' or (byte)'>' or (byte)'!' or (byte)'=')
        {
            return; // marcador privado DEC (ex.: ?25h) — consumido e ignorado no MVP
        }

        if (b is >= 0x20 and <= 0x2F)
        {
            return; // bytes intermediários — ignora
        }

        if (b is >= 0x40 and <= 0x7E)
        {
            if (_hasParam)
            {
                _params.Add(_paramValue);
            }
            DispatchCsi((char)b);
            _state = State.Ground;
            return;
        }

        _state = State.Ground; // byte inesperado — aborta a sequência
    }

    private void DispatchCsi(char final)
    {
        switch (final)
        {
            case 'A': _screen.MoveCursorBy(-Arg(0, 1), 0); break;
            case 'B': _screen.MoveCursorBy(Arg(0, 1), 0); break;
            case 'C': _screen.MoveCursorBy(0, Arg(0, 1)); break;
            case 'D': _screen.MoveCursorBy(0, -Arg(0, 1)); break;
            case 'E': _screen.MoveCursorTo(_screen.CursorRow + Arg(0, 1), 0); break;
            case 'F': _screen.MoveCursorTo(_screen.CursorRow - Arg(0, 1), 0); break;
            case 'G': _screen.MoveCursorTo(_screen.CursorRow, Arg(0, 1) - 1); break;
            case 'd': _screen.MoveCursorTo(Arg(0, 1) - 1, _screen.CursorColumn); break;
            case 'H': case 'f': _screen.MoveCursorTo(Arg(0, 1) - 1, Arg(1, 1) - 1); break;
            case 'J': _screen.EraseInDisplay(Arg(0, 0)); break;
            case 'K': _screen.EraseInLine(Arg(0, 0)); break;
            case 'm': ApplySgr(); break;
            case 's': _savedRow = _screen.CursorRow; _savedCol = _screen.CursorColumn; break;
            case 'u': _screen.MoveCursorTo(_savedRow, _savedCol); break;
            default: break; // 'h','l','r','P','@','L','X'… ignorados no MVP
        }
    }

    // valor do parâmetro i; ausente ou 0 → default (usado por movimentos, onde 0 == 1)
    private int Arg(int i, int def)
    {
        if (i >= _params.Count)
        {
            return def;
        }
        int v = _params[i];
        return v == 0 ? def : v;
    }

    private void ApplySgr()
    {
        if (_params.Count == 0)
        {
            _screen.ResetPen();
            return;
        }

        for (int i = 0; i < _params.Count; i++)
        {
            int p = _params[i];
            switch (p)
            {
                case 0: _screen.ResetPen(); break;
                case 1: _screen.SetBold(true); break;
                case 22: _screen.SetBold(false); break;
                case 4: _screen.SetUnderline(true); break;
                case 24: _screen.SetUnderline(false); break;
                case 7: _screen.SetInverse(true); break;
                case 27: _screen.SetInverse(false); break;
                case >= 30 and <= 37: _screen.SetForeground(VtPalette.Resolve(p - 30)); break;
                case 39: _screen.SetForeground(TerminalColor.Default); break;
                case >= 40 and <= 47: _screen.SetBackground(VtPalette.Resolve(p - 40)); break;
                case 49: _screen.SetBackground(TerminalColor.Default); break;
                case >= 90 and <= 97: _screen.SetForeground(VtPalette.Resolve(p - 90 + 8)); break;
                case >= 100 and <= 107: _screen.SetBackground(VtPalette.Resolve(p - 100 + 8)); break;
                case 38: i = ApplyExtendedColor(i, foreground: true); break;
                case 48: i = ApplyExtendedColor(i, foreground: false); break;
                default: break; // atributo não suportado — ignora
            }
        }
    }

    // 38/48 ;5;n (indexado) ou ;2;r;g;b (RGB). Devolve o novo índice de iteração.
    private int ApplyExtendedColor(int i, bool foreground)
    {
        if (i + 1 >= _params.Count)
        {
            return i;
        }

        int mode = _params[i + 1];
        if (mode == 5 && i + 2 < _params.Count)
        {
            var color = VtPalette.Resolve(_params[i + 2]);
            Apply(color);
            return i + 2;
        }

        if (mode == 2 && i + 4 < _params.Count)
        {
            var color = TerminalColor.FromRgb((byte)_params[i + 2], (byte)_params[i + 3], (byte)_params[i + 4]);
            Apply(color);
            return i + 4;
        }

        return i;

        void Apply(TerminalColor c)
        {
            if (foreground)
            {
                _screen.SetForeground(c);
            }
            else
            {
                _screen.SetBackground(c);
            }
        }
    }

    // ── OSC (título etc.) — ignorado ──────────────────────────────────────────
    private void OscByte(byte b)
    {
        if (b == 0x07)
        {
            _state = State.Ground; // BEL termina
        }
        else if (b == 0x1B)
        {
            _state = State.OscEscape; // ESC \ (ST)
        }
        // senão: acumula/ignora o conteúdo
    }

    private void ResetCsi()
    {
        _params.Clear();
        _paramValue = 0;
        _hasParam = false;
    }
}
