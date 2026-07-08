using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteOps.Desktop.Terminal.Vt;
using Size = System.Windows.Size;

namespace RemoteOps.Desktop.Terminal;

/// <summary>
/// View NATIVA da aba de terminal (SSH/Telnet), ligada ao <see cref="TerminalTabViewModel"/> —
/// o MESMO ViewModel que a antiga aba WebView2 usava. Só troca o "desenhar": consome os bytes de
/// saída via <see cref="TerminalTabViewModel.OutputReceived"/> → <see cref="AnsiParser"/> →
/// <see cref="TerminalScreen"/> → <see cref="TerminalScreenControl"/> (WPF puro). Teclado vira
/// bytes VT/UTF-8 e volta pelo <see cref="TerminalTabViewModel.SendInputAsync"/>. Sem WebView2,
/// sem HWND, sem MPO — renderiza claro por construção.
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
        Surface.GridSizeChanged += OnGridSizeChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        TextInput += OnTextInput;
        PreviewMouseDown += (_, _) => Surface.Focus();
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

        Surface.Focus();

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

    // pump roda em thread de fundo → marshaliza pro Dispatcher (parser/tela são UI-thread)
    private void OnOutput(ReadOnlyMemory<byte> data)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _parser?.Feed(data.Span);
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }
        byte[]? bytes = TerminalInputMapper.MapKey(e.Key, Keyboard.Modifiers);
        if (bytes is not null)
        {
            _ = _vm.SendInputAsync(bytes);
            e.Handled = true;
        }
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_vm is null || string.IsNullOrEmpty(e.Text))
        {
            return;
        }
        _ = _vm.SendInputAsync(Encoding.UTF8.GetBytes(e.Text));
        e.Handled = true;
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
