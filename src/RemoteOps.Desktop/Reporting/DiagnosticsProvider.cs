using System.Linq;
using System.Text;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Reporting;

/// <summary>Monta o diagnóstico anexável ao bug report. Só fontes secret-free.</summary>
public sealed class DiagnosticsProvider : IDiagnosticsProvider
{
    private const int MaxLogLines = 30;
    private readonly LogsViewModel _logs;
    private readonly string _appVersion;
    private readonly string _osDescription;
    private readonly string? _deviceId;

    public DiagnosticsProvider(LogsViewModel logs, string appVersion, string osDescription, string? deviceId)
    {
        _logs = logs;
        _appVersion = appVersion;
        _osDescription = osDescription;
        _deviceId = deviceId;
    }

    public string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"App: RemoteOps Desktop {_appVersion}");
        sb.AppendLine($"SO: {_osDescription}");
        if (!string.IsNullOrWhiteSpace(_deviceId))
        {
            sb.AppendLine($"Device: {_deviceId}");
        }

        sb.AppendLine();
        sb.AppendLine($"Últimas {MaxLogLines} linhas de log:");
        foreach (string line in _logs.Events.Take(MaxLogLines))
        {
            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }
}
