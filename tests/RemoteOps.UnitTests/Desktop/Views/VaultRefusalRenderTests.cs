using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Credentials;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.Security.Vault;
using RemoteOps.UnitTests.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// A recusa do cofre <b>desenhada</b> — no Keychain e no editor de host.
///
/// <para>Uma propriedade <c>ErrorMessage</c> que nenhum XAML lê é a mesma falha muda por outro
/// caminho: o VM "trata" o erro e a tela continua sem dizer nada. Estes testes afirmam
/// <b>visibilidade efetiva</b> (o elemento e todos os ancestrais) e o <b>texto</b>, comparado com a
/// propriedade da VM. Elemento ausente do XAML conta como invisível.</para>
/// </summary>
public sealed class VaultRefusalRenderTests
{
    private const string RecusaDoCofre =
        "O cofre do time 'time:W' ainda não tem a chave neste computador. Aceite o convite antes de "
        + "cadastrar ou abrir senhas do time.";

    private sealed class FailClosedVault : IVault
    {
        public Task<SecretEnvelope> StoreAsync(
            VaultStoreRequest r, ReadOnlyMemory<char> secret, CancellationToken ct = default)
            => throw new VaultException(RecusaDoCofre);

        public Task<VaultSecret> RetrieveAsync(
            string envelopeId, VaultAccessContext c, CancellationToken ct = default)
            => throw new VaultException(RecusaDoCofre);

        public Task<SecretEnvelope> RotateAsync(
            string envelopeId, ReadOnlyMemory<char> s, VaultAccessContext c, CancellationToken ct = default)
            => throw new VaultException(RecusaDoCofre);

        public Task RevokeAsync(
            string envelopeId, VaultAccessContext c, CancellationToken ct = default)
            => throw new VaultException(RecusaDoCofre);
    }

    private sealed class FailClosedInlineCredentials : IInlineCredentialService
    {
        public Task<string> CreateForEndpointAsync(
            string endpointId, string username, char[] password, CancellationToken ct = default)
        {
            Array.Clear(password);
            throw new VaultException(RecusaDoCofre);
        }

        public Task DeleteForEndpointAsync(Endpoint endpoint, CancellationToken ct = default)
            => Task.CompletedTask;

        public bool IsInlineScope(string? scope) => false;
    }

    private static bool IsEffectivelyVisible(FrameworkElement? element)
    {
        if (element is null)
        {
            return false;
        }

        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return true;
    }

    /// <summary>
    /// Keychain: com o cofre recusando, o painel de erro APARECE e mostra a frase da VM inteira —
    /// que é a que diz o que fazer.
    /// </summary>
    [Fact]
    public async Task Keychain_RecusaDoCofre_APARECE_NaTela()
    {
        var vm = new KeychainViewModel(
            new InMemoryLocalStore(), new FailClosedVault(), "ws-local", "time:W");
        await vm.CreateAsync("Cliente ACME", "admin", "segredo".ToCharArray());
        Assert.True(vm.HasError);

        bool visivel = false;
        string texto = string.Empty;

        Exception? error = StaThreadRunner.Run(() =>
        {
            var view = new KeychainView { DataContext = vm };
            var window = new Window
            {
                Content = view,
                Width = 900,
                Height = 600,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                var banner = view.FindName("VaultRefusalText") as TextBlock;
                visivel = IsEffectivelyVisible(banner);
                texto = banner?.Text ?? string.Empty;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(error is null, error?.ToString());
        Assert.True(visivel);
        Assert.Equal(vm.ErrorMessage, texto);
        Assert.Contains("Aceite o convite", texto, StringComparison.Ordinal);
    }

    /// <summary>
    /// A metade que denuncia binding quebrado: sem erro, COLLAPSED. O padrão de <c>Visibility</c> é
    /// <c>Visible</c>, então um binding que não resolve deixaria um painel de erro vazio aceso para
    /// sempre no Keychain.
    /// </summary>
    [Fact]
    public void Keychain_SemErro_NaoDESENHA_OPainel()
    {
        var vm = new KeychainViewModel(
            new InMemoryLocalStore(), new FakeVault(), "ws-local", "ws-local");

        bool visivel = true;
        Exception? error = StaThreadRunner.Run(() =>
        {
            var view = new KeychainView { DataContext = vm };
            var window = new Window
            {
                Content = view,
                Width = 900,
                Height = 600,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                visivel = IsEffectivelyVisible(view.FindName("VaultRefusalText") as TextBlock);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(error is null, error?.ToString());
        Assert.False(visivel);
    }

    /// <summary>
    /// Editor de host: o diálogo continua aberto E explica por quê. Era aqui que o clique em
    /// "Salvar" simplesmente não fazia nada.
    /// </summary>
    [Fact]
    public async Task EditorDeHost_RecusaDoCofre_APARECE_NoDialogo()
    {
        var store = new InMemoryLocalStore();
        var vm = new HostEditorViewModel(
            store, "ws-local", existing: null, groupId: null, new FailClosedInlineCredentials());

        vm.Name = "Cliente ACME";
        vm.UseInlineCredential = true;
        vm.NewEndpointProtocol = "ssh";
        vm.NewEndpointAddress = "10.0.0.1";
        vm.NewEndpointPort = 22;
        vm.NewEndpointInlineUsername = "admin";
        vm.AddInlineEndpoint("segredo".ToCharArray());
        await vm.SaveAsync();
        Assert.True(vm.HasError);

        bool visivel = false;
        string texto = string.Empty;

        Exception? error = StaThreadRunner.Run(() =>
        {
            var dialog = new HostEditorDialog(vm)
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
            };
            try
            {
                dialog.Show();
                dialog.UpdateLayout();

                var banner = dialog.FindName("VaultRefusalText") as TextBlock;
                visivel = IsEffectivelyVisible(banner);
                texto = banner?.Text ?? string.Empty;
            }
            finally
            {
                dialog.Close();
            }
        });

        Assert.True(error is null, error?.ToString());
        Assert.True(visivel);
        Assert.Equal(vm.ErrorMessage, texto);
        Assert.Contains("Aceite o convite", texto, StringComparison.Ordinal);
    }

    /// <summary>Sem erro, o diálogo não desenha painel nenhum — a metade anti-binding-quebrado.</summary>
    [Fact]
    public void EditorDeHost_SemErro_NaoDESENHA_OPainel()
    {
        var vm = new HostEditorViewModel(
            new InMemoryLocalStore(), "ws-local", existing: null, groupId: null);

        bool visivel = true;
        Exception? error = StaThreadRunner.Run(() =>
        {
            var dialog = new HostEditorDialog(vm)
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
            };
            try
            {
                dialog.Show();
                dialog.UpdateLayout();
                visivel = IsEffectivelyVisible(dialog.FindName("VaultRefusalText") as TextBlock);
            }
            finally
            {
                dialog.Close();
            }
        });

        Assert.True(error is null, error?.ToString());
        Assert.False(visivel);
    }
}
