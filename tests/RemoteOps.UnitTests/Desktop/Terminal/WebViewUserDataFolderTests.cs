using System;
using System.IO;
using RemoteOps.Desktop.Terminal;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Terminal;

/// <summary>
/// Regressão do LOOP de auto-update: o Velopack não conseguia trocar <c>current\</c> porque o
/// WebView2 gravava seus dados DENTRO de <c>current\</c> (default = ao lado do exe) e os processos
/// <c>msedgewebview2.exe</c> travavam os arquivos — o apply falhava, reiniciava a versão antiga e
/// ficava em loop. A pasta de dados do WebView2 precisa ficar FORA do diretório de instalação.
/// </summary>
public sealed class WebViewUserDataFolderTests
{
    [Fact]
    public void IsUnderLocalAppData_AndNotInVelopackCurrentDir()
    {
        string folder = TerminalTabView.WebViewUserDataFolder();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, folder, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("RemoteOps", "WebView2"), folder, StringComparison.OrdinalIgnoreCase);
        // O diretório versionado do Velopack chama-se "current"; a pasta do WebView2 nunca pode
        // cair nele (senão os locks do msedgewebview2.exe voltam a impedir a troca na atualização).
        Assert.DoesNotContain($"{Path.DirectorySeparatorChar}current{Path.DirectorySeparatorChar}", folder, StringComparison.OrdinalIgnoreCase);
    }
}
