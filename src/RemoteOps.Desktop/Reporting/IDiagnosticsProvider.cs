namespace RemoteOps.Desktop.Reporting;

public interface IDiagnosticsProvider
{
    /// <summary>Bloco de diagnóstico secret-free (versão, SO, device id, últimas N linhas de log).</summary>
    string BuildDiagnostics();
}
