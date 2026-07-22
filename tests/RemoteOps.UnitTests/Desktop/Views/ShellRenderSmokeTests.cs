using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteOps.Desktop;
using RemoteOps.Desktop.Credentials;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.UnitTests.Desktop;
using RemoteOps.UnitTests.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Smoke de renderização REAL (thread STA + tema de produção) das telas que NÃO dependem de
/// controle nativo (WebView2/ActiveX RDP). Renderiza cada Window/UserControl de verdade
/// (Show + UpdateLayout) para pegar a CLASSE de bug que o build não detecta: valor de enum
/// inválido, StaticResource inexistente, binding que lança no layout — só validados em runtime
/// dentro de InitializeComponent()/layout. Foi exatamente esse tipo de bug
/// (ResizeMode="CanResizeWithGrips") que travou o editor de host por 3 releases.
///
/// Cobertura: MainWindow (que aninha BrowserView → HostsView/KeychainView/LogsView), SettingsWindow
/// (todas as abas), CredentialDialog (os 3 modos), NewGroupDialog e TabsView (sem abas). As telas de
/// sessão TerminalTabView (WebView2) e RdpTabView (MSTSCAX) ficam de fora porque hospedam controle
/// nativo que não renderiza de forma confiável em CI — cobertas pela varredura estática.
/// </summary>
public sealed class ShellRenderSmokeTests
{
    private sealed class FakeSettingsStore : ISettingsStore
    {
        private AppSettings _current = new();
        public AppSettings Load() => _current;
        public void Save(AppSettings settings) => _current = settings;
    }

    private static SessionLauncher NewLauncher() =>
        new(new TabsViewModel(), winBox: null, flags: null, ssh: null, telnet: null, rdp: null, rdpCred: null);

    private static WorkspaceViewModel NewWorkspace(InMemoryLocalStore store)
    {
        var hosts = new HostsViewModel(store, NewLauncher(), "ws-local");
        var keychain = new KeychainViewModel(store, new FakeVault(), "ws-local", "ws-local");
        var browser = new BrowserViewModel(hosts, keychain, new LogsViewModel());
        return new WorkspaceViewModel(browser, new TabsViewModel());
    }

    [Fact]
    public void MainWindow_Renders_WithoutThrowing()
    {
        var store = new InMemoryLocalStore();
        var vm = NewWorkspace(store);

        Exception? captured = StaThreadRunner.Run(() =>
        {
            var window = new MainWindow(vm, store, new InlineCredentialService(store, new FakeVault(), "ws-local"))
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(captured is null, captured?.ToString());
    }

    [Fact]
    public void SettingsWindow_AllTabs_Render_WithoutThrowing()
    {
        var vm = new SettingsViewModel(new FakeSettingsStore());

        Exception? captured = StaThreadRunner.Run(() =>
        {
            var window = new SettingsWindow(vm)
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                // WPF só materializa o conteúdo da aba selecionada — percorre todas para
                // forçar o layout de cada template (máxima cobertura da classe de bug).
                if (FindVisualChild<TabControl>(window) is { } tabs)
                {
                    for (int i = 0; i < tabs.Items.Count; i++)
                    {
                        tabs.SelectedIndex = i;
                        window.UpdateLayout();
                    }
                }
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(captured is null, captured?.ToString());
    }

    [Theory]
    [InlineData(CredentialDialogMode.Add)]
    [InlineData(CredentialDialogMode.Edit)]
    [InlineData(CredentialDialogMode.ChangePassword)]
    public void CredentialDialog_Renders_WithoutThrowing(CredentialDialogMode mode)
    {
        var vm = new CredentialDialogViewModel(mode);

        Exception? captured = StaThreadRunner.Run(() =>
        {
            var dialog = new CredentialDialog(vm)
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                dialog.Show();
                dialog.UpdateLayout();
            }
            finally
            {
                dialog.Close();
            }
        });

        Assert.True(captured is null, captured?.ToString());
    }

    [Fact]
    public void NewGroupDialog_Renders_WithoutThrowing()
    {
        var vm = new NewGroupViewModel(new InMemoryLocalStore(), "ws-local", parentGroupId: null);

        Exception? captured = StaThreadRunner.Run(() =>
        {
            var dialog = new NewGroupDialog(vm)
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                dialog.Show();
                dialog.UpdateLayout();
            }
            finally
            {
                dialog.Close();
            }
        });

        Assert.True(captured is null, captured?.ToString());
    }

    [Fact]
    public void TabsView_Empty_Renders_WithoutThrowing()
    {
        var vm = new TabsViewModel();

        Exception? captured = StaThreadRunner.Run(() =>
        {
            var view = new TabsView { DataContext = vm };
            var window = new Window
            {
                Content = view,
                Width = 600,
                Height = 400,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(captured is null, captured?.ToString());
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }
}
