using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace RemoteOps.UnitTests.Desktop;

/// <summary>
/// WPF exige afinidade de thread STA para árvore visual/binding engine; xUnit roda em threads
/// de pool (MTA) por padrão. Mantém UMA thread STA dedicada com um <see cref="Application"/>
/// e um <see cref="Dispatcher"/> vivos, e marshala cada <see cref="Run"/> para ela — o mesmo
/// efeito de um <c>[StaFact]</c> sem depender de pacote externo, e com o mesmo tema (sistema
/// de design) carregado que o app real tem em runtime.
///
/// Por que precisa da Application + tema: as Views do Desktop referenciam recursos do tema via
/// <c>StaticResource</c> (ex.: <c>BasedOn="{StaticResource Text.Caption}"</c>), resolvidos no
/// parse do XAML dentro de <c>InitializeComponent()</c>. Sem o dicionário do tema num escopo
/// alcançável (Application.Resources), esse parse lança <c>XamlParseException</c> ("Cannot find
/// resource named 'Text.Caption'") — exatamente como aconteceria em produção se o App não
/// mesclasse o tema. Uma thread única evita o problema de afinidade de thread do WPF (a
/// Application e a árvore visual pertencem sempre à mesma thread).
/// </summary>
internal static class StaThreadRunner
{
    private static readonly Dispatcher Dispatcher = StartStaDispatcher();

    public static Exception? Run(Action action)
    {
        Exception? captured = null;
        Dispatcher.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        return captured;
    }

    private static Dispatcher StartStaDispatcher()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            // Uma Application por AppDomain; nenhuma outra existe no processo de teste.
            // OnExplicitShutdown impede o app de encerrar quando uma janela de teste fecha.
            if (Application.Current is null)
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }

            // Mesmo tema que App.xaml mescla em runtime (docs/06 §Sistema de design). Depois de
            // criar a Application, o esquema pack "application:" está registrado e o pack URI
            // resolve o dicionário compilado no assembly do Desktop.
            Application.Current!.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/RemoteOps.Desktop;component/Themes/DarkTheme.xaml",
                    UriKind.Absolute),
            });

            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "RemoteOps-WpfTestSta",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}
