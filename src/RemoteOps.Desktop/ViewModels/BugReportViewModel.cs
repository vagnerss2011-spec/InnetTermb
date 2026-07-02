using System;
using System.Diagnostics;
using System.Threading.Tasks;
using RemoteOps.Desktop.Reporting;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>Aba "Reportar problema": compõe o report e abre um e-mail pré-preenchido ao suporte.</summary>
public sealed class BugReportViewModel : BaseViewModel
{
    private readonly IBugReportComposer _composer;
    private readonly Action<Uri> _openMailto;
    private readonly Action<string> _copyToClipboard;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _previewText = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _includeDiagnostics = true;

    public BugReportViewModel(
        IBugReportComposer composer,
        Action<Uri>? openMailto = null,
        Action<string>? copyToClipboard = null)
    {
        _composer = composer;
        // AbsoluteUri preserva o percent-encoding do mailto (ToString() decodifica e quebraria o cliente).
        _openMailto = openMailto ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        _copyToClipboard = copyToClipboard ?? (text => System.Windows.Clipboard.SetText(text));
        SubmitCommand = new RelayCommand(() => _ = SubmitForTestAsync(), CanSubmit);
        PreviewCommand = new RelayCommand(RefreshPreview);
        CopyCommand = new RelayCommand(() =>
        {
            RefreshPreview();
            _copyToClipboard(PreviewText);
            StatusMessage = "Copiado para a área de transferência.";
        });
    }

    public string Title
    {
        get => _title;
        set { Set(ref _title, value); SubmitCommand.RaiseCanExecuteChanged(); }
    }

    public string Description
    {
        get => _description;
        set { Set(ref _description, value); SubmitCommand.RaiseCanExecuteChanged(); }
    }

    public bool IncludeDiagnostics { get => _includeDiagnostics; set => Set(ref _includeDiagnostics, value); }
    public string PreviewText { get => _previewText; private set => Set(ref _previewText, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    public RelayCommand SubmitCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand CopyCommand { get; }

    private bool CanSubmit() => !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Description);
    private BugReport Report => new(Title.Trim(), Description.Trim(), IncludeDiagnostics);
    private void RefreshPreview() => PreviewText = _composer.BuildPreview(Report);

    /// <summary>Salva a cópia local e abre o mailto. Público para teste; a UI chama via SubmitCommand.</summary>
    public async Task SubmitForTestAsync()
    {
        if (!CanSubmit())
        {
            return;
        }

        BugReport report = Report;
        try
        {
            await _composer.SaveLocalCopyAsync(report);
        }
        catch (Exception)
        {
            // Cópia local é best-effort; o e-mail ainda pode ser enviado.
        }

        try
        {
            _openMailto(_composer.BuildMailtoUri(report));
            StatusMessage = "Abrindo seu e-mail…";
        }
        catch (Exception)
        {
            StatusMessage = "Não foi possível abrir o e-mail. Use “Copiar” e cole no seu cliente.";
        }
    }
}
