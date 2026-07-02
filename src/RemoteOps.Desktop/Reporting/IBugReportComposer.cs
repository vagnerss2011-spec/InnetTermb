using System;
using System.Threading.Tasks;

namespace RemoteOps.Desktop.Reporting;

public interface IBugReportComposer
{
    /// <summary>Texto completo do report (o que o operador vê no preview e o que é salvo localmente).</summary>
    string BuildPreview(BugReport report);

    /// <summary>URI mailto: para o suporte (diagnóstico truncado se estourar o limite; descrição intacta).</summary>
    Uri BuildMailtoUri(BugReport report);

    /// <summary>Salva a cópia completa em %AppData%\RemoteOps\bug-reports\; devolve o caminho.</summary>
    Task<string> SaveLocalCopyAsync(BugReport report);
}
