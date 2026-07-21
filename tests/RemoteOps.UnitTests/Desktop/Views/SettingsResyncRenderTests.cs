using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.UnitTests.Desktop;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Renderização REAL (thread STA + tema de produção) da seção "Reenviar tudo para a nuvem" em
/// Configurações → Conta.
///
/// <para>Afirma VISIBILIDADE e TEXTO, nunca "não lançou": binding quebrado no WPF NÃO lança — cai no
/// valor padrão, e o padrão de <see cref="UIElement.Visibility"/> é <see cref="Visibility.Visible"/>.
/// Um teste de "não estourou" passaria com o botão invisível ou com a confirmação sempre na tela.
/// Chave de <c>DynamicResource</c> inexistente é a outra armadilha que só aparece aqui (já mordeu com
/// <c>Brush.Accent</c> e <c>Brush.Status.Warning</c>).</para>
/// </summary>
public sealed class SettingsResyncRenderTests
{
    private sealed class FakeSettingsStore : ISettingsStore
    {
        private AppSettings _current = new();

        public AppSettings Load() => _current;

        public void Save(AppSettings settings) => _current = settings;
    }

    private sealed class FakeSyncController : ISyncController
    {
        public TaskCompletionSource? Gate { get; init; }

        public async Task SyncNowAsync(CancellationToken ct = default)
        {
            if (Gate is not null)
            {
                await Gate.Task;
            }
        }

        public Task<IReadOnlyList<SyncConflictItem>> GetConflictsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SyncConflictItem>>([]);

        public Task DismissConflictsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static SettingsViewModel BuildVm(bool withCloud = true, TaskCompletionSource? gate = null)
    {
        ISyncController? sync = withCloud ? new FakeSyncController { Gate = gate } : null;
        var resync = new CloudResyncService(new InMemoryLocalStore(), "ws-local", sync);
        return new SettingsViewModel(new FakeSettingsStore(), resync: resync);
    }

    private sealed record Probe(Visibility Visibility, string Text, bool Enabled);

    /// <summary>
    /// Abre as Configurações JÁ na aba Conta (o TabControl só realiza a aba selecionada) e devolve o
    /// estado real dos elementos nomeados da seção de reenvio.
    /// </summary>
    /// <param name="beforeRender">
    /// Roda NA THREAD STA, antes de a janela existir. É onde uma operação assíncrona da VM deve ser
    /// disparada: começando na UI thread ela captura o contexto de sincronização do Dispatcher, e as
    /// continuações voltam pra cá — exatamente como em produção. Disparada da thread do xUnit, a
    /// continuação cairia no pool e tocaria os controles de outra thread.
    /// </param>
    private static (Exception? Error, Dictionary<string, Probe> Probes) RenderConta(
        SettingsViewModel vm, Action? beforeRender = null)
    {
        var probes = new Dictionary<string, Probe>(StringComparer.Ordinal);

        Exception? error = StaThreadRunner.Run(() =>
        {
            beforeRender?.Invoke();

            var window = new SettingsWindow(vm, initialTab: "Conta")
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                foreach (string name in new[]
                    { "ResyncSection", "ResyncButton", "ResyncConfirmPanel", "ResyncStatusText" })
                {
                    var element = (FrameworkElement?)window.FindName(name);
                    Assert.True(element is not null, $"elemento '{name}' sumiu do XAML");
                    probes[name] = new Probe(
                        element!.Visibility,
                        element is TextBlock tb ? tb.Text : string.Concat(FindTexts(element)),
                        element.IsEnabled);
                }
            }
            finally
            {
                window.Close();
            }
        });

        return (error, probes);
    }

    private static IEnumerable<string> FindTexts(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb)
            {
                yield return tb.Text + " ";
            }

            foreach (string nested in FindTexts(child))
            {
                yield return nested;
            }
        }
    }

    [Fact]
    public void Idle_With_Cloud_Shows_The_Button_And_Nothing_Else()
    {
        var (error, probes) = RenderConta(BuildVm());

        Assert.Null(error);
        Assert.Equal(Visibility.Visible, probes["ResyncSection"].Visibility);
        Assert.Equal(Visibility.Visible, probes["ResyncButton"].Visibility);
        Assert.True(probes["ResyncButton"].Enabled);

        // Ocioso: nem confirmação, nem status. Um "Visible" aqui é o bug clássico do binding quebrado.
        Assert.NotEqual(Visibility.Visible, probes["ResyncConfirmPanel"].Visibility);
        Assert.NotEqual(Visibility.Visible, probes["ResyncStatusText"].Visibility);
    }

    [Fact]
    public void Without_Cloud_The_Whole_Section_Is_Hidden()
    {
        var (error, probes) = RenderConta(BuildVm(withCloud: false));

        Assert.Null(error);
        Assert.NotEqual(Visibility.Visible, probes["ResyncSection"].Visibility);
    }

    /// <summary>
    /// A confirmação tem que EXPLICAR — o operador precisa ler que nada é alterado nem apagado antes
    /// de mandar o acervo inteiro subir de novo.
    /// </summary>
    [Fact]
    public void Confirmation_Is_Really_On_Screen_And_Explains_Itself()
    {
        SettingsViewModel vm = BuildVm();
        vm.ResyncCommand.Execute(null);
        Assert.True(vm.IsResyncConfirmVisible); // pré-condição na VM

        var (error, probes) = RenderConta(vm);

        Assert.Null(error);
        Assert.Equal(Visibility.Visible, probes["ResyncConfirmPanel"].Visibility);
        Assert.Contains("Nada é alterado", probes["ResyncConfirmPanel"].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task While_Running_The_Progress_Text_Is_Really_Visible()
    {
        var gate = new TaskCompletionSource();
        SettingsViewModel vm = BuildVm(gate: gate);
        Task? running = null;

        // O reenvio começa na thread STA (como no app: o clique vem da UI thread) e fica preso no
        // gate durante toda a inspeção — o estado "em progresso" é determinístico, sem sleep.
        var (error, probes) = RenderConta(vm, () =>
        {
            vm.ResyncCommand.Execute(null);
            running = vm.ResyncNowAsync();
        });

        Assert.True(vm.IsResyncing); // pré-condição na VM
        Assert.Null(error);
        Assert.Equal(Visibility.Visible, probes["ResyncStatusText"].Visibility);
        Assert.Contains("Reenviando", probes["ResyncStatusText"].Text, StringComparison.Ordinal);
        Assert.False(probes["ResyncButton"].Enabled); // sem disparar um segundo reenvio por cima
        Assert.NotEqual(Visibility.Visible, probes["ResyncConfirmPanel"].Visibility);

        gate.SetResult();
        await running!;
        Assert.False(vm.IsResyncing);
    }
}
