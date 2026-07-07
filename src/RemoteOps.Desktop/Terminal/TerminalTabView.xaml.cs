using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace RemoteOps.Desktop.Terminal;

/// <summary>
/// Aba de terminal real: WebView2 hospedando xterm.js, ligado ao TerminalTabViewModel.
/// A View pode ser recriada (tab switch) sem matar a sessão — o pump vive no ViewModel.
/// </summary>
public partial class TerminalTabView : UserControl
{
    private TerminalTabViewModel? _vm;
    private bool _webViewReady;
    private bool _sessionStarted;

    public TerminalTabView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // Ao trocar para esta aba (fica visível), devolve o foco do teclado e reajusta o
        // tamanho do terminal — antes o operador precisava clicar dentro pra digitar.
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            ActivateTerminal();
        }
    }

    /// <summary>
    /// Foca o WebView2 (WPF) e manda o xterm reajustar+focar (JS). Chamado quando a aba fica
    /// ativa e logo após a conexão iniciar, para o terminal já abrir pronto pra digitar.
    /// </summary>
    private void ActivateTerminal()
    {
        if (!_webViewReady || _webView.CoreWebView2 is null)
        {
            return;
        }

        _webView.Focus();
        _ = _webView.CoreWebView2.ExecuteScriptAsync("window.__roActivate && window.__roActivate()");
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as TerminalTabViewModel;
        if (_vm != null)
            _ = InitWebViewAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm != null)
            _vm.OutputReceived -= OnOutputReceived;

        // WebView2 CoreWebView2 cleanup
        if (_webView.CoreWebView2 != null)
        {
            _webView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
            _webView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        }

        _webViewReady = false;
        _sessionStarted = false;
    }

    // ── WebView2 init ────────────────────────────────────────────────────────

    private async Task InitWebViewAsync()
    {
        try
        {
            await _webView.EnsureCoreWebView2Async();

            // Guard: tab may have been closed while WebView2 was initializing
            if (!IsLoaded) return;

            ApplySecuritySettings(_webView.CoreWebView2.Settings);

            // Map https://terminal.local/ → Terminal/wwwroot/ in the output directory
            string wwwroot = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Terminal", "wwwroot");

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "terminal.local",
                wwwroot,
                CoreWebView2HostResourceAccessKind.Allow);

            // SEC-003: block navigation away from the virtual host and suppress popups
            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            _webView.Source = new Uri("https://terminal.local/index.html");
        }
        catch (Exception ex) when (IsWebView2RuntimeMissing(ex))
        {
            _loadingText.Text =
                "Componente WebView2 não encontrado nesta máquina. Instale o \"WebView2 Runtime\" da Microsoft " +
                "(developer.microsoft.com/microsoft-edge/webview2) e reabra a aba. Sem ele o terminal não abre.";
        }
        catch (Exception ex)
        {
            _loadingText.Text = $"Erro ao inicializar terminal: {ex.Message}";
        }
    }

    // O WebView2 ausente lança COMException com HRESULT de "arquivo não encontrado"/"classe
    // não registrada" (0x80070002 / 0x80040154), ou uma WebView2RuntimeNotFoundException.
    private static bool IsWebView2RuntimeMissing(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
            {
                return true;
            }

            if (e is System.Runtime.InteropServices.COMException com
                && ((uint)com.HResult == 0x80070002 || (uint)com.HResult == 0x80040154))
            {
                return true;
            }
        }

        return false;
    }

    // ── Security hardening (ADR-011) ─────────────────────────────────────────

    private static void ApplySecuritySettings(CoreWebView2Settings settings)
    {
#if !DEBUG
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
#endif
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = true;
        settings.IsScriptEnabled = true;
        // External navigation is blocked — virtual host serves all content
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
    }

    // SEC-003: only the local virtual host is allowed; cancel any external navigation
    private static void OnNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith("https://terminal.local/", StringComparison.Ordinal))
            e.Cancel = true;
    }

    // SEC-003: suppress window.open() and OSC-8 hyperlink clicks
    private static void OnNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
    }

    private async void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            // Antes: return silencioso deixava "Conectando…" pra sempre.
            _loadingText.Text = $"Erro ao carregar o terminal (WebView2: {e.WebErrorStatus}).";
            return;
        }

        // Confirma que o bundle do xterm EXECUTOU antes de conectar. Sem isto, um bundle
        // não carregado (CSP/cópia truncada) deixava o SSH conectar por trás de uma tela
        // preta, sem eco e sem erro — o clássico "parece só frontend".
        string probe;
        try
        {
            probe = await _webView.CoreWebView2.ExecuteScriptAsync("typeof window.Terminal");
        }
        catch (Exception ex)
        {
            _loadingText.Text = $"Erro ao verificar o terminal: {ex.Message}";
            return;
        }

        if (probe != "\"function\"")
        {
            _loadingText.Text =
                "Falha ao carregar o terminal (biblioteca xterm não encontrada). " +
                "Reinstale o RemoteOps pelo instalador (Setup.exe).";
            _loadingText.Visibility = Visibility.Visible;
            _webView.Visibility = Visibility.Collapsed;
            return;
        }

        _webViewReady = true;
        _webView.Visibility = Visibility.Visible;
        _loadingText.Visibility = Visibility.Collapsed;

        if (_vm != null)
        {
            _vm.OutputReceived += OnOutputReceived;

            if (!_sessionStarted && !_vm.IsConnected)
            {
                _sessionStarted = true;
                // Connect at 80×24; FitAddon sends the real size via onResize immediately after
                _ = ConnectSafeAsync();
            }
        }

        // Terminal pronto e visível → já foca o teclado e reajusta, sem exigir clique.
        ActivateTerminal();
    }

    /// <summary>
    /// Conecta observando a exceção — o fire-and-forget anterior engolia falhas de
    /// conexão (host inacessível, autenticação, DNS) e a aba ficava "Conectando…"
    /// pra sempre. Agora a falha vira mensagem visível na própria aba.
    /// </summary>
    private async Task ConnectSafeAsync()
    {
        if (_vm is null) return;
        try
        {
            await _vm.ConnectAsync(80, 24);
        }
        catch (Exception ex)
        {
            _sessionStarted = false; // permite tentar de novo ao reabrir a aba
            _webView.Visibility = Visibility.Collapsed;
            _loadingText.Text = $"Falha ao conectar: {ex.Message}";
            _loadingText.Visibility = Visibility.Visible;
        }
    }

    // ── Bridge: C# → JS (terminal output) ───────────────────────────────────

    private void OnOutputReceived(ReadOnlyMemory<byte> data)
    {
        if (!_webViewReady) return;

        string b64 = Convert.ToBase64String(data.Span);
        // PostWebMessageAsString is thread-safe from any thread
        _webView.CoreWebView2.PostWebMessageAsString(
            $"{{\"type\":\"output\",\"data\":\"{b64}\"}}");
    }

    // ── Bridge: JS → C# (input / resize) ─────────────────────────────────────

    private void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_vm == null) return;

        try
        {
            string json = e.TryGetWebMessageAsString();
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string? type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "init_error":
                    {
                        // O JS não conseguiu criar o xterm (bundle incompleto/erro de script).
                        _webViewReady = false;
                        _loadingText.Text = root.TryGetProperty("message", out var m)
                            ? m.GetString() ?? "Falha ao iniciar o terminal."
                            : "Falha ao iniciar o terminal.";
                        _loadingText.Visibility = Visibility.Visible;
                        _webView.Visibility = Visibility.Collapsed;
                        break;
                    }
                case "input":
                    {
                        string? b64 = root.GetProperty("data").GetString();
                        if (b64 != null)
                        {
                            byte[] bytes = Convert.FromBase64String(b64);
                            _ = _vm.SendInputAsync(bytes);
                        }
                        break;
                    }
                case "resize":
                    {
                        int cols = root.GetProperty("cols").GetInt32();
                        int rows = root.GetProperty("rows").GetInt32();
                        // SEC-006: reject out-of-range dimensions before forwarding to PTY
                        if (cols is >= 1 and <= 500 && rows is >= 1 and <= 500)
                            _ = _vm.ResizeAsync(cols, rows);
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Terminal] Bridge parse error: {ex.Message}");
        }
    }
}
