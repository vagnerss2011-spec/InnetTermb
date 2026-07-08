using System;

namespace RemoteOps.Desktop.Terminal.Vt;

/// <summary>
/// Buffer de tela do terminal: grade de células (linhas × colunas), posição do cursor e a
/// "caneta" atual (cor/atributos aplicados aos próximos caracteres). NÃO faz parse — recebe
/// operações do <see cref="AnsiParser"/>. Índices de linha/coluna são 0-based internamente
/// (o CSI do VT é 1-based; o parser converte). Feito para ser desenhado por um controle WPF.
/// </summary>
public sealed class TerminalScreen
{
    private TerminalCell[] _cells = Array.Empty<TerminalCell>();

    // caneta atual (aplicada em PutChar)
    private TerminalColor _fg = TerminalColor.Default;
    private TerminalColor _bg = TerminalColor.Default;
    private bool _bold;
    private bool _underline;
    private bool _inverse;

    public TerminalScreen(int columns, int rows) => Resize(columns, rows);

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorRow { get; private set; }
    public int CursorColumn { get; private set; }

    /// <summary>Lê uma célula (para renderização). Fora do intervalo devolve uma célula vazia.</summary>
    public TerminalCell this[int row, int col]
    {
        get
        {
            if ((uint)row >= (uint)Rows || (uint)col >= (uint)Columns)
            {
                return TerminalCell.Blank;
            }
            return _cells[(row * Columns) + col];
        }
    }

    // ── dimensionamento ──────────────────────────────────────────────────────
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        var next = new TerminalCell[columns * rows];
        for (int i = 0; i < next.Length; i++)
        {
            next[i] = TerminalCell.Blank;
        }

        // preserva o canto superior-esquerdo que couber
        int copyRows = Math.Min(rows, Rows);
        int copyCols = Math.Min(columns, Columns);
        for (int r = 0; r < copyRows; r++)
        {
            for (int c = 0; c < copyCols; c++)
            {
                next[(r * columns) + c] = _cells[(r * Columns) + c];
            }
        }

        _cells = next;
        Columns = columns;
        Rows = rows;
        CursorRow = Math.Min(CursorRow, rows - 1);
        CursorColumn = Math.Min(CursorColumn, columns - 1);
    }

    // ── caneta (SGR) ─────────────────────────────────────────────────────────
    public void ResetPen()
    {
        _fg = TerminalColor.Default;
        _bg = TerminalColor.Default;
        _bold = _underline = _inverse = false;
    }

    public void SetBold(bool on) => _bold = on;
    public void SetUnderline(bool on) => _underline = on;
    public void SetInverse(bool on) => _inverse = on;
    public void SetForeground(TerminalColor color) => _fg = color;
    public void SetBackground(TerminalColor color) => _bg = color;

    // ── escrita / movimento ──────────────────────────────────────────────────
    public void PutChar(char c)
    {
        if (CursorColumn >= Columns)
        {
            CursorColumn = 0;
            LineFeed();
        }

        _cells[(CursorRow * Columns) + CursorColumn] = new TerminalCell
        {
            Rune = c,
            Foreground = _fg,
            Background = _bg,
            Bold = _bold,
            Underline = _underline,
            Inverse = _inverse,
        };
        CursorColumn++;
    }

    public void CarriageReturn() => CursorColumn = 0;

    public void LineFeed()
    {
        if (CursorRow >= Rows - 1)
        {
            ScrollUp();
        }
        else
        {
            CursorRow++;
        }
    }

    public void Backspace()
    {
        if (CursorColumn > 0)
        {
            CursorColumn--;
        }
    }

    public void Tab()
    {
        int next = ((CursorColumn / 8) + 1) * 8;
        CursorColumn = Math.Min(next, Columns - 1);
    }

    /// <summary>Move o cursor para uma posição absoluta 0-based (com clamp).</summary>
    public void MoveCursorTo(int row, int col)
    {
        CursorRow = Math.Clamp(row, 0, Rows - 1);
        CursorColumn = Math.Clamp(col, 0, Columns - 1);
    }

    public void MoveCursorBy(int dRow, int dCol) =>
        MoveCursorTo(CursorRow + dRow, CursorColumn + dCol);

    // ── apagar ────────────────────────────────────────────────────────────────
    /// <summary>EL: 0 = cursor→fim da linha; 1 = início→cursor; 2 = linha toda.</summary>
    public void EraseInLine(int mode)
    {
        int rowStart = CursorRow * Columns;
        int from = mode == 1 ? 0 : (mode == 2 ? 0 : CursorColumn);
        int to = mode == 1 ? CursorColumn : Columns - 1;
        for (int c = from; c <= to && c < Columns; c++)
        {
            _cells[rowStart + c] = TerminalCell.Blank;
        }
    }

    /// <summary>ED: 0 = cursor→fim da tela; 1 = início→cursor; 2 = tela toda.</summary>
    public void EraseInDisplay(int mode)
    {
        if (mode == 2)
        {
            for (int i = 0; i < _cells.Length; i++)
            {
                _cells[i] = TerminalCell.Blank;
            }
            return;
        }

        int cursorIndex = (CursorRow * Columns) + CursorColumn;
        int from = mode == 1 ? 0 : cursorIndex;
        int to = mode == 1 ? cursorIndex : _cells.Length - 1;
        for (int i = from; i <= to && i < _cells.Length; i++)
        {
            _cells[i] = TerminalCell.Blank;
        }
    }

    // move todas as linhas uma para cima; a última fica em branco
    private void ScrollUp()
    {
        Array.Copy(_cells, Columns, _cells, 0, (Rows - 1) * Columns);
        int last = (Rows - 1) * Columns;
        for (int c = 0; c < Columns; c++)
        {
            _cells[last + c] = TerminalCell.Blank;
        }
    }
}
