using System;
using System.IO;
using System.Threading.Tasks;
using RemoteOps.Desktop.Reporting;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Reporting;

public sealed class MailtoBugReportComposerTests
{
    private sealed class FakeDiagnostics : IDiagnosticsProvider
    {
        private readonly string _text;
        public FakeDiagnostics(string text) => _text = text;
        public string BuildDiagnostics() => _text;
    }

    [Fact]
    public void BuildPreview_IncludesDescription_AndDiagnosticsWhenOptedIn()
    {
        var c = new MailtoBugReportComposer(new FakeDiagnostics("DIAG-BLOCK"));
        string withDiag = c.BuildPreview(new BugReport("t", "minha descrição", IncludeDiagnostics: true));
        string without = c.BuildPreview(new BugReport("t", "minha descrição", IncludeDiagnostics: false));
        Assert.Contains("minha descrição", withDiag);
        Assert.Contains("DIAG-BLOCK", withDiag);
        Assert.DoesNotContain("DIAG-BLOCK", without);
    }

    [Fact]
    public void BuildMailtoUri_IsMailto_ToSupport_WithEncodedSubject()
    {
        var c = new MailtoBugReportComposer(new FakeDiagnostics(""));
        Uri uri = c.BuildMailtoUri(new BugReport("Falha no WinBox", "x", IncludeDiagnostics: false));
        // AbsoluteUri preserva o percent-encoding; ToString() devolve a forma decodificada.
        Assert.StartsWith("mailto:" + SupportContact.Email, uri.AbsoluteUri);
        Assert.Contains(Uri.EscapeDataString("[RemoteOps] Falha no WinBox"), uri.AbsoluteUri);
    }

    [Fact]
    public void BuildMailtoBody_TruncatesDiagnostics_ButKeepsDescription()
    {
        var c = new MailtoBugReportComposer(new FakeDiagnostics(new string('D', 5000)));
        string body = c.BuildMailtoBody(new BugReport("t", "DESCRICAO-INTACTA", IncludeDiagnostics: true));
        Assert.Contains("DESCRICAO-INTACTA", body);
        Assert.Contains("diagnóstico truncado", body);
        Assert.True(body.Length < 2000);
    }

    [Fact]
    public async Task SaveLocalCopyAsync_WritesFileWithTitleAndDescription()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var c = new MailtoBugReportComposer(new FakeDiagnostics("D"), dir);
        string path = await c.SaveLocalCopyAsync(new BugReport("meu-titulo", "meu-corpo", IncludeDiagnostics: true));
        Assert.True(File.Exists(path));
        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("meu-titulo", content);
        Assert.Contains("meu-corpo", content);
    }
}
