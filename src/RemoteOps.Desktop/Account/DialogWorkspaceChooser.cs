using System.Windows;
using System.Windows.Threading;

using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// <see cref="IWorkspaceChooser"/> de produção: abre a <see cref="WorkspaceChoiceWindow"/> modal.
///
/// <para>Marshala para o <see cref="Dispatcher"/> de propósito. O login roda numa continuação
/// assíncrona que pode não estar na thread da UI, e construir <see cref="Window"/> fora dela é a
/// causa clássica de queda por afinidade de thread neste app.</para>
/// </summary>
public sealed class DialogWorkspaceChooser : IWorkspaceChooser
{
    private readonly Dispatcher _dispatcher;

    public DialogWorkspaceChooser(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public Task<AccountWorkspace?> ChooseAsync(
        IReadOnlyList<AccountWorkspace> workspaces, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspaces);

        AccountWorkspace? chosen = _dispatcher.Invoke(() =>
        {
            var viewModel = new WorkspaceChoiceViewModel(workspaces);
            var window = new WorkspaceChoiceWindow(viewModel);
            window.ShowDialog();

            // Quem confirmou preenche o Chosen; fechar no X deixa nulo. Desistir NÃO pode virar
            // "abre o primeiro" — abrir o cofre errado em silêncio é o que esta tela veio impedir.
            return viewModel.Chosen;
        });

        return Task.FromResult(chosen);
    }
}
