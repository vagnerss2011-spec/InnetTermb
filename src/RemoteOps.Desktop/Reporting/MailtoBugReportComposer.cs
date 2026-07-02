using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace RemoteOps.Desktop.Reporting;

/// <summary>Compõe o bug report como e-mail pré-preenchido (mailto:) + cópia local. Sem rede.</summary>
public sealed class MailtoBugReportComposer : IBugReportComposer
{
    private const int MaxMailtoBodyChars = 1500;
    private const string TruncationNote = "\n[diagnóstico truncado — cópia completa salva localmente]";
    private readonly IDiagnosticsProvider _diagnostics;
    private readonly string _bugReportsDir;

    public MailtoBugReportComposer(IDiagnosticsProvider diagnostics, string? bugReportsDir = null)
    {
        _diagnostics = diagnostics;
        _bugReportsDir = bugReportsDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RemoteOps",
            "bug-reports");
    }

    public string BuildPreview(BugReport report) => BuildFullBody(report);

    public Uri BuildMailtoUri(BugReport report)
    {
        string subject = "[RemoteOps] " + report.Title;
        string body = BuildMailtoBody(report);
        string url = $"mailto:{SupportContact.Email}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
        return new Uri(url);
    }

    public async Task<string> SaveLocalCopyAsync(BugReport report)
    {
        Directory.CreateDirectory(_bugReportsDir);
        string fileName = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt";
        string path = Path.Combine(_bugReportsDir, fileName);
        string content = $"Título: {report.Title}\n\n{BuildFullBody(report)}";
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    /// <summary>Corpo completo (descrição + diagnóstico, sem truncar). Preview e cópia local usam isto.</summary>
    private string BuildFullBody(BugReport report)
    {
        var sb = new StringBuilder();
        sb.Append(report.Description);
        if (report.IncludeDiagnostics)
        {
            sb.Append("\n\n--- Diagnósticos ---\n");
            sb.Append(_diagnostics.BuildDiagnostics());
        }

        return sb.ToString();
    }

    /// <summary>Corpo do mailto: descrição nunca truncada; só o diagnóstico é cortado ao orçamento.</summary>
    internal string BuildMailtoBody(BugReport report)
    {
        string head = report.Description;
        if (!report.IncludeDiagnostics)
        {
            return head;
        }

        string diagSection = "\n\n--- Diagnósticos ---\n" + _diagnostics.BuildDiagnostics();
        int budget = MaxMailtoBodyChars - head.Length;
        if (budget <= 0)
        {
            return head;
        }

        if (diagSection.Length > budget)
        {
            return head + diagSection[..budget] + TruncationNote;
        }

        return head + diagSection;
    }
}
