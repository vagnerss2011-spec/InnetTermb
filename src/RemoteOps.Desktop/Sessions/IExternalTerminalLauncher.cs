using System.Threading;
using System.Threading.Tasks;

namespace RemoteOps.Desktop.Sessions;

/// <summary>Alvo de uma sessão SSH aberta num terminal externo do Windows.</summary>
public sealed record SshLaunchTarget(string Host, int Port, string? Username);

/// <summary>
/// Abre uma sessão de terminal (SSH) numa janela de terminal REAL do Windows, por fora do
/// app — substitui o terminal WebView2, que em algumas GPUs (Win11 + NVIDIA / MPO)
/// renderizava escuro, não aceitava teclado e não maximizava. Espelha o padrão do WinBox: o
/// app resolve host/porta/usuário e delega o "desenhar" pro sistema operacional.
/// </summary>
public interface IExternalTerminalLauncher
{
    Task LaunchSshAsync(SshLaunchTarget target, CancellationToken ct = default);
}
