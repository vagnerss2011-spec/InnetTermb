using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

// UseWindowsForms está ligado no projeto → System.Drawing entra por ImplicitUsings e conflita.
// Este controle é 100% WPF; fixa os tipos ambíguos na versão do WPF.
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace RemoteOps.Desktop.Terminal.Vt;

/// <summary>
/// Controle WPF que DESENHA um <see cref="TerminalScreen"/> — 100% WPF (DrawingContext/FormattedText),
/// sem HWND, sem swapchain, sem WebView2. Por compor na MESMA árvore visual do resto do app, herda
/// o brilho normal do WPF e é IMUNE ao problema de MPO que escurecia o WebView2. Fonte monoespaçada
/// (Consolas): cada célula ocupa um slot fixo (largura do avanço de 'M').
/// </summary>
public sealed class TerminalScreenControl : FrameworkElement
{
    private readonly Typeface _typeface;
    private readonly Typeface _typefaceBold;
    private readonly GlyphTypeface _metrics;
    private readonly Dictionary<uint, Brush> _brushCache = new();
    private readonly Brush _cursorBrush;

    private double _fontSize = 15;
    private double _cellWidth;
    private double _cellHeight;

    // seleção com o mouse (estilo PuTTY: seleciona esquerdo → copia; direito → cola)
    private bool _selecting;
    private bool _hasSelection;
    private (int Row, int Col) _selStart;
    private (int Row, int Col) _selEnd;
    private readonly Brush _selectionBrush;

    public TerminalScreenControl()
    {
        // Consolas existe em todo Windows e é monoespaçada.
        _typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _typefaceBold = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        if (!_typeface.TryGetGlyphTypeface(out _metrics!))
        {
            throw new InvalidOperationException("Fonte monoespaçada (Consolas) não encontrada.");
        }

        DefaultForeground = Frozen(Color.FromRgb(0xD4, 0xD4, 0xD4));
        DefaultBackground = Frozen(Color.FromRgb(0x1E, 0x1E, 0x1E));
        _cursorBrush = Frozen(Color.FromArgb(0x88, 0xD4, 0xD4, 0xD4));
        _selectionBrush = Frozen(Color.FromArgb(0x66, 0x33, 0x99, 0xFF));

        Focusable = true;
        FocusVisualStyle = null;
        ComputeCellMetrics();
    }

    public TerminalScreen? Screen { get; set; }
    public Brush DefaultForeground { get; set; }
    public Brush DefaultBackground { get; set; }
    public bool CursorVisible { get; set; } = true;

    /// <summary>Disparado quando o tamanho em colunas/linhas muda (ao redimensionar o controle).</summary>
    public event EventHandler<GridSize>? GridSizeChanged;

    /// <summary>Bytes VT gerados pelo teclado (texto UTF-8 ou sequências de teclas especiais).</summary>
    public event EventHandler<byte[]>? InputBytes;

    public double FontSize
    {
        get => _fontSize;
        set { _fontSize = value; ComputeCellMetrics(); InvalidateVisual(); }
    }

    public GridSize ComputeGrid(Size size) => new(
        Math.Max(1, (int)(size.Width / _cellWidth)),
        Math.Max(1, (int)(size.Height / _cellHeight)));

    public void Redraw() => InvalidateVisual();

    private void ComputeCellMetrics()
    {
        ushort gi = _metrics.CharacterToGlyphMap.TryGetValue('M', out var g) ? g : (ushort)0;
        _cellWidth = _metrics.AdvanceWidths[gi] * _fontSize;
        _cellHeight = _metrics.Height * _fontSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? _cellWidth * 80 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? _cellHeight * 24 : availableSize.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        GridSizeChanged?.Invoke(this, ComputeGrid(finalSize));
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(DefaultBackground, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var screen = Screen;
        if (screen is null)
        {
            return;
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int row = 0; row < screen.Rows; row++)
        {
            double y = row * _cellHeight;
            int col = 0;
            while (col < screen.Columns)
            {
                TerminalCell first = screen[row, col];
                int start = col++;
                while (col < screen.Columns && SameStyle(screen[row, col], first))
                {
                    col++;
                }
                DrawRun(dc, screen, row, start, col - start, y, dpi, first);
            }
        }

        DrawSelection(dc, screen);
        DrawCursor(dc, screen);
    }

    private void DrawRun(DrawingContext dc, TerminalScreen screen, int row, int start, int length, double y, double dpi, TerminalCell style)
    {
        Brush fgBrush = style.Foreground.IsDefault ? DefaultForeground : BrushFor(style.Foreground);
        Brush bgBrush = style.Background.IsDefault ? DefaultBackground : BrushFor(style.Background);
        if (style.Inverse)
        {
            (fgBrush, bgBrush) = (bgBrush, fgBrush);
        }

        double x = start * _cellWidth;
        double runWidth = length * _cellWidth;

        if (!ReferenceEquals(bgBrush, DefaultBackground))
        {
            dc.DrawRectangle(bgBrush, null, new Rect(x, y, runWidth, _cellHeight));
        }

        var sb = new StringBuilder(length);
        bool allBlank = true;
        for (int i = 0; i < length; i++)
        {
            char ch = screen[row, start + i].Rune;
            if (ch != ' ')
            {
                allBlank = false;
            }
            sb.Append(ch);
        }

        if (!allBlank)
        {
            var ft = new FormattedText(
                sb.ToString(),
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                style.Bold ? _typefaceBold : _typeface,
                _fontSize,
                fgBrush,
                dpi);
            dc.DrawText(ft, new Point(x, y));
        }

        if (style.Underline)
        {
            double uy = y + (_cellHeight * 0.85);
            dc.DrawRectangle(fgBrush, null, new Rect(x, uy, runWidth, Math.Max(1.0, _fontSize / 14.0)));
        }
    }

    private void DrawCursor(DrawingContext dc, TerminalScreen screen)
    {
        if (!CursorVisible || screen.CursorRow >= screen.Rows || screen.CursorColumn >= screen.Columns)
        {
            return;
        }
        double cx = screen.CursorColumn * _cellWidth;
        double cy = screen.CursorRow * _cellHeight;
        dc.DrawRectangle(_cursorBrush, null, new Rect(cx, cy, _cellWidth, _cellHeight));
    }

    // ── seleção com o mouse: ESQUERDO seleciona e copia ao soltar; DIREITO cola (estilo PuTTY).
    //    O TECLADO é tratado no NativeTerminalView (Preview* no UserControl focado) — mais robusto
    //    dentro do TabControlEx keep-alive. O mouse é hit-test (não depende de foco de teclado). ──
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _selStart = CellAt(e.GetPosition(this));
        _selEnd = _selStart;
        _selecting = true;
        _hasSelection = false;
        CaptureMouse();
        InvalidateVisual();
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_selecting)
        {
            _selEnd = CellAt(e.GetPosition(this));
            _hasSelection = _selEnd != _selStart;
            InvalidateVisual();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_selecting)
        {
            _selecting = false;
            ReleaseMouseCapture();
            if (_hasSelection)
            {
                CopySelection(); // copia ao soltar (estilo PuTTY)
            }
            else
            {
                InvalidateVisual(); // clique simples limpa a seleção anterior
            }
        }
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        Paste();
        e.Handled = true;
        base.OnMouseRightButtonUp(e);
    }

    private (int Row, int Col) CellAt(Point p)
    {
        var screen = Screen;
        int cols = screen?.Columns ?? 1;
        int rows = screen?.Rows ?? 1;
        int col = Math.Clamp((int)(p.X / _cellWidth), 0, Math.Max(0, cols - 1));
        int row = Math.Clamp((int)(p.Y / _cellHeight), 0, Math.Max(0, rows - 1));
        return (row, col);
    }

    public void CopySelection()
    {
        var screen = Screen;
        if (screen is null || !_hasSelection)
        {
            return;
        }
        string text = TerminalSelection.ExtractText(screen, _selStart.Row, _selStart.Col, _selEnd.Row, _selEnd.Col);
        if (!string.IsNullOrEmpty(text))
        {
            try { Clipboard.SetText(text); } catch { /* clipboard ocupado por outro app */ }
        }
        InvalidateVisual();
    }

    public void Paste()
    {
        string text;
        try { text = Clipboard.GetText(); } catch { return; }
        text = TerminalSelection.NormalizePaste(text);
        if (text.Length > 0)
        {
            InputBytes?.Invoke(this, Encoding.UTF8.GetBytes(text));
        }
    }

    /// <summary>Limpa a seleção (a View chama quando chega saída nova, pra não deixar highlight velho).</summary>
    public void ClearSelection()
    {
        if (!_selecting && _hasSelection)
        {
            _hasSelection = false;
            InvalidateVisual();
        }
    }

    private void DrawSelection(DrawingContext dc, TerminalScreen screen)
    {
        if (!_hasSelection)
        {
            return;
        }
        int sr = _selStart.Row, sc = _selStart.Col, er = _selEnd.Row, ec = _selEnd.Col;
        if (sr > er || (sr == er && sc > ec))
        {
            (sr, er) = (er, sr);
            (sc, ec) = (ec, sc);
        }
        for (int row = sr; row <= er && row < screen.Rows; row++)
        {
            int from = row == sr ? sc : 0;
            int to = row == er ? ec : screen.Columns - 1;
            double x = from * _cellWidth;
            double w = (to - from + 1) * _cellWidth;
            dc.DrawRectangle(_selectionBrush, null, new Rect(x, row * _cellHeight, w, _cellHeight));
        }
    }

    private static bool SameStyle(TerminalCell a, TerminalCell b) =>
        a.Foreground == b.Foreground && a.Background == b.Background &&
        a.Bold == b.Bold && a.Underline == b.Underline && a.Inverse == b.Inverse;

    private Brush BrushFor(TerminalColor color)
    {
        uint key = ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (!_brushCache.TryGetValue(key, out var brush))
        {
            brush = Frozen(Color.FromRgb(color.R, color.G, color.B));
            _brushCache[key] = brush;
        }
        return brush;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>Tamanho da grade do terminal em colunas × linhas.</summary>
public readonly record struct GridSize(int Columns, int Rows);
