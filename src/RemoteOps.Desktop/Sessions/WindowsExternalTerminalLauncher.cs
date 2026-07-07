using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteOps.Desktop.Sessions;

/// <summary>
/// Implementação Windows de <see cref="IExternalTerminalLauncher"/>: abre o <c>ssh.exe</c> do
/// OpenSSH na própria janela de terminal do Windows (<see cref="ProcessStartInfo.UseShellExecute"/>
/// = true). É uma janela nativa do SO — legível, digitável e maximizável — ao contrário do
/// WebView2. A senha é digitada pelo usuário no prompt do ssh (o cofre continua guardando a
/// credencial; o auto-preenchimento via SSH_ASKPASS vem numa etapa seguinte).
/// </summary>
public sealed class WindowsExternalTerminalLauncher : IExternalTerminalLauncher
{
    private readonly Func<ProcessStartInfo, Process?> _start;

    // O seam de Process.Start é injetável só para teste; em produção usa o real.
    public WindowsExternalTerminalLauncher(Func<ProcessStartInfo, Process?>? start = null)
        => _start = start ?? (psi => Process.Start(psi));

    /// <summary>
    /// Monta os argumentos do ssh.exe: <c>-o StrictHostKeyChecking=accept-new -p PORT [user@]host</c>.
    /// <c>accept-new</c> aceita a chave de host na PRIMEIRA conexão sem o prompt "yes/no" (deixa de
    /// "pedir pra registrar" a cada host novo), mas ainda BLOQUEIA uma chave TROCADA (proteção MITM) —
    /// o ssh continua gravando em known_hosts, então hosts já conhecidos nem perguntam.
    /// </summary>
    public static string BuildSshArguments(SshLaunchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string destination = string.IsNullOrWhiteSpace(target.Username)
            ? target.Host
            : $"{target.Username}@{target.Host}";
        return $"-o StrictHostKeyChecking=accept-new -p {target.Port} {destination}";
    }

    public Task LaunchSshAsync(SshLaunchTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var psi = new ProcessStartInfo
        {
            FileName = "ssh.exe",
            Arguments = BuildSshArguments(target),
            // console app + UseShellExecute=true => o SO abre a janela de terminal padrão
            // (Windows Terminal / conhost) rodando o ssh. Sem isso, não haveria janela.
            UseShellExecute = true,
        };
        _start(psi);
        return Task.CompletedTask;
    }
}
