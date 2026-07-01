using System.Threading;

namespace RemoteOps.UnitTests.Desktop;

/// <summary>
/// WPF exige afinidade de thread STA para árvore visual/binding engine; xUnit roda em threads
/// de pool (MTA) por padrão. Roda <paramref name="action"/> numa thread STA dedicada e devolve
/// qualquer exceção capturada para a thread de teste relançar/verificar — o mesmo efeito de um
/// <c>[StaFact]</c> sem depender de pacote externo. Compartilhado entre os testes de
/// renderização WPF (Desktop e Desktop.NDesk) para evitar reimplementar isso por arquivo.
/// </summary>
internal static class StaThreadRunner
{
    public static Exception? Run(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
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
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return captured;
    }
}
