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
        catch (Exception ex)
        {
            _loadingText.Text = $"Erro ao inicializar terminal: {ex.Message}";
        }
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

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;

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
                _ = _vm.ConnectAsync(80, 24);
            }
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
