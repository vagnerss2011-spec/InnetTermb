using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AxMSTSCLib;

namespace RemoteOps.Desktop.Rdp;

/// <summary>
/// Aba RDP real: WindowsFormsHost hospedando o controle ActiveX MSTSCAX (mstscax.dll),
/// ligado ao RdpTabViewModel. Camada fina não testável em headless — coberta por
/// verificação manual (spike) documentada no PR/docs/08-rdp-terminal-server.md.
/// </summary>
public partial class RdpTabView : UserControl
{
    private RdpTabViewModel? _vm;
    private AxMsRdpClient9NotSafeForScripting? _client;
    private bool _connectStarted;

    public RdpTabView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as RdpTabViewModel;
        if (_vm != null && !_connectStarted)
        {
            _connectStarted = true;
            _ = InitAndConnectAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_client != null)
        {
            _client.OnConnected -= OnAxConnected;
            _client.OnDisconnected -= OnAxDisconnected;
            try
            {
                if (_client.Connected == 1) _client.Disconnect();
            }
            catch
            {
                // Controle pode já ter sido finalizado pelo runtime COM — ignorar no shutdown.
            }
        }

        // Descarta EXPLICITAMENTE o controle ActiveX (mstscax.dll) e o WindowsFormsHost. O WPF NÃO
        // descarta HwndHost/WindowsFormsHost ao remover da árvore visual — sem este Dispose os
        // handles nativos (HWND/GDI) e os objetos COM da sessão RDP vazam a cada abrir/fechar de aba,
        // dependendo só do finalizador do AxHost (não-determinístico e problemático em STA).
        try
        {
            _formsHost.Child = null;
            _client?.Dispose();
            _formsHost.Dispose();
        }
        catch
        {
            // Teardown COM/STA é best-effort — não deixa uma falha de descarte derrubar o fechamento.
        }

        _client = null;
        _connectStarted = false;
    }

    // ── MSTSCAX init + connect ───────────────────────────────────────────────

    private async Task InitAndConnectAsync()
    {
        try
        {
            var config = await _vm!.PrepareAsync();

            // Guard: a aba pode ter sido fechada enquanto PrepareAsync estava em
            // andamento (já abriu/auditou a sessão no provider — feche-a corretamente
            // em vez de abandonar o handle).
            if (!IsLoaded)
            {
                await _vm.CloseAsync();
                return;
            }

            _client = new AxMsRdpClient9NotSafeForScripting();
            ((ISupportInitialize)_client).BeginInit();
            _formsHost.Child = _client;
            ((ISupportInitialize)_client).EndInit();

            _client.Server = config.Host;
            _client.UserName = config.Username;

            var advancedSettings = _client.AdvancedSettings9;
            advancedSettings.RDPPort = config.Port;

            // NLA + nível de autenticação — nunca ignorar certificado sem auditoria (ADR-014).
            advancedSettings.EnableCredSspSupport = config.NlaRequired;
            advancedSettings.AuthenticationLevel = 2; // exige autenticação de servidor (não suprime aviso/prompt de certificado)

            // Redirecionamentos — espelham 1:1 a política resolvida; nunca hardcoded "on".
            advancedSettings.RedirectClipboard = config.Redirection.ClipboardRedirectionEnabled;
            advancedSettings.RedirectDrives = config.Redirection.DriveRedirectionEnabled;
            advancedSettings.RedirectPrinters = config.Redirection.PrinterRedirectionEnabled;
            advancedSettings.AudioRedirectionMode =
                config.Redirection.AudioRedirectionEnabled ? 0u /* redirecionar/tocar localmente */ : 2u /* não tocar */;

            // Senha: resolvida do vault só agora, aplicada e imediatamente fora de escopo
            // (mitigação ADR-009 — a senha nunca fica retida em campo desta View/ViewModel).
            string? password = await _vm.ResolvePasswordAsync();

            // Guard de novo: a aba pode ter sido fechada durante a resolução da senha
            // (chamada ao vault). Mesma lógica — fecha a sessão corretamente.
            if (!IsLoaded)
            {
                await _vm.CloseAsync();
                return;
            }

            if (password != null)
            {
                advancedSettings.ClearTextPassword = password;
            }

            _client.OnConnected += OnAxConnected;
            _client.OnDisconnected += OnAxDisconnected;

            _formsHost.Visibility = Visibility.Visible;
            _statusText.Visibility = Visibility.Collapsed;

            _client.Connect();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Erro ao conectar RDP: {ex.Message}";
            _vm?.MarkDisconnected(ex.Message);
        }
    }

    // ── MSTSCAX events ───────────────────────────────────────────────────────

    private void OnAxConnected(object? sender, EventArgs e) => _vm?.MarkConnected();

    private void OnAxDisconnected(object? sender, IMsTscAxEvents_OnDisconnectedEvent e) =>
        _vm?.MarkDisconnected($"disconnect reason code {e.discReason}");
}
