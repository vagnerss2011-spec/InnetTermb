using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RemoteOps.Desktop.Terminal.Vt;
using Size = System.Windows.Size;

namespace RemoteOps.Desktop.Terminal;

/// <summary>
/// View NATIVA da aba de terminal (SSH/Telnet), ligada ao <see cref="TerminalTabViewModel"/>.
/// Renderiza em WPF puro (<see cref="TerminalScreenControl"/>) — sem WebView2, imune ao MPO.
///
/// FOCO/TECLADO: dentro do TabControlEx keep-alive, dar foco a um FrameworkElement "cru" não pegava
/// (o terminal só EXIBIA, não recebia tecla). Aqui o UserControl (um Control de verdade, focável)
/// é o alvo de foco, e o teclado é tratado por <see cref="UIElement.PreviewKeyDown"/>/
/// <see cref="UIElement.PreviewTextInput"/> — que disparam esteja o foco no UserControl ou num filho.
/// O mouse (seleção/cópia/colagem) fica no controle de tela (é hit-test, não depende de foco).
/// </summary>
public partial class NativeTerminalView : UserControl
{
    private TerminalScreen? _screen;
    private AnsiParser? _parser;
    private TerminalTabViewModel? _vm;
    private bool _subscribed;

    public NativeTerminalView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        Surface.GridSizeChanged += OnGridSizeChanged;
        Surface.InputBytes += OnSurfaceInput; // bytes de COLAGEM (mouse/atalho) → sessão
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;
        PreviewMouseDown += (_, _) => FocusTerminal();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as TerminalTabViewModel;
        if (_vm is null)
        {
            return;
        }

        if (!_subscribed)
        {
            _vm.OutputReceived += OnOutput;
            _subscribed = true;
        }

        GridSize grid = Surface.ComputeGrid(new Size(
            Surface.ActualWidth > 0 ? Surface.ActualWidth : ActualWidth,
            Surface.ActualHeight > 0 ? Surface.ActualHeight : ActualHeight));
        if (grid.Columns < 2 || grid.Rows < 2)
        {
            grid = new GridSize(80, 24);
        }

        if (_screen is null)
        {
            _screen = new TerminalScreen(grid.Columns, grid.Rows);
            _parser = new AnsiParser(_screen);
            Surface.Screen = _screen;
            Surface.Redraw();
        }

        FocusTerminal();

        if (!_vm.IsConnected)
        {
            try
            {
                await _vm.ConnectAsync(grid.Columns, grid.Rows);
            }
            catch (Exception ex)
            {
                WriteLocal($"\r\n[Falha ao conectar: {ex.Message}]\r\n");
            }
        }
    }

    // dá foco de teclado ao terminal — via Dispatcher pra rodar depois do layout/seleção de aba
    private void FocusTerminal() =>
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            Focus();
            Keyboard.Focus(this);
        }), DispatcherPriority.Input);

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            FocusTerminal(); // ao voltar pra aba do terminal, recupera o foco
        }
    }

    // pump roda em thread de fundo → marshaliza pro Dispatcher (parser/tela são UI-thread)
    private void OnOutput(ReadOnlyMemory<byte> data)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _parser?.Feed(data.Span);
            Surface.ClearSelection(); // saída nova → não deixa highlight velho em posição errada
            Surface.Redraw();
        }));
    }

    private void OnGridSizeChanged(object? sender, GridSize g)
    {
        if (_screen is null || (g.Columns == _screen.Columns && g.Rows == _screen.Rows))
        {
            return;
        }
        _screen.Resize(g.Columns, g.Rows);
        Surface.Redraw();
        if (_vm is { IsConnected: true })
        {
            _ = _vm.ResizeAsync(g.Columns, g.Rows);
        }
    }

    // ── teclado (previews no UserControl focado) ──────────────────────────────
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        ModifierKeys mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;

        // atalhos de copiar/colar — Ctrl+C SOZINHO continua sendo 0x03 (interromper) pro host
        if (ctrl && shift && e.Key == Key.C) { Surface.CopySelection(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.V) { Surface.Paste(); e.Handled = true; return; }
        if (shift && e.Key == Key.Insert) { Surface.Paste(); e.Handled = true; return; }

        byte[]? bytes = TerminalInputMapper.MapKey(e.Key, mods);
        if (bytes is not null)
        {
            _ = _vm.SendInputAsync(bytes);
            e.Handled = true;
        }
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_vm is null || string.IsNullOrEmpty(e.Text))
        {
            return;
        }
        _ = _vm.SendInputAsync(Encoding.UTF8.GetBytes(e.Text));
        e.Handled = true;
    }

    // bytes de COLAGEM emitidos pelo controle (botão direito / atalho) → sessão remota
    private void OnSurfaceInput(object? sender, byte[] bytes)
    {
        if (_vm is not null)
        {
            _ = _vm.SendInputAsync(bytes);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && _subscribed)
        {
            _vm.OutputReceived -= OnOutput;
            _subscribed = false;
        }
    }

    // escreve texto local (mensagens do app) direto na tela, sem passar pelo host
    private void WriteLocal(string text)
    {
        if (_parser is null)
        {
            return;
        }
        _parser.Feed(Encoding.UTF8.GetBytes(text));
        Surface.Redraw();
    }
}
