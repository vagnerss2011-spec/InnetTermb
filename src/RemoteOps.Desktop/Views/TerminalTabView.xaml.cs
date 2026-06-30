using System.IO;
using System.Windows.Controls;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

public partial class TerminalTabView : UserControl
{
    private bool _webViewInitialized;

    public TerminalTabView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_webViewInitialized) return;
        _webViewInitialized = true;

        // EnsureCoreWebView2Async must complete before CoreWebView2 is accessible.
        await WebView.EnsureCoreWebView2Async();

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "terminal.local", wwwroot,
            Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

        // Disable DevTools, context menu, and status bar in production.
        WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

        if (DataContext is TerminalTabViewModel vm && vm.Session is { } session)
        {
            session.AttachWebView(WebView.CoreWebView2);
        }

        WebView.CoreWebView2.Navigate("https://terminal.local/terminal.html");
    }
}
