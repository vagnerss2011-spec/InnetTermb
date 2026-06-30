using System.Diagnostics;

namespace RemoteOps.MikroTik;

public interface IWinBoxProcessLauncher
{
    Task<string> StartAsync(ProcessStartInfo psi, CancellationToken ct);
}
